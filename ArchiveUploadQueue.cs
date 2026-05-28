using System.Collections.Concurrent;
using System.IO;

namespace KgmExporter;

internal sealed class ArchiveUploadQueue : IDisposable
{
    private sealed record Job(string FilePath, KgmapMetadata? Metadata, bool SkipDuplicates);

    private readonly BlockingCollection<Job> _jobs = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _workers;
    private readonly Action<string, bool> _reportStatus;

    private int _queued;
    private int _running;
    private int _uploaded;
    private int _alreadyExists;
    private int _pending;
    private int _failed;

    public int PendingCount => Volatile.Read(ref _queued) + Volatile.Read(ref _running);

    public ArchiveUploadQueue(Action<string, bool> reportStatus, int workerCount = 32)
    {
        _reportStatus = reportStatus;
        _workers = Enumerable.Range(0, Math.Max(1, workerCount))
            .Select(_ => Task.Run(WorkerLoopAsync))
            .ToArray();
    }

    public void Enqueue(string filePath, KgmapMetadata? metadata, bool skipDuplicates)
    {
        if (_jobs.IsAddingCompleted)
            throw new InvalidOperationException("Archive upload queue is shut down.");

        Interlocked.Increment(ref _queued);
        try
        {
            _jobs.Add(new Job(filePath, metadata, skipDuplicates), _cts.Token);
            Report("Archive upload queued.");
        }
        catch
        {
            Interlocked.Decrement(ref _queued);
            throw;
        }
    }

    // Called by upstream code when it skipped a download because the file is
    // already on archive.org. The queue tracks this so the end-of-run summary
    // can show "already archivized: N".
    public void NoteAlreadyArchived(string fileName)
    {
        Interlocked.Increment(ref _alreadyExists);
        Report($"{fileName} already on archive.org.");
        ReportIdleIfDone();
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            foreach (var job in _jobs.GetConsumingEnumerable(_cts.Token))
                await ProcessJobAsync(job);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ProcessJobAsync(Job job)
    {
        string fileName = Path.GetFileName(job.FilePath);
        Interlocked.Decrement(ref _queued);
        Interlocked.Increment(ref _running);
        Report($"Uploading {fileName} to archive.org...");

        try
        {
            if (!ArchiveUploader.TryCreateOptions(LocalSettings.Load(), out var options, out string error) || options == null)
            {
                Interlocked.Increment(ref _failed);
                Report($"Archive upload skipped: {error}", error: true);
                return;
            }

            // Upstream has already done the pre-download HEAD check, so tell
            // the uploader to skip its own (no double round-trip).
            var result = await ArchiveUploader.UploadKgmapAsync(
                job.FilePath,
                options,
                job.Metadata,
                status => Report($"{fileName}: {status}"),
                _cts.Token,
                job.SkipDuplicates,
                skipRemoteCheck: true);

            switch (result.Status)
            {
                case ArchiveUploadStatus.Uploaded:
                    Interlocked.Increment(ref _uploaded);
                    Report($"Uploaded {fileName} to archive.org.");
                    break;
                case ArchiveUploadStatus.AlreadyExists:
                    Interlocked.Increment(ref _alreadyExists);
                    Report($"{fileName} is already on archive.org.");
                    break;
                case ArchiveUploadStatus.Pending:
                    Interlocked.Increment(ref _pending);
                    Report($"Uploaded {fileName}; archive.org is still indexing it.");
                    break;
                case ArchiveUploadStatus.Failed:
                    Interlocked.Increment(ref _failed);
                    Report($"Archive upload failed for {fileName}: {result.Message}", error: true);
                    break;
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failed);
            Report($"Archive upload failed for {fileName}: {ex.Message}", error: true);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            ReportIdleIfDone();
        }
    }

    private void ReportIdleIfDone()
    {
        if (Volatile.Read(ref _queued) != 0 || Volatile.Read(ref _running) != 0)
            return;

        int failed = Volatile.Read(ref _failed);
        int uploaded = Volatile.Read(ref _uploaded);
        int alreadyExists = Volatile.Read(ref _alreadyExists);
        int pending = Volatile.Read(ref _pending);
        int finished = uploaded + alreadyExists + pending + failed;
        if (finished <= 0)
            return;

        string text = $"Archive uploads finished: {uploaded} uploaded";
        if (pending > 0) text += $", {pending} pending on archive.org";
        if (alreadyExists > 0) text += $", already archivized: {alreadyExists}";
        if (failed > 0) text += $", {failed} failed";
        Report(text + ".", error: failed > 0);
    }

    private void Report(string text, bool error = false)
    {
        int running = Volatile.Read(ref _running);
        int queued = Volatile.Read(ref _queued);
        int alreadyExists = Volatile.Read(ref _alreadyExists);

        string status = "";
        if (running > 0) status += (status.Length == 0 ? "" : ", ") + $"{running} uploading";
        if (queued > 0) status += (status.Length == 0 ? "" : ", ") + $"{queued} queued";
        if (alreadyExists > 0) status += (status.Length == 0 ? "" : ", ") + $"already archivized: {alreadyExists}";

        if (status.Length > 0)
            text += " (" + status + ")";

        _reportStatus(text, error);
    }

    public void Dispose()
    {
        _jobs.CompleteAdding();
        _cts.Cancel();
        _jobs.Dispose();
        _cts.Dispose();
    }
}
