using System.IO;
using System.Text.Json;

namespace KgmExporter;

internal sealed record AppSettings
{
    // null = user hasn't been asked yet; true/false = decided
    public bool? ArchiveAutoUpload { get; init; }
    // null defaults to true (skip duplicates on archive.org)
    public bool? ArchiveSkipDuplicates { get; init; }
    // null/empty = use the built-in default S3 credentials baked into the binary.
    // Non-empty = user override (entered in Advanced options).
    public string? S3AccessKey { get; init; }
    public string? S3SecretKey { get; init; }

    // Per-worker delay between successive uploads, to avoid getting rate-limited.
    public int? UploadDelayMs { get; init; }
    // Base wait between retries; attempt N waits N * UploadRetryDelayMs.
    public int? UploadRetryDelayMs { get; init; }
    // When true, .zip / .rar are uploaded to archive.org as-is instead of being
    // extracted and uploaded per .kgmap inside.
    public bool? UploadArchivesAsIs { get; init; }
}

internal static class UploadTuning
{
    public const int DefaultDelayMs = 0;
    public const int DefaultRetryDelayMs = 1000;

    public static int Delay(AppSettings s) => Math.Clamp(s.UploadDelayMs ?? DefaultDelayMs, 0, 60_000);
    public static int RetryDelay(AppSettings s) => Math.Clamp(s.UploadRetryDelayMs ?? DefaultRetryDelayMs, 0, 60_000);
}

internal static class LocalSettings
{
    private static readonly string Path_ = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        ".kgmexporter_settings");

    public static AppSettings Load()
    {
        if (!File.Exists(Path_)) return new AppSettings();
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path_)) ?? new AppSettings(); }
        catch { return new AppSettings(); }
    }

    public static void Save(AppSettings s)
        => File.WriteAllText(Path_, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
}
