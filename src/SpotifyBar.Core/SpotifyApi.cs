using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SpotifyBar.Core;

public sealed class SpotifyApi(HttpClient http, SpotifyAuth auth)
{
    public DateTimeOffset? RetryAt { get; private set; }

    public async Task<Playback?> GetPlaybackAsync(CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Get, "me/player?additional_types=track,episode", null, ct).ConfigureAwait(false);
        if (result is null) return null;
        using (result) return Playback.FromJson(result.RootElement);
    }

    public async Task<IReadOnlyList<Device>> GetDevicesAsync(CancellationToken ct)
    {
        using var result = await SendAsync(HttpMethod.Get, "me/player/devices", null, ct).ConfigureAwait(false);
        if (result is null) return [];
        return result.RootElement.GetProperty("devices").EnumerateArray().Select(Device.FromJson).ToList();
    }

    public async Task<IReadOnlyList<Track>> SearchAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        if (string.IsNullOrEmpty(query)) return [];
        if (SpotifyLinks.TryParseTrack(query, out var uri))
        {
            using var track = await SendAsync(HttpMethod.Get, "tracks/" + uri[14..], null, ct).ConfigureAwait(false);
            return track is null ? [] : [Track.FromJson(track.RootElement)];
        }
        if (SpotifyLinks.IsLinkLike(query)) throw new ArgumentException("Paste a Spotify track link or search by song and artist.");
        using var result = await SendAsync(HttpMethod.Get,
            "search?type=track&limit=10&q=" + Uri.EscapeDataString(query[..Math.Min(query.Length, 256)]), null, ct).ConfigureAwait(false);
        if (result is null) return [];
        return result.RootElement.GetProperty("tracks").GetProperty("items").EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object).Select(Track.FromJson).ToList();
    }

    public async Task<string> GetDisplayNameAsync(CancellationToken ct)
    {
        using var result = await SendAsync(HttpMethod.Get, "me", null, ct).ConfigureAwait(false);
        return result is null ? "Spotify account" : JsonValue.String(result.RootElement, "display_name", "Spotify account");
    }

    public Task PlayAsync(string? deviceId, string? trackUri, CancellationToken ct) => CommandAsync(HttpMethod.Put,
        WithDevice("me/player/play", deviceId), trackUri is null ? null : new { uris = new[] { trackUri } }, ct);
    public Task PauseAsync(string? deviceId, CancellationToken ct) => CommandAsync(HttpMethod.Put, WithDevice("me/player/pause", deviceId), null, ct);
    public Task NextAsync(string? deviceId, CancellationToken ct) => CommandAsync(HttpMethod.Post, WithDevice("me/player/next", deviceId), null, ct);
    public Task PreviousAsync(string? deviceId, CancellationToken ct) => CommandAsync(HttpMethod.Post, WithDevice("me/player/previous", deviceId), null, ct);
    public Task SeekAsync(string? deviceId, int position, CancellationToken ct) => CommandAsync(HttpMethod.Put,
        WithDevice("me/player/seek?position_ms=" + Math.Max(0, position), deviceId), null, ct);
    public Task VolumeAsync(string? deviceId, int volume, CancellationToken ct) => CommandAsync(HttpMethod.Put,
        WithDevice("me/player/volume?volume_percent=" + Math.Clamp(volume, 0, 100), deviceId), null, ct);
    public Task ShuffleAsync(string? deviceId, bool enabled, CancellationToken ct) => CommandAsync(HttpMethod.Put,
        WithDevice("me/player/shuffle?state=" + enabled.ToString().ToLowerInvariant(), deviceId), null, ct);
    public Task RepeatAsync(string? deviceId, string repeat, CancellationToken ct)
    {
        if (repeat is not ("off" or "context" or "track")) throw new ArgumentException("Invalid repeat mode.");
        return CommandAsync(HttpMethod.Put, WithDevice("me/player/repeat?state=" + repeat, deviceId), null, ct);
    }
    public Task TransferAsync(string id, bool play, CancellationToken ct) => CommandAsync(HttpMethod.Put,
        "me/player", new { device_ids = new[] { id }, play }, ct);
    public Task QueueAsync(string? deviceId, string uri, CancellationToken ct) => CommandAsync(HttpMethod.Post,
        WithDevice("me/player/queue?uri=" + Uri.EscapeDataString(uri), deviceId), null, ct);

    private static string WithDevice(string path, string? id) => string.IsNullOrEmpty(id) ? path
        : path + (path.Contains('?') ? "&" : "?") + "device_id=" + Uri.EscapeDataString(id);

    private async Task CommandAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var ignored = await SendAsync(method, path, body, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (RetryAt is { } retry && retry > DateTimeOffset.UtcNow)
            throw new SpotifyApiException(429, "Spotify API limit reached. Waiting before trying again.", retry);
        var token = await auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(method, "https://api.spotify.com/v1/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                token = await auth.GetAccessTokenAsync(ct, token).ConfigureAwait(false);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.NoContent) return null;
            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Spotify rejected this session. Reconnect your account.",
                    HttpStatusCode.Forbidden => "Spotify refused this action. Check app access, Premium, and device restrictions.",
                    HttpStatusCode.NotFound => "Open Spotify on a device, then select it in Devices.",
                    HttpStatusCode.TooManyRequests => "Spotify API limit reached. Waiting before trying again.",
                    _ => "Spotify could not complete this action. Please try again."
                };
                if (response.StatusCode == HttpStatusCode.Unauthorized) throw new SignInRequiredException(message);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    RetryAt = response.Headers.RetryAfter?.Date
                        ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(15));
                    if (RetryAt <= DateTimeOffset.UtcNow) RetryAt = DateTimeOffset.UtcNow.AddSeconds(1);
                }
                throw new SpotifyApiException((int)response.StatusCode, message, response.StatusCode == HttpStatusCode.TooManyRequests ? RetryAt : null);
            }
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json);
        }
        throw new SignInRequiredException();
    }
}
