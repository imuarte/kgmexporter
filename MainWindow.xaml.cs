using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.Win32;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;

namespace KgmExporter;

public partial class MainWindow : Window
{
    private ArchiveUploadQueue? _archiveUploadQueue;
    private CancellationTokenSource _scanCts = new();

    public MainWindow()
    {
        InitializeComponent();
        _archiveUploadQueue = new ArchiveUploadQueue((text, error) =>
            Dispatch(() => SetArchiveStatus(text, error)));
        Closing += MainWindow_Closing;
        Closed += (_, _) => _archiveUploadQueue?.Dispose();
        LoadArchiveSettingsIntoUi();
    }

    private void CancelUploadBtn_Click(object sender, RoutedEventArgs e)
    {
        _scanCts.Cancel();
        _scanCts = new CancellationTokenSource();
        _archiveUploadQueue?.CancelAndReset();
        SetStatus("Cancelled.");
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

    private void LoadArchiveSettingsIntoUi()
    {
        var settings = LocalSettings.Load();
        ArchiveDedupBox.IsChecked = settings.ArchiveSkipDuplicates != false;
        UploadArchivesAsIsBox.IsChecked = settings.UploadArchivesAsIs == true;
        UploadDelayBox.Text = UploadTuning.Delay(settings).ToString();
        RetryDelayBox.Text = UploadTuning.RetryDelay(settings).ToString();
    }

    private void UploadArchivesAsIsBox_Changed(object sender, RoutedEventArgs e)
    {
        bool enabled = UploadArchivesAsIsBox.IsChecked == true;
        var settings = LocalSettings.Load() with { UploadArchivesAsIs = enabled };
        LocalSettings.Save(settings);
    }

    private void UploadTuning_LostFocus(object sender, RoutedEventArgs e)
    {
        int delay = ParseClamped(UploadDelayBox.Text, UploadTuning.DefaultDelayMs, 0, 60_000);
        int retryDelay = ParseClamped(RetryDelayBox.Text, UploadTuning.DefaultRetryDelayMs, 0, 60_000);

        UploadDelayBox.Text = delay.ToString();
        RetryDelayBox.Text = retryDelay.ToString();

        var settings = LocalSettings.Load() with
        {
            UploadDelayMs = delay,
            UploadRetryDelayMs = retryDelay,
        };
        LocalSettings.Save(settings);
    }

    private static int ParseClamped(string? text, int fallback, int min, int max)
    {
        if (int.TryParse(text?.Trim(), out int n))
            return Math.Clamp(n, min, max);
        return fallback;
    }

    private void ArchiveDedupBox_Changed(object sender, RoutedEventArgs e)
    {
        bool enabled = ArchiveDedupBox.IsChecked == true;
        var settings = LocalSettings.Load() with { ArchiveSkipDuplicates = enabled };
        LocalSettings.Save(settings);
    }

    private void S3KeysBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new S3KeysWindow { Owner = this };
        dlg.ShowDialog();
    }

    private void UploadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void UploadFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveUploadQueue == null)
        {
            SetStatus("Archive upload queue is not initialized.", error: true);
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = "Pick .kgmap / .zip / .rar files",
            Filter = "Map files (*.kgmap;*.zip;*.rar)|*.kgmap;*.zip;*.rar|All files (*.*)|*.*",
            InitialDirectory = GetDefaultSaveDirectory(),
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        var files = dlg.FileNames.ToArray();
        SetStatus(files.Length == 1
            ? $"Queueing {Path.GetFileName(files[0])}..."
            : $"Queueing {files.Length} files...");
        var token = _scanCts.Token;
        var settings = LocalSettings.Load();
        bool dedup = settings.ArchiveSkipDuplicates != false;
        bool asIs = settings.UploadArchivesAsIs == true;
        _ = Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(
                    files,
                    new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = token },
                    file => ProcessUploadPath(file, token, dedup, asIs));
                Dispatch(() => SetStatus($"Queued {files.Length} file{(files.Length == 1 ? "" : "s")}. Uploading..."));
            }
            catch (OperationCanceledException) { }
        });
    }

    private void UploadFolders_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveUploadQueue == null)
        {
            SetStatus("Archive upload queue is not initialized.", error: true);
            return;
        }

        var dlg = new OpenFolderDialog
        {
            Title = "Pick one or more folders to scan for .kgmap files",
            InitialDirectory = GetDefaultSaveDirectory(),
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        var folders = dlg.FolderNames.ToArray();
        var settings = LocalSettings.Load();
        bool asIs = settings.UploadArchivesAsIs == true;
        bool dedup = settings.ArchiveSkipDuplicates != false;
        SetStatus(folders.Length == 1
            ? $"{(asIs ? "Zipping" : "Scanning")} {folders[0]}..."
            : $"{(asIs ? "Zipping" : "Scanning")} {folders.Length} folders...");
        var token = _scanCts.Token;
        _ = Task.Run(() =>
        {
            foreach (string folder in folders)
            {
                if (token.IsCancellationRequested) return;
                if (asIs) ZipAndQueueFolder(folder, token, dedup);
                else ScanAndQueueFolder(folder, null, token, dedup);
            }
        });
    }

    private void ProcessUploadPath(string path, CancellationToken token, bool dedup, bool asIs)
    {
        if (token.IsCancellationRequested) return;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".zip":
                if (asIs) QueueRawArchive(path, dedup);
                else ScanAndQueueZip(path, token, dedup);
                break;
            case ".rar":
                if (asIs) QueueRawArchive(path, dedup);
                else ScanAndQueueRar(path, token, dedup);
                break;
            case ".kgmap":
                QueueSingleKgmap(path, dedup);
                break;
            default:
                if (Directory.Exists(path))
                {
                    if (asIs) ZipAndQueueFolder(path, token, dedup);
                    else ScanAndQueueFolder(path, null, token, dedup);
                }
                else
                    Dispatch(() => SetStatus($"Skipped {Path.GetFileName(path)}: unsupported file type.", error: true));
                break;
        }
    }

    private void ZipAndQueueFolder(string folder, CancellationToken token, bool dedup)
    {
        if (token.IsCancellationRequested) return;

        string folderName = new DirectoryInfo(folder).Name;
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string zipName = $"{folderName}-{stamp}.zip";
        string zipPath = Path.Combine(Path.GetTempPath(), "kgmexporter-out-" + Guid.NewGuid().ToString("N") + "-" + zipName);

        try
        {
            Dispatch(() => SetStatus($"Zipping {folderName} -> {zipName}..."));
            // No compression - .kgmap is already gzipped.
            System.IO.Compression.ZipFile.CreateFromDirectory(
                folder, zipPath,
                System.IO.Compression.CompressionLevel.NoCompression,
                includeBaseDirectory: false);
        }
        catch (Exception ex)
        {
            Dispatch(() => SetStatus($"Zip create failed: {ex.Message}", error: true));
            return;
        }

        QueueRawArchive(zipPath, dedup);
    }

    private void QueueRawArchive(string filePath, bool dedup)
    {
        if (_archiveUploadQueue == null) return;
        string fileName = Path.GetFileName(filePath);
        try
        {
            _archiveUploadQueue.Enqueue(filePath, metadata: null, skipDuplicates: dedup);
            Dispatch(() => SetStatus($"Queued {fileName}."));
        }
        catch (Exception ex)
        {
            Dispatch(() => SetStatus($"Failed to queue {fileName}: {ex.Message}", error: true));
        }
    }

    private void QueueSingleKgmap(string filePath, bool dedup)
    {
        if (_archiveUploadQueue == null) return;

        string fileName = Path.GetFileName(filePath);
        var check = ClassifyKgmap(filePath);
        if (check == KgmapCheck.TooOld)
        {
            Dispatch(() => SetStatus($"Skipped {fileName}: version too old (legacy mesh format).", error: true));
            return;
        }
        if (check != KgmapCheck.Valid)
        {
            Dispatch(() => SetStatus($"Skipped {fileName}: not a valid .kgmap.", error: true));
            return;
        }

        try
        {
            _archiveUploadQueue.Enqueue(filePath, metadata: null, skipDuplicates: dedup);
        }
        catch
        {
        }
    }

    private void ScanAndQueueFolder(string folder, string? originLabel, CancellationToken token, bool dedup)
    {
        if (token.IsCancellationRequested) return;

        string label = originLabel ?? folder;
        int queued = 0, invalid = 0, tooOld = 0;
        try
        {
            var files = Directory.EnumerateFiles(folder, "*.kgmap", SearchOption.AllDirectories).ToList();

            // No HEAD pre-flight - just enqueue and let the queue workers do the
            // duplicate check inline with the PUT. Avoids holding up scans on
            // slow archive.org responses.
            Parallel.ForEach(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = token },
                filePath =>
                {
                    var check = ClassifyKgmap(filePath);
                    if (check == KgmapCheck.TooOld)
                    {
                        Interlocked.Increment(ref tooOld);
                        return;
                    }
                    if (check != KgmapCheck.Valid)
                    {
                        Interlocked.Increment(ref invalid);
                        return;
                    }
                    try
                    {
                        _archiveUploadQueue!.Enqueue(filePath, metadata: null, skipDuplicates: dedup);
                        Interlocked.Increment(ref queued);
                    }
                    catch
                    {
                        Interlocked.Increment(ref invalid);
                    }
                });
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Dispatch(() => SetStatus($"Folder scan failed: {ex.Message}", error: true));
            return;
        }

        Dispatch(() =>
        {
            string summary = $"Queued {queued} .kgmap file{(queued == 1 ? "" : "s")} from {label}";
            if (tooOld > 0) summary += $", {tooOld} skipped (version too old - legacy mesh format)";
            if (invalid > 0) summary += $", {invalid} skipped as not valid .kgmap";
            SetStatus(summary + ".");
        });
    }

    private void ScanAndQueueZip(string zipPath, CancellationToken token, bool dedup)
    {
        if (token.IsCancellationRequested) return;

        string tempRoot = Path.Combine(Path.GetTempPath(), "kgmexporter-zip-" + Guid.NewGuid().ToString("N"));
        string label = Path.GetFileName(zipPath);
        int queued = 0, invalid = 0, tooOld = 0;

        try
        {
            Directory.CreateDirectory(tempRoot);
            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (token.IsCancellationRequested) return;
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (!entry.Name.EndsWith(".kgmap", StringComparison.OrdinalIgnoreCase)) continue;

                string outPath = Path.Combine(tempRoot, entry.Name);
                try
                {
                    using (var src = entry.Open())
                    using (var dst = File.Create(outPath))
                        src.CopyTo(dst);
                }
                catch
                {
                    invalid++;
                    continue;
                }

                var check = ClassifyKgmap(outPath);
                if (check == KgmapCheck.TooOld)
                {
                    tooOld++;
                    continue;
                }
                if (check != KgmapCheck.Valid)
                {
                    invalid++;
                    continue;
                }

                try
                {
                    _archiveUploadQueue!.Enqueue(outPath, metadata: null, skipDuplicates: dedup);
                    queued++;
                }
                catch
                {
                    invalid++;
                }
            }
        }
        catch (Exception ex)
        {
            Dispatch(() => SetStatus($"Zip extract failed: {ex.Message}", error: true));
            return;
        }

        int q = queued, inv = invalid, old = tooOld;
        Dispatch(() =>
        {
            string summary = $"Queued {q} .kgmap from {label}";
            if (old > 0) summary += $", {old} skipped (version too old)";
            if (inv > 0) summary += $", {inv} skipped";
            SetStatus(summary + ".");
        });
    }

    private void ScanAndQueueRar(string rarPath, CancellationToken token, bool dedup)
    {
        if (token.IsCancellationRequested) return;

        string tempRoot = Path.Combine(Path.GetTempPath(), "kgmexporter-rar-" + Guid.NewGuid().ToString("N"));
        string label = Path.GetFileName(rarPath);
        int queued = 0, invalid = 0, tooOld = 0;

        try
        {
            Directory.CreateDirectory(tempRoot);
            using var archive = RarArchive.Open(rarPath);
            foreach (var entry in archive.Entries)
            {
                if (token.IsCancellationRequested) return;
                if (entry.IsDirectory) continue;
                if (string.IsNullOrEmpty(entry.Key)) continue;

                string entryName = Path.GetFileName(entry.Key);
                if (!entryName.EndsWith(".kgmap", StringComparison.OrdinalIgnoreCase)) continue;

                string outPath = Path.Combine(tempRoot, entryName);
                try
                {
                    using (var src = entry.OpenEntryStream())
                    using (var dst = File.Create(outPath))
                        src.CopyTo(dst);
                }
                catch
                {
                    invalid++;
                    continue;
                }

                var check = ClassifyKgmap(outPath);
                if (check == KgmapCheck.TooOld)
                {
                    tooOld++;
                    continue;
                }
                if (check != KgmapCheck.Valid)
                {
                    invalid++;
                    continue;
                }

                try
                {
                    _archiveUploadQueue!.Enqueue(outPath, metadata: null, skipDuplicates: dedup);
                    queued++;
                }
                catch
                {
                    invalid++;
                }
            }
        }
        catch (Exception ex)
        {
            Dispatch(() => SetStatus($"Rar extract failed: {ex.Message}", error: true));
            return;
        }

        int q = queued, inv = invalid, old = tooOld;
        Dispatch(() =>
        {
            string summary = $"Queued {q} .kgmap from {label}";
            if (old > 0) summary += $", {old} skipped (version too old)";
            if (inv > 0) summary += $", {inv} skipped";
            SetStatus(summary + ".");
        });
    }

    // .kgmap container versions: v2 = legacy baked-mesh format (released in
    // kgmexporter v0.1.0), v5/v6 = raw GetGameBatch batches (v0.2.0+). Only the
    // batch formats can be parsed/converted today, so anything older is dropped.
    private const ushort MinSupportedKgmapVersion = 5;
    private const uint KgmapMagic = 0x504D474B; // "KGMP"

    private enum KgmapCheck { Valid, NotKgmap, TooOld }

    // Reads the gzip + KGMP header to classify a .kgmap. Cheap: only the first
    // few decompressed bytes (magic + version) are read.
    private static KgmapCheck ClassifyKgmap(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            if (stream.ReadByte() != 0x1F || stream.ReadByte() != 0x8B)
                return KgmapCheck.NotKgmap;

            stream.Position = 0;
            using var gz = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
            using var br = new BinaryReader(gz);

            if (br.ReadUInt32() != KgmapMagic)
                return KgmapCheck.NotKgmap;
            ushort version = br.ReadUInt16();
            return version < MinSupportedKgmapVersion ? KgmapCheck.TooOld : KgmapCheck.Valid;
        }
        catch
        {
            return KgmapCheck.NotKgmap;
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
        ConvertBtn.IsEnabled = false;
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
            ConvertBtn.IsEnabled = true;
        }
    }

    private void KgmapPathBox_TextChanged(object sender, TextChangedEventArgs e)
        => KgmapPlaceholder.Visibility = string.IsNullOrEmpty(KgmapPathBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private static string GetDefaultSaveDirectory()
    {
        foreach (string path in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        })
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                return path;
        }

        return Environment.CurrentDirectory;
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
            Dispatcher.BeginInvoke(a);
    }
}
