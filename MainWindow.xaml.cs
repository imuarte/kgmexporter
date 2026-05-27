using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using KogamaScripts;
using Microsoft.Win32;

namespace KgmExporter;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _activeCts;
    private bool _busy;
    private bool _loadingArchiveUi;
    private ArchiveUploadQueue? _archiveUploadQueue;
    private readonly ObservableCollection<BotSlotViewModel> _botSlots = new();

    public MainWindow()
    {
        InitializeComponent();
        BotSlotsPanel.ItemsSource = _botSlots;
        _archiveUploadQueue = new ArchiveUploadQueue((text, error) =>
            Dispatch(() => SetArchiveStatus(text, error)));
        Closing += MainWindow_Closing;
        Closed += (_, _) => _archiveUploadQueue?.Dispose();
        LoadArchiveSettingsIntoUi();
        RefreshAccountStatus();
        Loaded += (_, _) => MaybeShowFirstLaunchPrompt();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        int pending = _archiveUploadQueue?.PendingCount ?? 0;
        if (pending <= 0) return;

        var result = MessageBox.Show(
            this,
            $"{pending} archive.org upload{(pending == 1 ? "" : "s")} still in flight.\n\nQuit anyway and lose them?",
            "Uploads in progress",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
            e.Cancel = true;
    }

    private void MaybeShowFirstLaunchPrompt()
    {
        var settings = LocalSettings.Load();
        if (settings.ArchiveAutoUpload.HasValue) return;

        var dlg = new FirstLaunchWindow { Owner = this };
        dlg.ShowDialog();
        settings = LocalSettings.Load() with { ArchiveAutoUpload = dlg.OptedIn };
        LocalSettings.Save(settings);
        SetArchiveAutoUploadChecked(dlg.OptedIn);
    }

    private void RefreshAccountStatus()
    {
        var session = LocalAuth.LoadSession();
        AccountBtn.Content = session != null ? session.Username : "Sign in";
    }

    private void AccountBtn_Click(object sender, RoutedEventArgs e)
    {
        var session = LocalAuth.LoadSession();
        if (session != null)
        {
            LocalAuth.ClearSession();
            RefreshAccountStatus();
            return;
        }

        var dlg = new LoginWindow { Owner = this };
        if (dlg.ShowDialog() == true)
            RefreshAccountStatus();
    }

    private async void SaveBtn_Click(object sender, RoutedEventArgs e)
        => await RunSaveAsync(forceAvatarMode: false);

    private async void SaveAvatarBtn_Click(object sender, RoutedEventArgs e)
        => await RunSaveAsync(forceAvatarMode: true);

    private async Task RunSaveAsync(bool forceAvatarMode)
    {
        if (_busy)
        {
            _activeCts?.Cancel();
            SetStatus("Cancelling...");
            SaveBtn.IsEnabled = false;
            return;
        }

        string url = UrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            SetStatus("Paste a Kogama URL.", error: true);
            return;
        }

        bool avatarMode = forceAvatarMode;
        if (avatarMode && LocalAuth.LoadSession() == null)
        {
            SetStatus("Sign in first to use avatar mode.", error: true);
            return;
        }

        if (UrlParser.TryParseProfile(url, out int profileId, out var profileRegion))
        {
            await SaveProfileAsync(profileId, profileRegion, avatarMode);
            return;
        }

        if (UrlParser.IsBuildRoot(url, out var buildRegion))
        {
            if (LocalAuth.LoadSession() == null)
            {
                SetStatus("Sign in first to download your own games.", error: true);
                return;
            }
            await SaveMyGamesAsync(buildRegion, avatarMode);
            return;
        }

        if (UrlParser.IsGamesListing(url, out var listingRegion))
        {
            await SavePublicGamesAsync(listingRegion, avatarMode);
            return;
        }

        string defaultName = "world.kgmap";
        int parsedOwnerId = 0;
        KogamaScripts.KogamaRegion parsedRegion = KogamaScripts.KogamaRegion.Www;
        GameMode parsedMode = GameMode.Play;
        if (UrlParser.TryParse(url, out string? worldId, out parsedOwnerId, out parsedRegion, out parsedMode, out _)
            && !string.IsNullOrWhiteSpace(worldId))
        {
            defaultName = BuildFileName(avatarMode, parsedMode, worldId);
        }

        var dlg = new SaveFileDialog
        {
            FileName = defaultName,
            Filter = "Kogama map (*.kgmap)|*.kgmap",
            DefaultExt = ".kgmap"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        string outPath = dlg.FileName;
        var saveCts = new CancellationTokenSource();
        _activeCts = saveCts;
        SetBusy(true);

        if (ShouldSkipArchiveExisting())
        {
            SetStatus("Checking archive.org...");
            try
            {
                using var preflightCts = CancellationTokenSource.CreateLinkedTokenSource(_activeCts.Token);
                preflightCts.CancelAfter(TimeSpan.FromSeconds(8));
                if (await ShouldSkipRemoteArchiveFileAsync(Path.GetFileName(outPath), enabled: true, preflightCts.Token))
                {
                    SetStatus($"{Path.GetFileName(outPath)} is already on archive.org, skipped.");
                    _activeCts?.Dispose();
                    _activeCts = null;
                    SetBusy(false);
                    return;
                }
            }
            catch (OperationCanceledException) when (saveCts.IsCancellationRequested)
            {
                SetStatus("Cancelled.");
                _activeCts?.Dispose();
                _activeCts = null;
                SetBusy(false);
                return;
            }
            catch (OperationCanceledException)
            {
                // Pre-flight timed out; fall through to actually try the save.
            }
        }

        SetStatus("Connecting...");
        try
        {
            var meta = new KgmapMetadata(
                GameId: worldId,
                OwnerProfileId: parsedOwnerId,
                Region: RegionTag(parsedRegion));
            await SaveMapAsync(url, outPath, avatarMode, metadata: meta, ct: saveCts.Token);
            var fi = new FileInfo(outPath);
            SetStatus($"Saved {fi.Length / 1024.0:N1} KB to {outPath}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed: {ex.Message}", error: true);
        }
        finally
        {
            _activeCts?.Dispose();
            _activeCts = null;
            SetBusy(false);
        }
    }

    // Filename convention:
    //   play  → "<gameId>.kgmap"
    //   build → "build_<gameId>.kgmap"
    //   avatar → "avatar_<myProfileId>.kgmap"  (gameId is irrelevant - the
    //            avatar is yours and the iterated game just gives us a world
    //            to connect through; we want one file per identity, not per game)
    private static string BuildFileName(bool avatarMode, GameMode urlMode, string gameId)
    {
        if (avatarMode)
        {
            int profileId = LocalAuth.LoadSession()?.ProfileId ?? 0;
            return $"avatar_{profileId}.kgmap";
        }
        if (urlMode == GameMode.Build) return $"build_{gameId}.kgmap";
        return $"{gameId}.kgmap";
    }

    private static string RegionTag(KogamaScripts.KogamaRegion r) => r switch
    {
        KogamaScripts.KogamaRegion.Br      => "br",
        KogamaScripts.KogamaRegion.Friends => "friends",
        _                                  => "www",
    };

    private static string Truncate(string? value, int max = 24)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }

    private IDisposable StartBatchSummaryTimer(string label, int total, Func<(int saved, int skipped, int failed)> read)
    {
        var timer = new System.Threading.Timer(_ =>
        {
            var (saved, skipped, failed) = read();
            int done = saved + skipped + failed;
            string text = $"{label}: {done}/{total} done  saved={saved}  skipped={skipped}  failed={failed}";
            Dispatch(() => SetStatus(text));
        }, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        return timer;
    }

    private void InitBotSlots(int count)
    {
        Dispatch(() =>
        {
            _botSlots.Clear();
            for (int i = 0; i < count; i++)
                _botSlots.Add(new BotSlotViewModel(i));
            BotSlotsPanel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void ClearBotSlots()
    {
        Dispatch(() =>
        {
            _botSlots.Clear();
            BotSlotsPanel.Visibility = Visibility.Collapsed;
        });
    }

    private void UpdateBotSlot(int slot, string text)
    {
        Dispatch(() =>
        {
            if (slot >= 0 && slot < _botSlots.Count)
                _botSlots[slot].Text = $"[bot {slot + 1}] {text}";
        });
    }

    // Channel + N worker pattern. Each worker owns a fixed slot index so its
    // status text never collides with another bot's. The channel guarantees
    // each input item is delivered to exactly one worker, so two bots can
    // never end up processing the same game id even if the input list had dupes.
    private async Task RunWithBotSlotsAsync<T>(
        int botCount,
        IReadOnlyList<T> items,
        Func<T, int, CancellationToken, Task> processItem,
        CancellationToken ct)
    {
        InitBotSlots(botCount);
        try
        {
            var channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
            {
                SingleWriter = true,
                SingleReader = false,
            });
            foreach (var item in items)
                channel.Writer.TryWrite(item);
            channel.Writer.Complete();

            var tasks = Enumerable.Range(0, botCount).Select(slot =>
                Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var item in channel.Reader.ReadAllAsync(ct))
                            await processItem(item, slot, ct);
                    }
                    catch (OperationCanceledException) { }
                }, ct)
            ).ToArray();

            await Task.WhenAll(tasks);
        }
        finally
        {
            ClearBotSlots();
        }
    }

    private async Task SaveProfileAsync(int profileId, KogamaScripts.KogamaRegion region, bool avatarMode)
    {
        var folderDlg = new OpenFolderDialog
        {
            Title = $"Pick output folder for profile {profileId}"
        };
        if (folderDlg.ShowDialog(this) != true)
            return;

        string outDir = folderDlg.FolderName;
        int botCount = GetBotCount();
        bool skipExisting = SkipExistingBox.IsChecked == true;
        bool skipArchiveExisting = ShouldSkipArchiveExisting();
        // Own profile must use logged-in cookies so private/build-mode games are reachable.
        var ownSession = LocalAuth.LoadSession();
        bool isOwnProfile = ownSession != null && ownSession.ProfileId == profileId;
        bool asGuest = !isOwnProfile && GuestModeBox.IsChecked == true;
        _activeCts = new CancellationTokenSource();
        var ct = _activeCts.Token;
        SetBusy(true);
        SetStatus(isOwnProfile
            ? $"Listing your games (profile {profileId})..."
            : $"Listing games for profile {profileId}...");

        if (asGuest) Auth.ClearCookies();
        else LocalAuth.LoadCookies();

        int saved = 0, failed = 0, skipped = 0;
        try
        {
            var games = await KogamaScripts.WebApi.GetUserGamesAsync(profileId, region);
            if (games.Count == 0)
            {
                SetStatus($"Profile {profileId} has no games (or none are public).", error: true);
                return;
            }

            string regionHost = region switch
            {
                KogamaScripts.KogamaRegion.Br      => "kogama.com.br",
                KogamaScripts.KogamaRegion.Friends => "friends.kogama.com",
                _                                  => "www.kogama.com",
            };

            int total = games.Count;

            using var summaryTimer = StartBatchSummaryTimer(
                $"Profile {profileId}",
                total,
                () => (Volatile.Read(ref saved), Volatile.Read(ref skipped), Volatile.Read(ref failed)));

            await RunWithBotSlotsAsync(botCount, games, async (game, slot, token) =>
            {
                string fileName = BuildFileName(avatarMode, GameMode.Play, game.Id.ToString());
                string outPath = Path.Combine(outDir, fileName);
                if (skipExisting && File.Exists(outPath))
                {
                    Interlocked.Increment(ref skipped);
                    UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - already in folder");
                    return;
                }

                if (await ShouldSkipRemoteArchiveFileAsync(fileName, skipArchiveExisting, token))
                {
                    Interlocked.Increment(ref skipped);
                    UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - already on archive.org");
                    return;
                }

                string gameUrl = $"https://{regionHost}/games/play/{game.Id}/";
                UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - connecting...");
                try
                {
                    const int corruptionRetries = 3;
                    bool ok = false;
                    for (int attempt = 1; attempt <= corruptionRetries; attempt++)
                    {
                        token.ThrowIfCancellationRequested();
                        int attemptCopy = attempt;
                        Action<string> onStatus = status =>
                            UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}'{(attemptCopy > 1 ? $" (try {attemptCopy})" : "")}: {status}");
                        try
                        {
                            var meta = new KgmapMetadata(
                                GameId: game.Id.ToString(),
                                GameTitle: game.Name,
                                OwnerProfileId: profileId,
                                Region: RegionTag(region));
                            await SaveMapAsync(gameUrl, outPath, avatarMode: avatarMode, onStatus, asGuest: asGuest && !avatarMode, metadata: meta, ct: token);
                            ok = true;
                            break;
                        }
                        catch (ServerLogicDisconnectException)
                        {
                            if (attempt < corruptionRetries)
                            {
                                UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - disconnected, retry {attempt + 1}/{corruptionRetries}...");
                                await Task.Delay(750, token);
                            }
                        }
                    }

                    if (ok)
                    {
                        Interlocked.Increment(ref saved);
                        UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - saved");
                    }
                    else
                    {
                        Interlocked.Increment(ref skipped);
                        UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - corrupted, skipped");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - failed: {Truncate(ex.Message, 40)}");
                    await Task.Delay(500, token);
                }
            }, ct);

            string summary = $"Profile {profileId}: {saved} saved";
            if (skipped > 0) summary += $", {skipped} skipped";
            if (failed > 0) summary += $", {failed} failed";
            summary += $" (of {games.Count}) -> {outDir}";
            SetStatus(summary, error: failed > 0 && saved == 0);
        }
        catch (OperationCanceledException)
        {
            string s = $"Cancelled. Profile {profileId}: {saved} saved";
            if (skipped > 0) s += $", {skipped} skipped";
            if (failed > 0) s += $", {failed} failed";
            SetStatus(s);
        }
        catch (Exception ex)
        {
            SetStatus($"Profile listing failed: {ex.Message}", error: true);
        }
        finally
        {
            Auth.ClearCookies();
            LocalAuth.LoadCookies();
            _activeCts?.Dispose();
            _activeCts = null;
            SetBusy(false);
        }
    }

    private async Task SavePublicGamesAsync(KogamaScripts.KogamaRegion region, bool avatarMode)
    {
        var folderDlg = new OpenFolderDialog
        {
            Title = "Pick output folder for public games"
        };
        if (folderDlg.ShowDialog(this) != true)
            return;

        string outDir = folderDlg.FolderName;
        int botCount = GetBotCount();
        bool skipExisting = SkipExistingBox.IsChecked == true;
        bool skipArchiveExisting = ShouldSkipArchiveExisting();
        bool asGuest = GuestModeBox.IsChecked == true;
        _activeCts = new CancellationTokenSource();
        var ct = _activeCts.Token;
        SetBusy(true);
        SetStatus("Listing public games...");

        if (asGuest) Auth.ClearCookies();
        else LocalAuth.LoadCookies();

        int saved = 0, failed = 0, skipped = 0;
        try
        {
            var games = await KogamaScripts.WebApi.GetPublicGamesAsync(
                region,
                onProgress: msg => Dispatch(() => SetStatus(msg)),
                ct: ct);
            if (games.Count == 0)
            {
                SetStatus("No public games returned.", error: true);
                return;
            }
            SetStatus($"Listed {games.Count} public games, starting downloads...");

            string regionHost = region switch
            {
                KogamaScripts.KogamaRegion.Br      => "kogama.com.br",
                KogamaScripts.KogamaRegion.Friends => "friends.kogama.com",
                _                                  => "www.kogama.com",
            };

            int total = games.Count;

            using var summaryTimer = StartBatchSummaryTimer(
                "Public games",
                total,
                () => (Volatile.Read(ref saved), Volatile.Read(ref skipped), Volatile.Read(ref failed)));

            await RunWithBotSlotsAsync(botCount, games, async (game, slot, token) =>
            {
                string fileName = BuildFileName(avatarMode, GameMode.Play, game.Id.ToString());
                string outPath = Path.Combine(outDir, fileName);
                if (skipExisting && File.Exists(outPath))
                {
                    Interlocked.Increment(ref skipped);
                    UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - already in folder");
                    return;
                }

                if (await ShouldSkipRemoteArchiveFileAsync(fileName, skipArchiveExisting, token))
                {
                    Interlocked.Increment(ref skipped);
                    UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - already on archive.org");
                    return;
                }

                string gameUrl = $"https://{regionHost}/games/play/{game.Id}/";
                UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - connecting...");
                try
                {
                    const int corruptionRetries = 3;
                    bool ok = false;
                    for (int attempt = 1; attempt <= corruptionRetries; attempt++)
                    {
                        token.ThrowIfCancellationRequested();
                        int attemptCopy = attempt;
                        Action<string> onStatus = status =>
                            UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}'{(attemptCopy > 1 ? $" (try {attemptCopy})" : "")}: {status}");
                        try
                        {
                            var meta = new KgmapMetadata(
                                GameId: game.Id.ToString(),
                                GameTitle: game.Name,
                                OwnerProfileId: game.OwnerId,
                                OwnerUsername: game.OwnerName,
                                Region: RegionTag(region));
                            await SaveMapAsync(gameUrl, outPath, avatarMode: avatarMode, onStatus, asGuest: asGuest && !avatarMode, metadata: meta, ct: token);
                            ok = true;
                            break;
                        }
                        catch (ServerLogicDisconnectException)
                        {
                            if (attempt < corruptionRetries)
                            {
                                UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - disconnected, retry {attempt + 1}/{corruptionRetries}...");
                                await Task.Delay(750, token);
                            }
                        }
                    }

                    if (ok)
                    {
                        Interlocked.Increment(ref saved);
                        UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - saved");
                    }
                    else
                    {
                        Interlocked.Increment(ref skipped);
                        UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - corrupted, skipped");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - failed: {Truncate(ex.Message, 40)}");
                    await Task.Delay(500, token);
                }
            }, ct);

            string summary = $"Public games: {saved} saved";
            if (skipped > 0) summary += $", {skipped} skipped";
            if (failed > 0) summary += $", {failed} failed";
            summary += $" (of {total}) -> {outDir}";
            SetStatus(summary, error: failed > 0 && saved == 0);
        }
        catch (OperationCanceledException)
        {
            string s = $"Cancelled. Public games: {saved} saved";
            if (skipped > 0) s += $", {skipped} skipped";
            if (failed > 0) s += $", {failed} failed";
            SetStatus(s);
        }
        catch (Exception ex)
        {
            SetStatus($"Public listing failed: {ex.Message}", error: true);
        }
        finally
        {
            Auth.ClearCookies();
            LocalAuth.LoadCookies();
            _activeCts?.Dispose();
            _activeCts = null;
            SetBusy(false);
        }
    }

    private async Task SaveMyGamesAsync(KogamaScripts.KogamaRegion region, bool avatarMode)
    {
        var session = LocalAuth.LoadSession();
        if (session == null)
        {
            SetStatus("Sign in first to download your own games.", error: true);
            return;
        }

        var folderDlg = new OpenFolderDialog
        {
            Title = $"Pick output folder for {session.Username}'s games"
        };
        if (folderDlg.ShowDialog(this) != true)
            return;

        string outDir = folderDlg.FolderName;
        int botCount = GetBotCount();
        bool skipExisting = SkipExistingBox.IsChecked == true;
        bool skipArchiveExisting = ShouldSkipArchiveExisting();
        _activeCts = new CancellationTokenSource();
        var ct = _activeCts.Token;
        SetBusy(true);
        SetStatus("Listing your games...");

        LocalAuth.LoadCookies();

        int saved = 0, failed = 0, skipped = 0;
        try
        {
            var games = await KogamaScripts.WebApi.GetMyGamesAsync(region);
            if (games.Count == 0)
            {
                SetStatus("No games found on your account.", error: true);
                return;
            }

            int total = games.Count;

            using var summaryTimer = StartBatchSummaryTimer(
                session.Username,
                total,
                () => (Volatile.Read(ref saved), Volatile.Read(ref skipped), Volatile.Read(ref failed)));

            string regionHost = region switch
            {
                KogamaScripts.KogamaRegion.Br      => "kogama.com.br",
                KogamaScripts.KogamaRegion.Friends => "friends.kogama.com",
                _                                  => "www.kogama.com",
            };

            await RunWithBotSlotsAsync(botCount, games, async (game, slot, token) =>
            {
                string fileName = BuildFileName(avatarMode, GameMode.Build, game.Id.ToString());
                string outPath = Path.Combine(outDir, fileName);
                if (skipExisting && File.Exists(outPath))
                {
                    Interlocked.Increment(ref skipped);
                    UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - already in folder");
                    return;
                }

                if (await ShouldSkipRemoteArchiveFileAsync(fileName, skipArchiveExisting, token))
                {
                    Interlocked.Increment(ref skipped);
                    UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - already on archive.org");
                    return;
                }

                string buildUrl = $"https://{regionHost}/build/{session.ProfileId}/project/{game.Id}/";
                UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - connecting...");
                try
                {
                    var meta = new KgmapMetadata(
                        GameId: game.Id.ToString(),
                        GameTitle: game.Name,
                        OwnerProfileId: session.ProfileId,
                        OwnerUsername: session.Username,
                        Region: RegionTag(region));
                    Action<string> onStatus = status =>
                        UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}': {status}");
                    await SaveMapAsync(buildUrl, outPath, avatarMode, onStatus, metadata: meta, ct: token);
                    Interlocked.Increment(ref saved);
                    UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - saved");
                }
                catch (ServerLogicDisconnectException)
                {
                    Interlocked.Increment(ref skipped);
                    UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - skipped (DisconnectedByServerLogic)");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    UpdateBotSlot(slot, $"{game.Id} '{Truncate(game.Name)}' - failed: {Truncate(ex.Message, 40)}");
                    await Task.Delay(500, token);
                }
            }, ct);

            string summary = $"{session.Username}: {saved} saved";
            if (skipped > 0) summary += $", {skipped} skipped";
            if (failed > 0) summary += $", {failed} failed";
            summary += $" (of {total}) -> {outDir}";
            SetStatus(summary, error: failed > 0 && saved == 0);
        }
        catch (OperationCanceledException)
        {
            string s = $"Cancelled. {session.Username}: {saved} saved";
            if (skipped > 0) s += $", {skipped} skipped";
            if (failed > 0) s += $", {failed} failed";
            SetStatus(s);
        }
        catch (Exception ex)
        {
            SetStatus($"My-games listing failed: {ex.Message}", error: true);
        }
        finally
        {
            _activeCts?.Dispose();
            _activeCts = null;
            SetBusy(false);
        }
    }

    private const int MaxBotCount = 32;

    private int GetBotCount()
    {
        if (int.TryParse(BotCountBox.Text?.Trim(), out int n) && n > 0)
            return Math.Min(n, MaxBotCount);
        return 1;
    }

    private bool ShouldSkipArchiveExisting()
    {
        var settings = LocalSettings.Load();
        return settings.ArchiveAutoUpload == true && settings.ArchiveSkipDuplicates != false;
    }

    private async Task<bool> ShouldSkipRemoteArchiveFileAsync(string fileName, bool enabled, CancellationToken ct)
    {
        if (!enabled)
            return false;

        return await ArchiveUploader.RemoteFileExistsAsync(fileName, ct);
    }

    private void BrowseKgmapBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Kogama map (*.kgmap)|*.kgmap|All files (*.*)|*.*",
            DefaultExt = ".kgmap"
        };
        if (dlg.ShowDialog(this) == true)
            KgmapPathBox.Text = dlg.FileName;
    }

    private async void ConvertBtn_Click(object sender, RoutedEventArgs e)
    {
        string kgmapPath = KgmapPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(kgmapPath) || !File.Exists(kgmapPath))
        {
            SetStatus("Pick a valid .kgmap file.", error: true);
            return;
        }

        var dlg = new SaveFileDialog
        {
            FileName = Path.GetFileNameWithoutExtension(kgmapPath) + ".obj",
            Filter = "Wavefront OBJ (*.obj)|*.obj",
            DefaultExt = ".obj"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        string outPath = dlg.FileName;
        SetBusy(true);
        SetStatus("Converting...");
        try
        {
            int written = await Task.Run(() => KgmapToObj.Convert(kgmapPath, outPath));
            if (written == 0)
                throw new InvalidOperationException("No geometry written.");
            var fi = new FileInfo(outPath);
            SetStatus($"Saved {fi.Length / 1024.0:N1} KB to {outPath}");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed: {ex.Message}", error: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SaveMapAsync(string url, string outPath, bool avatarMode, Action<string>? onStatus = null, bool asGuest = false, KgmapMetadata? metadata = null, CancellationToken ct = default)
    {
        GameMode? forceMode = avatarMode ? GameMode.AvatarEdit : null;
        SessionType? forceSession = avatarMode ? SessionType.Character : null;

        ct.ThrowIfCancellationRequested();
        WorldSession? ws = await WorldOpener.OpenAsync(url, forceMode, forceSession, asGuest).WaitAsync(ct);
        if (ws == null)
            throw new InvalidOperationException("Invalid Kogama URL. Use /games/play/<id> or /build/<owner>/project/<id>.");

        using var cancelReg = ct.Register(() =>
        {
            try { ws.Client.Disconnect(); } catch { }
        });

        void ReportStatus(string s)
        {
            Dispatch(() =>
            {
                if (onStatus != null) onStatus(s);
                else SetStatus(s);
            });
        }

        // Drain bytes counter to the status label without spamming the UI thread.
        // Use a thread-pool timer (not DispatcherTimer) so it ticks even when
        // SaveMapAsync runs on a Parallel.ForEachAsync worker thread (no UI pump).
        long lastShownKb = -1;
        var progressTimer = new System.Threading.Timer(_ =>
        {
            long kb = ws.RawBytesReceived / 1024;
            if (kb != Interlocked.Read(ref lastShownKb))
            {
                Interlocked.Exchange(ref lastShownKb, kb);
                ReportStatus($"Downloading... {kb:N0} KB");
            }
        }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        bool disconnected = false;

        try
        {
            await ws.WaitForWorldQuietAsync(
                quietFor: TimeSpan.FromSeconds(5),
                ct: ct);

            var batches = ws.SnapshotBatches();
            if (batches.Count == 0)
                throw new InvalidOperationException("World produced no data batches.");

            Dispatch(() => ReportStatus($"Exporting ({batches.Count} batches, {ws.RawBytesReceived / 1024.0:N1} KB)..."));

            int written = await Task.Run(() =>
            {
                using var stream = File.Create(outPath);
                return KgmapExport.WriteBatches(stream, batches, metadata);
            });

            if (written == 0)
                throw new InvalidOperationException("Export produced no batches.");

            progressTimer.Dispose();
            ws.Client.Disconnect();
            disconnected = true;
            QueueArchiveUpload(outPath, metadata);
        }
        finally
        {
            progressTimer.Dispose();
            if (!disconnected)
                ws.Client.Disconnect();
        }
    }

    private void QueueArchiveUpload(string outPath, KgmapMetadata? metadata)
    {
        try
        {
            var settings = LocalSettings.Load();
            if (settings.ArchiveAutoUpload != true)
                return;

            bool skipDuplicates = settings.ArchiveSkipDuplicates != false;
            _archiveUploadQueue?.Enqueue(outPath, metadata, skipDuplicates);
        }
        catch (Exception ex)
        {
            SetArchiveStatus($"Saved locally; archive.org upload could not be queued: {ex.Message}", error: true);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SaveBtn.IsEnabled = true;
        SaveBtn.Content = busy ? "Cancel" : "Save .kgmap";
        UrlBox.IsEnabled = !busy;
        SaveAvatarBtn.IsEnabled = !busy;
        GuestModeBox.IsEnabled = !busy;
        BotCountBox.IsEnabled = !busy;
        SkipExistingBox.IsEnabled = !busy;
        ArchiveAutoUploadBox.IsEnabled = !busy;
        ArchiveDedupBox.IsEnabled = !busy;
        AccountBtn.IsEnabled = !busy;
        ConvertBtn.IsEnabled = !busy;
        KgmapPathBox.IsEnabled = !busy;
        BrowseKgmapBtn.IsEnabled = !busy;
        // Keep Tabs enabled while busy so the Convert tab is still navigable;
        // individual controls inside disable themselves above. Convert flow
        // is not cancellable yet, so block it via ConvertBtn.IsEnabled.
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusLabel.Text = text;
        StatusLabel.Foreground = error
            ? System.Windows.Media.Brushes.Firebrick
            : System.Windows.Media.Brushes.Gray;
    }

    private void SetArchiveStatus(string text, bool error = false)
    {
        ArchiveStatusLabel.Text = text;
        ArchiveStatusLabel.Foreground = error
            ? System.Windows.Media.Brushes.Firebrick
            : System.Windows.Media.Brushes.Gray;
    }

    private void Dispatch(Action a)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        if (Dispatcher.CheckAccess())
            a();
        else
            Dispatcher.Invoke(a);
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
        => UrlPlaceholder.Visibility = string.IsNullOrEmpty(UrlBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void KgmapPathBox_TextChanged(object sender, TextChangedEventArgs e)
        => KgmapPlaceholder.Visibility = string.IsNullOrEmpty(KgmapPathBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void ArchiveAutoUploadBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingArchiveUi)
            return;

        bool enabled = ArchiveAutoUploadBox.IsChecked == true;
        var settings = LocalSettings.Load() with { ArchiveAutoUpload = enabled };
        LocalSettings.Save(settings);
        SetStatus(enabled ? "Archive.org map upload enabled." : "Archive.org upload disabled.");
    }

    private void ArchiveDedupBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingArchiveUi)
            return;

        bool enabled = ArchiveDedupBox.IsChecked == true;
        var settings = LocalSettings.Load() with { ArchiveSkipDuplicates = enabled };
        LocalSettings.Save(settings);
    }

    private void LoadArchiveSettingsIntoUi()
    {
        var settings = LocalSettings.Load();
        SetArchiveAutoUploadChecked(settings.ArchiveAutoUpload == true);
    }

    private void SetArchiveAutoUploadChecked(bool value)
    {
        _loadingArchiveUi = true;
        ArchiveAutoUploadBox.IsChecked = value;
        var settings = LocalSettings.Load();
        ArchiveDedupBox.IsChecked = settings.ArchiveSkipDuplicates != false;
        _loadingArchiveUi = false;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void ArchiveLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = e.Uri.AbsoluteUri,
            UseShellExecute = true,
        });
        e.Handled = true;
    }
}
