using System.Text.RegularExpressions;

namespace SpotifyBar.Core;

public static partial class SpotifyLinks
{
    [GeneratedRegex("^[A-Za-z0-9]{22}$", RegexOptions.CultureInvariant)]
    private static partial Regex TrackId();

    public static bool TryParseTrack(string input, out string uri)
    {
        uri = "";
        input = input.Trim();
        string? id = null;
        if (input.StartsWith("spotify:track:", StringComparison.Ordinal)) id = input[14..];
        else if (Uri.TryCreate(input, UriKind.Absolute, out var link)
            && link.Scheme == "https" && link.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = link.AbsolutePath.Trim('/').Split('/');
            if (parts.Length == 2 && parts[0] == "track") id = parts[1];
            else if (parts.Length == 3 && parts[0].StartsWith("intl-", StringComparison.Ordinal)
                && parts[1] == "track") id = parts[2];
        }
        if (id is null || !TrackId().IsMatch(id)) return false;
        uri = "spotify:track:" + id;
        return true;
    }

    public static bool IsLinkLike(string input) => input.Contains("://", StringComparison.Ordinal)
        || input.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase);
}
