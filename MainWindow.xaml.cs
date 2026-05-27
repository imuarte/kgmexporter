using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using KogamaScripts;
using Microsoft.Win32;

namespace KgmExporter;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RefreshAccountStatus();
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
    {
        string url = UrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            SetStatus("Paste a Kogama URL.", error: true);
            return;
        }

        bool avatarMode = AvatarModeBox.IsChecked == true;
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

        string defaultName = "world.kgmap";
        if (UrlParser.TryParse(url, out string? worldId, out _, out _, out _, out _)
            && !string.IsNullOrWhiteSpace(worldId))
        {
            defaultName = $"{worldId}.kgmap";
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
        SetBusy(true);
        SetStatus("Connecting...");
        try
        {
            await SaveMapAsync(url, outPath, avatarMode);
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

    private async Task SaveProfileAsync(int profileId, KogamaScripts.KogamaRegion region, bool avatarMode)
    {
        var folderDlg = new OpenFolderDialog
        {
            Title = $"Pick output folder for profile {profileId}"
        };
        if (folderDlg.ShowDialog(this) != true)
            return;

        string outDir = folderDlg.FolderName;
        SetBusy(true);
        SetStatus($"Listing games for profile {profileId}...");

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

            for (int i = 0; i < games.Count; i++)
            {
                var game = games[i];
                string outPath = Path.Combine(outDir, $"{game.Id}.kgmap");
                if (File.Exists(outPath))
                {
                    skipped++;
                    SetStatus($"[{i + 1}/{games.Count}] {game.Id} '{game.Name}' - already saved, skipped.");
                    continue;
                }

                string gameUrl = $"https://{regionHost}/games/play/{game.Id}/";
                SetStatus($"[{i + 1}/{games.Count}] Connecting to {game.Id} '{game.Name}'...");
                try
                {
                    await SaveMapAsync(gameUrl, outPath, avatarMode, status =>
                        SetStatus($"[{i + 1}/{games.Count}] {game.Id} '{game.Name}': {status}"));
                    saved++;
                }
                catch (ServerLogicDisconnectException)
                {
                    skipped++;
                    SetStatus($"[{i + 1}/{games.Count}] {game.Id} '{game.Name}': skipped (DisconnectedByServerLogic).");
                }
                catch (Exception ex)
                {
                    failed++;
                    SetStatus($"[{i + 1}/{games.Count}] {game.Id} failed: {ex.Message}", error: true);
                    await Task.Delay(500);
                }
            }

            string summary = $"Profile {profileId}: {saved} saved";
            if (skipped > 0) summary += $", {skipped} skipped";
            if (failed > 0) summary += $", {failed} failed";
            summary += $" (of {games.Count}) -> {outDir}";
            SetStatus(summary, error: failed > 0 && saved == 0);
        }
        catch (Exception ex)
        {
            SetStatus($"Profile listing failed: {ex.Message}", error: true);
        }
        finally
        {
            SetBusy(false);
        }
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

    private async Task SaveMapAsync(string url, string outPath, bool avatarMode, Action<string>? onStatus = null)
    {
        GameMode? forceMode = avatarMode ? GameMode.AvatarEdit : null;
        SessionType? forceSession = avatarMode ? SessionType.Character : null;

        WorldSession? ws = await WorldOpener.OpenAsync(url, forceMode, forceSession);
        if (ws == null)
            throw new InvalidOperationException("Invalid Kogama URL. Use /games/play/<id> or /build/<owner>/project/<id>.");

        void ReportStatus(string s)
        {
            if (onStatus != null) onStatus(s);
            else SetStatus(s);
        }

        // Drain bytes counter to the status label without spamming the UI thread.
        long lastShownKb = -1;
        var progressTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        progressTimer.Tick += (_, _) =>
        {
            long kb = ws.RawBytesReceived / 1024;
            if (kb != lastShownKb)
            {
                lastShownKb = kb;
                ReportStatus($"Downloading... {kb:N0} KB");
            }
        };
        progressTimer.Start();

        try
        {
            await ws.WaitForWorldQuietAsync(
                readyTimeout: Timeout.InfiniteTimeSpan,
                quietFor: TimeSpan.FromSeconds(5),
                quietTimeout: TimeSpan.FromSeconds(20));

            var batches = ws.SnapshotBatches();
            if (batches.Count == 0)
                throw new InvalidOperationException("World produced no data batches.");

            Dispatch(() => ReportStatus($"Exporting ({batches.Count} batches, {ws.RawBytesReceived / 1024.0:N1} KB)..."));

            int written = await Task.Run(() =>
            {
                using var stream = File.Create(outPath);
                return KgmapExport.WriteBatches(stream, batches);
            });

            if (written == 0)
                throw new InvalidOperationException("Export produced no batches.");
        }
        finally
        {
            progressTimer.Stop();
            ws.Client.Disconnect();
        }
    }

    private void SetBusy(bool busy)
    {
        SaveBtn.IsEnabled = !busy;
        UrlBox.IsEnabled = !busy;
        AvatarModeBox.IsEnabled = !busy;
        AccountBtn.IsEnabled = !busy;
        ConvertBtn.IsEnabled = !busy;
        KgmapPathBox.IsEnabled = !busy;
        BrowseKgmapBtn.IsEnabled = !busy;
        Tabs.IsEnabled = !busy;
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusLabel.Text = text;
        StatusLabel.Foreground = error
            ? System.Windows.Media.Brushes.Firebrick
            : System.Windows.Media.Brushes.Gray;
    }

    private void Dispatch(Action a) => Dispatcher.Invoke(a);

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
        => UrlPlaceholder.Visibility = string.IsNullOrEmpty(UrlBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void KgmapPathBox_TextChanged(object sender, TextChangedEventArgs e)
        => KgmapPlaceholder.Visibility = string.IsNullOrEmpty(KgmapPathBox.Text) ? Visibility.Visible : Visibility.Collapsed;

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
