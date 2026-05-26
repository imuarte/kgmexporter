using KogamaScripts;

namespace KgmExporter;

internal sealed class WorldSession
{
    private long _lastWorldDataTicks = DateTime.UtcNow.Ticks;
    private long _rawBytesReceived;

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

    public event Action<long>? OnBytesProgress;

    public void NoteRawData(int byteCount)
    {
        long total = Interlocked.Add(ref _rawBytesReceived, byteCount);
        Interlocked.Exchange(ref _lastWorldDataTicks, DateTime.UtcNow.Ticks);
        OnBytesProgress?.Invoke(total);
    }

    public void MarkReady()
    {
        Interlocked.Exchange(ref _lastWorldDataTicks, DateTime.UtcNow.Ticks);
        Ready.TrySetResult();
    }

    public void MarkActivity()
        => Interlocked.Exchange(ref _lastWorldDataTicks, DateTime.UtcNow.Ticks);

    public async Task WaitForWorldQuietAsync(TimeSpan readyTimeout, TimeSpan quietFor, TimeSpan quietTimeout)
    {
        await Ready.Task.WaitAsync(readyTimeout);

        DateTime deadline = DateTime.UtcNow + quietTimeout;
        while (DateTime.UtcNow < deadline)
        {
            DateTime lastActivity = new(Volatile.Read(ref _lastWorldDataTicks), DateTimeKind.Utc);
            TimeSpan idleFor = DateTime.UtcNow - lastActivity;
            if (idleFor >= quietFor)
                return;

            TimeSpan remainingQuiet = quietFor - idleFor;
            TimeSpan remainingTimeout = deadline - DateTime.UtcNow;
            TimeSpan delay = remainingQuiet < remainingTimeout ? remainingQuiet : remainingTimeout;
            if (delay > TimeSpan.FromMilliseconds(100))
                delay = TimeSpan.FromMilliseconds(100);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);
        }
    }
}

internal static class WorldOpener
{
    public static async Task<WorldSession?> OpenAsync(
        string url,
        GameMode? forceMode = null,
        SessionType? forceSessionType = null)
    {
        if (!UrlParser.TryParse(url, out string? worldId, out int ownerProfileId, out var region, out var mode, out var sessionType))
            return null;

        if (forceMode.HasValue) mode = forceMode.Value;
        if (forceSessionType.HasValue) sessionType = forceSessionType.Value;

        LocalAuth.LoadCookies();
        var session = LocalAuth.LoadSession();
        int profileId = mode == GameMode.Build ? ownerProfileId : session?.ProfileId ?? 0;

        var client = new KogamaClient();
        var ready = new TaskCompletionSource();
        var ws = new WorldSession(client, worldId!, ready);
        client.OnWorldReady += ws.MarkReady;
        client.OnWorldDataActivity += ws.MarkActivity;
        client.OnRawWorldData += data => ws.NoteRawData(data?.Length ?? 0);

        await client.ConnectAsync(worldId!, region, mode: mode, ownerProfileId: profileId, sessionType: sessionType);
        return ws;
    }
}
