using System.Text.RegularExpressions;
using KogamaScripts;

namespace KgmExporter;

internal static class UrlParser
{
    public static bool TryParse(
        string url,
        out string? worldId,
        out int ownerProfileId,
        out KogamaRegion region,
        out GameMode mode,
        out SessionType? sessionType)
    {
        worldId = null;
        ownerProfileId = 0;
        region = KogamaRegion.Www;
        mode = GameMode.Play;
        sessionType = null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        string host = uri.Host;
        region = host switch
        {
            var h when h.Contains("kogama.com.br")      => KogamaRegion.Br,
            var h when h.Contains("friends.kogama.com") => KogamaRegion.Friends,
            _                                            => KogamaRegion.Www,
        };

        var playMatch = Regex.Match(uri.AbsolutePath, @"^/games/play/(\d+)");
        if (playMatch.Success)
        {
            worldId = playMatch.Groups[1].Value;
            mode = GameMode.Play;
            sessionType = SessionType.Play;
            return true;
        }

        var buildMatch = Regex.Match(uri.AbsolutePath, @"^/build/(\d+)/project/(\d+)");
        if (buildMatch.Success)
        {
            ownerProfileId = int.Parse(buildMatch.Groups[1].Value);
            worldId = buildMatch.Groups[2].Value;
            mode = GameMode.Build;
            sessionType = SessionType.Edit;
            return true;
        }

        return false;
    }

    public static bool TryParseProfile(string url, out int profileId, out KogamaRegion region)
    {
        profileId = 0;
        region = KogamaRegion.Www;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        string host = uri.Host;
        region = host switch
        {
            var h when h.Contains("kogama.com.br")      => KogamaRegion.Br,
            var h when h.Contains("friends.kogama.com") => KogamaRegion.Friends,
            _                                            => KogamaRegion.Www,
        };

        var m = Regex.Match(uri.AbsolutePath, @"^/profile/(\d+)");
        if (!m.Success) return false;
        profileId = int.Parse(m.Groups[1].Value);
        return true;
    }

    public static bool IsBuildRoot(string url, out KogamaRegion region)
    {
        region = KogamaRegion.Www;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        string host = uri.Host;
        region = host switch
        {
            var h when h.Contains("kogama.com.br")      => KogamaRegion.Br,
            var h when h.Contains("friends.kogama.com") => KogamaRegion.Friends,
            _                                            => KogamaRegion.Www,
        };

        return Regex.IsMatch(uri.AbsolutePath, @"^/build/?$");
    }

    public static bool IsGamesListing(string url, out KogamaRegion region)
    {
        region = KogamaRegion.Www;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        string host = uri.Host;
        region = host switch
        {
            var h when h.Contains("kogama.com.br")      => KogamaRegion.Br,
            var h when h.Contains("friends.kogama.com") => KogamaRegion.Friends,
            _                                            => KogamaRegion.Www,
        };

        return Regex.IsMatch(uri.AbsolutePath, @"^/games/?$");
    }
}
