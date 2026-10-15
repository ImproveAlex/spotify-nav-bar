using System.Text.Json;

namespace SpotifyBar.Core;

public sealed record Track(string Id, string Name, string Artist, string? ImageUrl, string Uri,
    string? ExternalUrl, int DurationMs)
{
    public static Track FromJson(JsonElement item)
    {
        var artist = item.TryGetProperty("artists", out var artists)
            ? string.Join(", ", artists.EnumerateArray().Select(a => JsonValue.String(a, "name")))
            : item.TryGetProperty("show", out var show) ? JsonValue.String(show, "name") : "Spotify";
        string? image = null;
        var source = item.TryGetProperty("album", out var album) ? album : item;
        if (source.TryGetProperty("images", out var images) && images.GetArrayLength() > 0)
            image = JsonValue.String(images[0], "url");
        return new(JsonValue.String(item, "id"), JsonValue.String(item, "name"), artist, image,
            JsonValue.String(item, "uri"), item.TryGetProperty("external_urls", out var urls)
                ? JsonValue.String(urls, "spotify") : null, JsonValue.Int(item, "duration_ms"));
    }
}

public sealed record Device(string Id, string Name, string Type, bool IsActive, bool IsRestricted,
    bool SupportsVolume, int? Volume)
{
    public static Device FromJson(JsonElement item) => new(JsonValue.String(item, "id"),
        JsonValue.String(item, "name"), JsonValue.String(item, "type"), JsonValue.Bool(item, "is_active"),
        JsonValue.Bool(item, "is_restricted"),
        !item.TryGetProperty("supports_volume", out var supported) || supported.ValueKind == JsonValueKind.True,
        item.TryGetProperty("volume_percent", out var volume) && volume.ValueKind == JsonValueKind.Number
            ? volume.GetInt32() : null);
    public string Label => Name + (IsActive ? "  ·  Playing here" : "") + (IsRestricted ? "  ·  Unavailable" : "");
    public bool CanSelect => !IsRestricted && !string.IsNullOrEmpty(Id);
}

public sealed record Playback(Track? Track, Device? Device, bool IsPlaying, int ProgressMs,
    bool Shuffle, string Repeat, HashSet<string> Disallowed)
{
    public bool Allows(string action) => Device is { IsRestricted: false } && !Disallowed.Contains(action);
    public static Playback FromJson(JsonElement root)
    {
        var disallowed = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("actions", out var actions) && actions.TryGetProperty("disallows", out var list))
            foreach (var property in list.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.True) disallowed.Add(property.Name);
        return new(root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object
                ? Track.FromJson(item) : null,
            root.TryGetProperty("device", out var device) && device.ValueKind == JsonValueKind.Object
                ? Device.FromJson(device) : null,
            JsonValue.Bool(root, "is_playing"), JsonValue.Int(root, "progress_ms"),
            JsonValue.Bool(root, "shuffle_state"), JsonValue.String(root, "repeat_state", "off"), disallowed);
    }
}

internal static class JsonValue
{
    public static string String(JsonElement item, string key, string fallback = "") =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback : fallback;
    public static int Int(JsonElement item, string key) => item.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
    public static bool Bool(JsonElement item, string key) => item.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.True;
}

public sealed record TokenSet(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string Scope = "");

public interface ITokenStore
{
    TokenSet? Load();
    void Save(TokenSet tokens);
    void Clear();
}

public sealed class SignInRequiredException(string message = "Connect Spotify to continue.") : Exception(message);
public sealed class SpotifyApiException(int statusCode, string message, DateTimeOffset? retryAt = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
