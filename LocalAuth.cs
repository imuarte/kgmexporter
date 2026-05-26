using System.IO;
using System.Text.Json;
using KogamaScripts;

namespace KgmExporter;

internal record SessionData(string Username, int ProfileId, KogamaRegion Region);

internal static class LocalAuth
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string SessionPath = Path.Combine(Home, ".kgmexporter_session");
    private static readonly string CookiePath = Path.Combine(Home, ".kgmexporter_cookies");

    public static SessionData? LoadSession()
    {
        if (!File.Exists(SessionPath)) return null;
        try { return JsonSerializer.Deserialize<SessionData>(File.ReadAllText(SessionPath)); }
        catch { return null; }
    }

    public static void SaveSession(SessionData data)
        => File.WriteAllText(SessionPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));

    public static void ClearSession()
    {
        if (File.Exists(SessionPath)) File.Delete(SessionPath);
        if (File.Exists(CookiePath)) File.Delete(CookiePath);
    }

    public static void LoadCookies() => Auth.LoadCookies(CookiePath);
    public static void SaveCookies() => Auth.SaveCookies(CookiePath);
}
