using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
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

    public MainWindow()
    {
        InitializeComponent();
        _archiveUploadQueue = new ArchiveUploadQueue((text, error) =>
            Dispatch(() => SetArchiveStatus(text, error)));
        Closed += (_, _) => _archiveUploadQueue?.Dispose();
        LoadArchiveSettingsIntoUi();
        RefreshAccountStatus();
        Loaded += (_, _) => MaybeShowFirstLaunchPrompt();
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
            defaultName = $"{FilenamePrefix(avatarMode, parsedMode)}{worldId}.kgmap";
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
        if (ShouldSkipArchiveExisting())
        {
            SetStatus("Checking archive.org...");
            if (await ShouldSkipRemoteArchiveFileAsync(Path.GetFileName(outPath), enabled: true, CancellationToken.None))
            {
                SetStatus($"{Path.GetFileName(outPath)} is already on archive.org, skipped.");
                return;
            }
        }

        _activeCts = new CancellationTokenSource();
        SetBusy(true);
        SetStatus("Connecting...");
        try
        {
            var meta = new KgmapMetadata(
                GameId: worldId,
                OwnerProfileId: parsedOwnerId,
                Region: RegionTag(parsedRegion));
            await SaveMapAsync(url, outPath, avatarMode, metadata: meta, ct: _activeCts.Token);
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

    // Filename naming convention: play sessions get no prefix; build/avatar
    // sessions get a prefix so the same game id can coexist as multiple files
    // in one folder without clobbering each other.
    private static string FilenamePrefix(bool avatarMode, GameMode urlMode)
    {
        if (avatarMode) return "avatar_";
        if (urlMode == GameMode.Build) return "build_";
        return "";
    }

    private static string RegionTag(KogamaScripts.KogamaRegion r) => r switch
    {
        KogamaScripts.KogamaRegion.Br      => "br",
        KogamaScripts.KogamaRegion.Friends => "friends",
        _                                  => "www",
    };

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
        bool asGuest = GuestModeBox.IsChecked == true;
        _activeCts = new CancellationTokenSource();
        var ct = _activeCts.Token;
        SetBusy(true);
        SetStatus($"Listing games for profile {profileId}...");

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
            int started = 0;
            int running = 0;

            await Parallel.ForEachAsync(
                games,
                new ParallelOptions { MaxDegreeOfParallelism = botCount, CancellationToken = ct },
                async (game, token) =>
                {
                    int idx = Interlocked.Increment(ref started);
                    string fileName = $"{FilenamePrefix(avatarMode, GameMode.Play)}{game.Id}.kgmap";
                    string outPath = Path.Combine(outDir, fileName);
                    if (skipExisting && File.Exists(outPath))
                    {
                        Interlocked.Increment(ref skipped);
                        Dispatch(() => SetStatus($"[{idx}/{total}] {game.Id} '{game.Name}' - already saved, skipped."));
                        return;
                    }

                    if (await ShouldSkipRemoteArchiveFileAsync(fileName, skipArchiveExisting, token))
                    {
                        Interlocked.Increment(ref skipped);
                        Dispatch(() => SetStatus($"[{idx}/{total}] {game.Id} '{game.Name}' - already on archive.org, skipped."));
                        return;
                    }

                    string gameUrl = $"https://{regionHost}/games/play/{game.Id}/";
                    int active = Interlocked.Increment(ref running);
                    Dispatch(() => SetStatus($"[{idx}/{total}] Connecting to {game.Id} '{game.Name}'... ({active} bot{(active == 1 ? "" : "s")} running)"));
                    try
                    {
                        const int corruptionRetries = 3;
                        ServerLogicDisconnectException? lastDisconnect = null;
                        bool ok = false;
                        for (int attempt = 1; attempt <= corruptionRetries; attempt++)
                        {
                            token.ThrowIfCancellationRequested();
                            int attemptCopy = attempt;
                            Action<string> onStatus = status => Dispatch(() => SetStatus($"[{idx}/{total}] {game.Id} '{game.Name}'{(attemptCopy > 1 ? $" (try {attemptCopy})" : "")}: {status}"));
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
                            catch (ServerLogicDisconnectException ex)
                            {
                                lastDisconnect = ex;
                                if (attempt < corruptionRetries)
                                {
                                    Dispatch(() => SetStatus($"[{idx}/{total}] {game.Id} '{game.Name}': DisconnectedByServerLogic, retry {attempt + 1}/{corruptionRetries}..."));
                                    await Task.Delay(750, token);
                                }
                            }
                        }

                        if (ok)
                        {
                            Interlocked.Increment(ref saved);
                        }
                        else
                        {
                            Interlocked.Increment(ref skipped);
                            Dispatch(() => SetStatus($"[{idx}/{total}] {game.Id} '{game.Name}': skipped after {corruptionRetries} DisconnectedByServerLogic attempts (world is corrupted)."));
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        Dispatch(() => SetStatus($"[{idx}/{total}] {game.Id} failed: {ex.Message}", error: true));
                        await Task.Delay(500);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref running);
                    }
                });

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
            int started = 0;
            int running = 0;

            await Parallel.ForEachAsync(
                games,
                new ParallelOptions { MaxDegreeOfParallelism = botCount, CancellationToken = ct },
                async (game, token) =>
                {
                    int idx = Interlocked.Increment(ref started);
                    string fileName = $"{FilenamePrefix(avatarMode, GameMode.Play)}{game.Id}.kgmap";
                    string outPath = Path.Combine(outDir, fileName);
                    if (skipExisting && File.Exists(outPath))
                    {
                        Interlocked.Increment(ref skipped);
                        Dispatch(() => SetStatus($"[{idx}/{total}] {game.Id} '{game.Name}' - already saved, skipped."));
                        return;
                    }

                    if (await ShouldSkipRemoteArchiveFileAsync(fileName, skipArchiveExisting, token))
                    {
                        Interlocked.Increment(ref skipped);
                        Dispatch(() => SetStatus($"[{idx}/{total}] {game.Id} '{game.Name}' - already on archive.org, skipped."));
                        return;
                    }

                    string gameUrl = $"https://{regionHost}/games/play/{game.Id}/";
                    int active = Interlocked.Increment(ref running);
                    Dispatch(() => SetStatus($"[{idx}/{total}] Connecting to {game.Id} '{game.Name}'... ({active} bot{(active == 1 ? "" : "s")} running)"));
                    try
                    {
                        const int corruptionRetries = 3;
                        bool ok = false;
                        for (int attempt = 1; attempt <= corruptionRetries; attempt++)
                        {
                            token.ThrowIfCancellationRequested();
                            int attemptCopy = attempt;
                            Action<string> onStatus = status => Dispatch(() => SetStatus($"[{idx}/{total}] {game.Id} '{game.Name}'{(attemptCopy > 1 ? $" (try {attemptCopy})" : "")}: {status}"));
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
                                    Dispatch(() => SetStatus($"[{idx}/{total}] {game.Id} '{game.Name}': DisconnectedByServerLogic, retry {attempt + 1}/{corruptionRetries}..."));
                                    await Task.Delay(750, token);
                                }
                            }
                        }

                        if (ok) Interlocked.Increment(ref saved);
                        else
                        {
                            Interlocked.Increment(ref skipped);
                            Dispatch(() => SetStatus($"[{idx}/{total}] {game.Id} '{game.Name}': skipped after {corruptionRetries} DisconnectedByServerLogic attempts (world is corrupted)."));
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        Dispatch(() => SetStatus($"[{idx}/{total}] {game.Id} failed: {ex.Message}", error: true));
                        await Task.Delay(500);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref running);
                    }
                });

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
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var game = games[i];
                string fileName = $"{FilenamePrefix(avatarMode, GameMode.Build)}{game.Id}.kgmap";
                string outPath = Path.Combine(outDir, fileName);
                if (skipExisting && File.Exists(outPath))
                {
                    skipped++;
                    SetStatus($"[{i + 1}/{total}] {game.Id} '{game.Name}' - already saved, skipped.");
                    continue;
                }

                if (await ShouldSkipRemoteArchiveFileAsync(fileName, skipArchiveExisting, ct))
                {
                    skipped++;
                    SetStatus($"[{i + 1}/{total}] {game.Id} '{game.Name}' - already on archive.org, skipped.");
                    continue;
                }

                string buildUrl = region switch
                {
                    KogamaScripts.KogamaRegion.Br      => $"https://kogama.com.br/build/{session.ProfileId}/project/{game.Id}/",
                    KogamaScripts.KogamaRegion.Friends => $"https://friends.kogama.com/build/{session.ProfileId}/project/{game.Id}/",
                    _                                  => $"https://www.kogama.com/build/{session.ProfileId}/project/{game.Id}/",
                };

                SetStatus($"[{i + 1}/{total}] Connecting to {game.Id} '{game.Name}'...");
                try
                {
                    var meta = new KgmapMetadata(
                        GameId: game.Id.ToString(),
                        GameTitle: game.Name,
                        OwnerProfileId: session.ProfileId,
                        OwnerUsername: session.Username,
                        Region: RegionTag(region));
                    await SaveMapAsync(buildUrl, outPath, avatarMode, status =>
                        SetStatus($"[{i + 1}/{total}] {game.Id} '{game.Name}': {status}"), metadata: meta, ct: ct);
                    saved++;
                }
                catch (ServerLogicDisconnectException)
                {
                    skipped++;
                    SetStatus($"[{i + 1}/{total}] {game.Id} '{game.Name}': skipped (DisconnectedByServerLogic).");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    SetStatus($"[{i + 1}/{total}] {game.Id} failed: {ex.Message}", error: true);
                    await Task.Delay(500, ct);
                }
            }

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

    private int GetBotCount()
    {
        if (int.TryParse(BotCountBox.Text?.Trim(), out int n) && n > 0)
            return n;
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
        WorldSession? ws = await WorldOpener.OpenAsync(url, forceMode, forceSession, asGuest)
            .WaitAsync(TimeSpan.FromSeconds(20), ct);
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
                readyTimeout: TimeSpan.FromSeconds(30),
                quietFor: TimeSpan.FromSeconds(5),
                quietTimeout: TimeSpan.FromSeconds(20),
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
