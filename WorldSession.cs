using System.IO;
using KogamaScripts;

namespace KgmExporter;

internal sealed class WorldSession
{
    private long _lastWorldDataTicks = DateTime.UtcNow.Ticks;
    private long _rawBytesReceived;
    private int _serverLogicDisconnected;
    private readonly List<byte[]> _batches = new();
    private readonly object _batchLock = new();
    private readonly Dictionary<int, MemoryStream> _pendingBatches = new();
    private readonly MemoryStream _pendingSnapshot = new();

    public WorldSession(KogamaClient client, string worldId, TaskCompletionSource ready)
    {
        Client = client;
        WorldId = worldId;
        Ready = ready;
    }

    public KogamaClient Client { get; }
    public string WorldId { get; }
    public TaskCompletionSource Ready { get; }
    public long RawBytesReceived => Volatile.Read(ref _rawBytesReceived);
    public bool ServerLogicDisconnected => Volatile.Read(ref _serverLogicDisconnected) != 0;

    public event Action<long>? OnBytesProgress;

    public void NoteRawBytes(int byteCount)
    {
        if (byteCount <= 0) return;
        long total = Interlocked.Add(ref _rawBytesReceived, byteCount);
        Interlocked.Exchange(ref _lastWorldDataTicks, DateTime.UtcNow.Ticks);
        OnBytesProgress?.Invoke(total);
    }

    public void FeedBatchChunk(int queryId, byte[] data, bool dataLeft)
    {
        lock (_batchLock)
        {
            if (data != null && data.Length > 0)
            {
                if (!_pendingBatches.TryGetValue(queryId, out var buf))
                    _pendingBatches[queryId] = buf = new MemoryStream();
                buf.Write(data, 0, data.Length);
            }

            if (!dataLeft && _pendingBatches.Remove(queryId, out var complete) && complete.Length > 0)
                _batches.Add(complete.ToArray());
        }
    }

    public void FeedSnapshotChunk(byte[] data, bool dataLeft)
    {
        lock (_batchLock)
        {
            if (data != null && data.Length > 0)
                _pendingSnapshot.Write(data, 0, data.Length);

            if (!dataLeft && _pendingSnapshot.Length > 0)
            {
                _batches.Add(_pendingSnapshot.ToArray());
                _pendingSnapshot.SetLength(0);
            }
        }
    }

    public IReadOnlyList<byte[]> SnapshotBatches()
    {
        lock (_batchLock) return _batches.ToArray();
    }

    public void MarkReady()
    {
        Interlocked.Exchange(ref _lastWorldDataTicks, DateTime.UtcNow.Ticks);
        Ready.TrySetResult();
    }

    public void MarkActivity()
        => Interlocked.Exchange(ref _lastWorldDataTicks, DateTime.UtcNow.Ticks);

    public void MarkServerLogicDisconnected()
    {
        Interlocked.Exchange(ref _serverLogicDisconnected, 1);
        Ready.TrySetException(new ServerLogicDisconnectException());
    }

    public void ThrowIfServerLogicDisconnected()
    {
        if (ServerLogicDisconnected)
            throw new ServerLogicDisconnectException();
    }

    public async Task WaitForWorldQuietAsync(TimeSpan quietFor, CancellationToken ct = default)
    {
        await Ready.Task.WaitAsync(ct);
        ThrowIfServerLogicDisconnected();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfServerLogicDisconnected();

            DateTime lastActivity = new(Volatile.Read(ref _lastWorldDataTicks), DateTimeKind.Utc);
            TimeSpan idleFor = DateTime.UtcNow - lastActivity;
            if (idleFor >= quietFor)
                return;

            TimeSpan remainingQuiet = quietFor - idleFor;
            TimeSpan delay = remainingQuiet;
            if (delay > TimeSpan.FromMilliseconds(100))
                delay = TimeSpan.FromMilliseconds(100);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);
        }
    }
}

internal sealed class ServerLogicDisconnectException()
    : Exception("Server closed the connection with DisconnectedByServerLogic.");

internal static class WorldOpener
{
    public static async Task<WorldSession?> OpenAsync(
        string url,
        GameMode? forceMode = null,
        SessionType? forceSessionType = null,
        bool asGuest = false)
    {
        if (!UrlParser.TryParse(url, out string? worldId, out int ownerProfileId, out var region, out var mode, out var sessionType))
            return null;

        if (forceMode.HasValue) mode = forceMode.Value;
        if (forceSessionType.HasValue) sessionType = forceSessionType.Value;

        int profileId;
        if (asGuest)
        {
            profileId = 0;
        }
        else
        {
            LocalAuth.LoadCookies();
            var session = LocalAuth.LoadSession();
            profileId = mode == GameMode.Build ? ownerProfileId : session?.ProfileId ?? 0;
        }

        var client = new KogamaClient();
        var ready = new TaskCompletionSource();
        var ws = new WorldSession(client, worldId!, ready);
        client.OnWorldReady += ws.MarkReady;
        client.OnWorldDataActivity += ws.MarkActivity;
        client.OnRawWorldData += data => ws.NoteRawBytes(data?.Length ?? 0);
        client.OnGameBatchChunk += (queryId, data, _, dataLeft) => ws.FeedBatchChunk(queryId, data, dataLeft);
        client.OnGameSnapshotChunk += (_, data, dataLeft) => ws.FeedSnapshotChunk(data, dataLeft);
        client.OnServerLogicDisconnect += ws.MarkServerLogicDisconnected;

        await client.ConnectAsync(worldId!, region, mode: mode, ownerProfileId: profileId, sessionType: sessionType);
        return ws;
    }
}
