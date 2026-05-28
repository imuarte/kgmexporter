using System.Collections.Concurrent;
using System.IO;

namespace KgmExporter;

internal sealed class ArchiveUploadQueue : IDisposable
{
    private sealed record Job(string FilePath, KgmapMetadata? Metadata, bool SkipDuplicates);

    private readonly BlockingCollection<Job> _jobs = new();
    private CancellationTokenSource _cts = new();
    private Task[] _workers;
    private readonly Action<string, bool> _reportStatus;
    private readonly int _workerCount;

    private int _queued;
    private int _running;
    private int _uploaded;
    private int _alreadyExists;
    private int _pending;
    private int _failed;

    public int PendingCount => Volatile.Read(ref _queued) + Volatile.Read(ref _running);

    public ArchiveUploadQueue(Action<string, bool> reportStatus, int workerCount = 128)
    {
        _reportStatus = reportStatus;
        _workerCount = Math.Max(1, workerCount);
        _workers = Enumerable.Range(0, _workerCount)
            .Select(_ => Task.Run(WorkerLoopAsync))
            .ToArray();
    }

    public void CancelAndReset()
    {
        int drained = Volatile.Read(ref _queued);
        while (_jobs.TryTake(out _)) { }
        Interlocked.Exchange(ref _queued, 0);

        _cts.Cancel();
        try { Task.WaitAll(_workers, TimeSpan.FromSeconds(5)); } catch { }
        _cts.Dispose();
        _cts = new CancellationTokenSource();

        Interlocked.Exchange(ref _running, 0);
        Interlocked.Exchange(ref _uploaded, 0);
        Interlocked.Exchange(ref _alreadyExists, 0);
        Interlocked.Exchange(ref _pending, 0);
        Interlocked.Exchange(ref _failed, 0);

        _workers = Enumerable.Range(0, _workerCount)
            .Select(_ => Task.Run(WorkerLoopAsync))
            .ToArray();

        _reportStatus(drained > 0
            ? $"Cancelled uploads ({drained} dropped from queue)."
            : "Cancelled uploads.", false);
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
            {
                await ProcessJobAsync(job);

                int delayMs = UploadTuning.Delay(LocalSettings.Load());
                if (delayMs > 0)
                {
                    try { await Task.Delay(delayMs, _cts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }
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

            // Worker does the HEAD check inline so scan-time isn't gated on
            // archive.org. Duplicates skip the PUT inside UploadKgmapAsync.
            var result = await ArchiveUploader.UploadKgmapAsync(
                job.FilePath,
                options,
                job.Metadata,
                status => Report($"{fileName}: {status}"),
                _cts.Token,
                job.SkipDuplicates,
                skipRemoteCheck: false);

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

    private void ReportIdleIfDone() => Report("", error: false);

    private void Report(string _text, bool error = false)
    {
        int running = Volatile.Read(ref _running);
        int queued = Volatile.Read(ref _queued);
        int uploaded = Volatile.Read(ref _uploaded);
        int failed = Volatile.Read(ref _failed);
        int already = Volatile.Read(ref _alreadyExists);
        int pending = Volatile.Read(ref _pending);

        if (running == 0 && queued == 0 && uploaded == 0 && failed == 0 && already == 0 && pending == 0)
        {
            _reportStatus("", false);
            return;
        }

        string status = $"Done {uploaded}   Running {running}   Queued {queued}";
        if (failed > 0) status += $"   Failed {failed}";
        if (already > 0) status += $"   On archive {already}";
        if (pending > 0) status += $"   Pending {pending}";

        _reportStatus(status, error || failed > 0);
    }

    public void Dispose()
    {
        _jobs.CompleteAdding();
        _cts.Cancel();
        _jobs.Dispose();
        _cts.Dispose();
    }
}
