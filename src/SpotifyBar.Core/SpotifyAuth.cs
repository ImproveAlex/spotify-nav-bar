using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpotifyBar.Core;

public sealed class SpotifyAuth
{
    private readonly HttpClient _http;
    private readonly ITokenStore _store;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private TokenSet? _tokens;
    private int _sessionGeneration;
    public string ClientId { get; set; } = "";
    public bool IsConnected => _tokens is not null;
    public const string RequestedScopes = "user-read-playback-state user-read-currently-playing user-modify-playback-state";

    public SpotifyAuth(HttpClient http, ITokenStore store)
    {
        _http = http;
        _store = store;
        _tokens = store.Load();
    }

    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(64));
    public static string CreateChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        if (ClientId.Length != 32 || ClientId.Any(c => !Uri.IsHexDigit(c)))
            throw new SignInRequiredException("Enter your Spotify app's Client ID in App setup first.");
        var generation = _sessionGeneration;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var callback = new LoopbackCallback();
        var verifier = CreateVerifier();
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = ClientId, ["response_type"] = "code", ["redirect_uri"] = LoopbackCallback.RedirectUri,
            ["code_challenge_method"] = "S256", ["code_challenge"] = CreateChallenge(verifier), ["state"] = state,
            ["scope"] = RequestedScopes
        };
        var url = "https://accounts.spotify.com/authorize?" + string.Join("&", parameters.Select(p =>
            Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        var code = await callback.WaitForCodeAsync(state, timeout.Token).ConfigureAwait(false);
        var tokens = await RequestTokensAsync(new()
        {
            ["client_id"] = ClientId, ["grant_type"] = "authorization_code", ["code"] = code,
            ["redirect_uri"] = LoopbackCallback.RedirectUri, ["code_verifier"] = verifier
        }, null, timeout.Token).ConfigureAwait(false);
        timeout.Token.ThrowIfCancellationRequested();
        await _refreshLock.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            if (generation != _sessionGeneration) throw new SignInRequiredException();
            _store.Save(tokens);
            _tokens = tokens;
        }
        finally { _refreshLock.Release(); }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct, string? rejectedToken = null)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = _tokens ?? throw new SignInRequiredException();
            if (existing.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60)
                && (rejectedToken is null || existing.AccessToken != rejectedToken)) return existing.AccessToken;
            var generation = _sessionGeneration;
            try
            {
                var refreshed = await RequestTokensAsync(new()
                {
                    ["client_id"] = ClientId, ["grant_type"] = "refresh_token", ["refresh_token"] = existing.RefreshToken
                }, existing.RefreshToken, ct, existing.Scope).ConfigureAwait(false);
                if (generation != _sessionGeneration) throw new SignInRequiredException();
                _store.Save(refreshed);
                _tokens = refreshed;
                return refreshed.AccessToken;
            }
            catch (SignInRequiredException)
            {
                if (generation == _sessionGeneration) { _tokens = null; _store.Clear(); }
                throw;
            }
        }
        finally { _refreshLock.Release(); }
    }

    public async Task SignOutAsync()
    {
        Interlocked.Increment(ref _sessionGeneration);
        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try { _tokens = null; _store.Clear(); }
        finally { _refreshLock.Release(); }
    }

    private async Task<TokenSet> RequestTokensAsync(Dictionary<string, string> form, string? oldRefresh, CancellationToken ct, string? previousScope = null)
    {
        using var response = await _http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            throw new SignInRequiredException("Spotify could not authorize this app. Check App setup and reconnect.");
        if (!response.IsSuccessStatusCode)
            throw new SpotifyApiException((int)response.StatusCode, "Spotify sign-in is temporarily unavailable. Try again later.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = document.RootElement;
        var access = JsonValue.String(root, "access_token");
        var refresh = JsonValue.String(root, "refresh_token", oldRefresh ?? "");
        if (access.Length == 0 || refresh.Length == 0) throw new SignInRequiredException("Spotify returned an incomplete sign-in. Please reconnect.");
        return new(access, refresh, DateTimeOffset.UtcNow.AddSeconds(JsonValue.Int(root, "expires_in")), JsonValue.String(root, "scope", previousScope ?? ""));
    }
}
