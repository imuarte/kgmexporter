using System.IO;
using System.Text.Json;

namespace KgmExporter;

internal sealed record AppSettings
{
    // null = user hasn't been asked yet; true/false = decided
    public bool? ArchiveAutoUpload { get; init; }
    // null defaults to true (skip duplicates on archive.org)
    public bool? ArchiveSkipDuplicates { get; init; }
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
