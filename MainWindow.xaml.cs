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

        var dlg = new SaveFileDialog
        {
            FileName = "world.kgmap",
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
            FileName = Path.GetFileNameWithoutExtension(kgmapPath) + ".glb",
            Filter = "glTF binary (*.glb)|*.glb",
            DefaultExt = ".glb"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        string outPath = dlg.FileName;
        SetBusy(true);
        SetStatus("Converting...");
        try
        {
            int written = await Task.Run(() => KgmapToGlb.Convert(kgmapPath, outPath));
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

    private async Task SaveMapAsync(string url, string outPath, bool avatarMode)
    {
        GameMode? forceMode = avatarMode ? GameMode.AvatarEdit : null;
        SessionType? forceSession = avatarMode ? SessionType.Character : null;

        WorldSession? ws = await WorldOpener.OpenAsync(url, forceMode, forceSession);
        if (ws == null)
            throw new InvalidOperationException("Invalid Kogama URL. Use /games/play/<id> or /build/<owner>/project/<id>.");

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
                SetStatus($"Downloading... {kb:N0} KB");
            }
        };
        progressTimer.Start();

        try
        {
            await ws.WaitForWorldQuietAsync(
                readyTimeout: Timeout.InfiniteTimeSpan,
                quietFor: TimeSpan.FromSeconds(5),
                quietTimeout: TimeSpan.FromSeconds(20));

            int objects = ws.Client.World.Objects.Count;
            int prototypes = ws.Client.World.Prototypes.Count;
            if (objects == 0 || prototypes == 0)
                throw new InvalidOperationException("World loaded but contained no parsed objects.");

            Dispatch(() => SetStatus($"Exporting ({objects} objects)..."));

            int written = await Task.Run(() =>
            {
                using var stream = File.Create(outPath);
                return KgmapExport.WriteWorld(stream, ws);
            });

            if (written == 0)
                throw new InvalidOperationException("Export produced no geometry.");
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
