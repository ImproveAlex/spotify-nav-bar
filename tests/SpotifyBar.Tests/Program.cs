using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SpotifyBar;
using SpotifyBar.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Saved remote and former browser sessions refresh without requiring new consent", async () =>
    {
        foreach (var scope in new[] { "", "streaming user-read-email user-read-private " + SpotifyAuth.RequestedScopes })
        {
            using var client = new HttpClient(new FakeHandler(_ => Response(HttpStatusCode.OK, """{"access_token":"new-access","expires_in":3600}""")));
            var store = new MemoryStore(new("old-access", "refresh", DateTimeOffset.UtcNow.AddMinutes(-1), scope));
            var auth = Auth(client, store);
            Assert(auth.IsConnected, "Existing sessions must remain usable after rollback.");
            Equal("new-access", await auth.GetAccessTokenAsync(default));
            Equal(scope, store.Value?.Scope);
        }
    }),
    ("Portable storage uses Windows Documents regardless of the working directory", () =>
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Equal(Path.Combine(documents, "SpotifyBar"), PortablePaths.GetDataRoot(false, @"C:\An unrelated working folder"));
        var isolated = PortablePaths.GetDataRoot(true, Environment.CurrentDirectory);
        Assert(isolated.StartsWith(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts")) + Path.DirectorySeparatorChar), "Smoke data must remain inside the test workspace.");
        return Task.CompletedTask;
    }),
    ("First launch creates Documents settings and usable help without changing user notes", () =>
    {
        using var directory = new TestDirectory();
        var root = Path.Combine(directory.Path, "Documents", "SpotifyBar");
        Storage.InitializeDirectory(root);
        Assert(File.Exists(Path.Combine(root, "settings.json")), "Default preferences must be saved in the data folder.");
        Assert(File.ReadAllText(Path.Combine(root, "README.txt")).Contains("Runtime"), "User help must describe the saved runtime files.");
        Assert(File.ReadAllText(Path.Combine(root, "SPOTIFY-SETUP.txt")).Contains(LoopbackCallback.RedirectUri), "Saved setup help must include the registered callback.");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "my own notes");
        var written = File.GetLastWriteTimeUtc(Path.Combine(root, "README.txt"));
        Storage.InitializeDirectory(root);
        Equal("my own notes", File.ReadAllText(Path.Combine(root, "notes.txt")));
        Equal(written, File.GetLastWriteTimeUtc(Path.Combine(root, "README.txt")));
        return Task.CompletedTask;
    }),
    ("Legacy migration preserves encrypted sessions and leaves unrelated files untouched", () =>
    {
        using var directory = new TestDirectory();
        var legacy = Path.Combine(directory.Path, "old-appdata");
        var root = Path.Combine(directory.Path, "Documents", "SpotifyBar");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), """{"ClientId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","ReserveSpace":false}""");
        var tokens = new TokenSet("migration-access", "migration-refresh", DateTimeOffset.UtcNow.AddHours(1));
        new WindowsTokenStore(Path.Combine(legacy, "session.dat")).Save(tokens);
        File.WriteAllText(Path.Combine(legacy, "errors.log"), "older error log");
        File.WriteAllText(Path.Combine(legacy, "unrelated.txt"), "leave this alone");
        Storage.InitializeDirectory(root, legacy);
        Equal(tokens, new WindowsTokenStore(Path.Combine(root, "session.dat")).Load());
        Assert(File.ReadAllText(Path.Combine(root, "settings.json")).Contains(new string('a', 32)), "Saved Client ID must migrate intact.");
        Equal("older error log", File.ReadAllText(Path.Combine(root, "errors.log")));
        Assert(!File.Exists(Path.Combine(legacy, "settings.json")) && !File.Exists(Path.Combine(legacy, "session.dat")), "App-owned data should move out of the legacy folder.");
        Equal("leave this alone", File.ReadAllText(Path.Combine(legacy, "unrelated.txt")));
        return Task.CompletedTask;
    }),
    ("Migration never replaces current Documents data with older settings", () =>
    {
        using var directory = new TestDirectory();
        var legacy = Path.Combine(directory.Path, "old-appdata");
        var root = Path.Combine(directory.Path, "Documents", "SpotifyBar");
        Directory.CreateDirectory(legacy);
        Storage.InitializeDirectory(root);
        var current = File.ReadAllText(Path.Combine(root, "settings.json"));
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "older settings");
        Storage.InitializeDirectory(root, legacy);
        Equal(current, File.ReadAllText(Path.Combine(root, "settings.json")));
        var backups = Directory.GetFiles(Path.Combine(root, "PreviousData"));
        Equal(1, backups.Length); Equal("older settings", File.ReadAllText(backups[0]));
        return Task.CompletedTask;
    }),
    ("Portable launcher arguments round-trip through the Windows command-line parser", () =>
    {
        foreach (var value in new[] { "", "--demo", "two words", "a\"quoted value", @"C:\folder with spaces\", "music 🎵", @"backslash\""quote" })
        {
            var pointer = WindowsArguments.CommandLineToArgvW("launcher.exe " + PortablePaths.QuoteArgument(value), out var count);
            try { Equal(2, count); Equal(value, Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, IntPtr.Size))); }
            finally { WindowsArguments.LocalFree(pointer); }
        }
        return Task.CompletedTask;
    }),
    ("PKCE matches the RFC 7636 challenge", () =>
    {
        Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", SpotifyAuth.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var verifier = SpotifyAuth.CreateVerifier();
        Assert(verifier.Length >= 43 && verifier.Length <= 128 && verifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), "Verifier must satisfy PKCE character and length rules.");
        return Task.CompletedTask;
    }),
    ("Track links reject hostile hosts and malformed IDs", () =>
    {
        const string id = "4iV5W9uYEdYUVa79Axb7Rh";
        foreach (var link in new[] { "spotify:track:" + id, "https://open.spotify.com/track/" + id + "?si=example", "https://open.spotify.com/intl-es/track/" + id })
        { Assert(SpotifyLinks.TryParseTrack(link, out var uri), "Expected a valid track link."); Equal("spotify:track:" + id, uri); }
        foreach (var link in new[] { "https://open.spotify.com.evil.test/track/" + id, "http://open.spotify.com/track/" + id, "https://open.spotify.com/playlist/" + id, "spotify:track:abc", "spotify:track:" + id + "?foo" })
            Assert(!SpotifyLinks.TryParseTrack(link, out _), "Invalid link was accepted.");
        return Task.CompletedTask;
    }),
    ("Playback handles missing items and device restrictions", () =>
    {
        using var json = JsonDocument.Parse("""{"item":null,"progress_ms":null,"is_playing":false,"device":{"id":"pc","name":"PC","is_restricted":true,"supports_volume":false,"volume_percent":null},"actions":{"disallows":{"seeking":true,"pausing":false}}}""");
        var state = Playback.FromJson(json.RootElement);
        Assert(state.Track is null && state.ProgressMs == 0 && state.Device?.SupportsVolume == false && state.Device.Volume is null, "Null playback fields should remain safe.");
        Assert(!state.Allows("seeking") && !state.Allows("resuming"), "A restricted device must disable controls.");
        Assert(state.Disallowed.Contains("seeking") && !state.Disallowed.Contains("pausing"), "Only true restrictions should be applied.");
        return Task.CompletedTask;
    }),
    ("Episode metadata uses the show and its artwork", () =>
    {
        using var json = JsonDocument.Parse("""{"id":"episode","name":"An episode","uri":"spotify:episode:example","duration_ms":1234,"show":{"name":"A show"},"images":[{"url":"https://example.test/art.jpg"}]}""");
        var track = Track.FromJson(json.RootElement);
        Equal("A show", track.Artist); Equal("https://example.test/art.jpg", track.ImageUrl);
        return Task.CompletedTask;
    }),
    ("No active playback (204) is a valid idle state", async () =>
    {
        using var client = new HttpClient(new FakeHandler(_ => Response(HttpStatusCode.NoContent)));
        var api = new SpotifyApi(client, Auth(client));
        Assert(await api.GetPlaybackAsync(default) is null, "204 should yield no playback, not a parse failure.");
    }),
    ("Playback commands target devices and encode track URIs", async () =>
    {
        var calls = new List<string>();
        using var client = new HttpClient(new FakeHandler(request => { calls.Add(request.Method + " " + request.RequestUri!.PathAndQuery); return Response(HttpStatusCode.NoContent); }));
        var api = new SpotifyApi(client, Auth(client));
        await api.SeekAsync("pc & office", 12345, default);
        await api.QueueAsync("pc & office", "spotify:track:4iV5W9uYEdYUVa79Axb7Rh", default);
        await api.VolumeAsync("pc", 400, default);
        Assert(calls[0].Contains("position_ms=12345&device_id=pc%20%26%20office"), "Device identifiers must be encoded.");
        Assert(calls[1].Contains("uri=spotify%3Atrack%3A"), "Queue URIs must be encoded.");
        Assert(calls[2].Contains("volume_percent=100"), "Volume must be clamped to Spotify's range.");
    }),
    ("Search sends a bounded encoded query and does not call the API for invalid links", async () =>
    {
        var calls = 0;
        using var client = new HttpClient(new FakeHandler(request =>
        {
            calls++;
            Assert(request.RequestUri!.Query.Contains("q=title%20%26%20artist"), "Search query must be encoded.");
            return Response(HttpStatusCode.OK, """{"tracks":{"items":[]}}""");
        }));
        var api = new SpotifyApi(client, Auth(client));
        await api.SearchAsync("title & artist", default);
        await Throws<ArgumentException>(() => api.SearchAsync("https://evil.test/track/anything", default));
        Equal(1, calls);
    }),
    ("Concurrent expired-token requests share one refresh and retain the refresh token", async () =>
    {
        var refreshCalls = 0;
        using var client = new HttpClient(new FakeHandler(_ =>
        { Interlocked.Increment(ref refreshCalls); return Response(HttpStatusCode.OK, """{"access_token":"new-access","expires_in":3600}"""); }));
        var store = new MemoryStore(new("old-access", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var auth = Auth(client, store);
        var values = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => auth.GetAccessTokenAsync(default)));
        Equal(1, refreshCalls); Assert(values.All(value => value == "new-access"), "All requests should see the refreshed token.");
        Equal("old-refresh", store.Value?.RefreshToken);
    }),
    ("A 401 refreshes once, then retries with the new bearer token", async () =>
    {
        var requests = 0;
        using var client = new HttpClient(new FakeHandler(request =>
        {
            requests++;
            if (request.RequestUri!.Host == "accounts.spotify.com") return Response(HttpStatusCode.OK, """{"access_token":"new-access","expires_in":3600}""");
            return request.Headers.Authorization?.Parameter == "new-access" ? Response(HttpStatusCode.NoContent) : Response(HttpStatusCode.Unauthorized);
        }));
        var api = new SpotifyApi(client, Auth(client));
        await api.GetPlaybackAsync(default);
        Equal(3, requests);
    }),
    ("A repeated 401 stops rather than refreshing indefinitely", async () =>
    {
        var calls = 0;
        using var client = new HttpClient(new FakeHandler(request =>
        { calls++; return request.RequestUri!.Host == "accounts.spotify.com" ? Response(HttpStatusCode.OK, """{"access_token":"new-access","expires_in":3600}""") : Response(HttpStatusCode.Unauthorized); }));
        await Throws<SignInRequiredException>(() => new SpotifyApi(client, Auth(client)).GetPlaybackAsync(default));
        Equal(3, calls);
    }),
    ("A 429 suspends subsequent requests for the Retry-After interval", async () =>
    {
        var calls = 0;
        using var client = new HttpClient(new FakeHandler(_ =>
        { calls++; var response = Response(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new(TimeSpan.FromSeconds(30)); return response; }));
        var api = new SpotifyApi(client, Auth(client));
        await Throws<SpotifyApiException>(() => api.GetPlaybackAsync(default));
        await Throws<SpotifyApiException>(() => api.GetDevicesAsync(default));
        Equal(1, calls); Assert(api.RetryAt > DateTimeOffset.UtcNow.AddSeconds(25), "Retry-After must govern all endpoints.");
    }),
    ("Revoked refresh tokens are cleared; transient server errors preserve the session", async () =>
    {
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.ServiceUnavailable })
        {
            using var client = new HttpClient(new FakeHandler(_ => Response(status)));
            var store = new MemoryStore(new("expired-access", "refresh", DateTimeOffset.UtcNow.AddMinutes(-1)));
            var auth = Auth(client, store);
            if (status == HttpStatusCode.BadRequest) { await Throws<SignInRequiredException>(() => auth.GetAccessTokenAsync(default)); Assert(store.Value is null, "Revoked tokens must be cleared."); }
            else { await Throws<SpotifyApiException>(() => auth.GetAccessTokenAsync(default)); Assert(store.Value is not null, "A temporary server failure must preserve the refresh token."); }
        }
    }),
    ("Sign-out cannot be undone by an in-flight refresh", async () =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new AsyncHandler(async _ =>
        { entered.SetResult(); await finish.Task; return Response(HttpStatusCode.OK, """{"access_token":"late-access","expires_in":3600}"""); }));
        var store = new MemoryStore(new("expired", "refresh", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var auth = Auth(client, store);
        var refresh = auth.GetAccessTokenAsync(default);
        await entered.Task;
        var signOut = auth.SignOutAsync();
        finish.SetResult();
        await Throws<SignInRequiredException>(async () => { await refresh; });
        await signOut;
        Assert(!auth.IsConnected && store.Value is null, "Late refresh must not resurrect a signed-out account.");
    }),
    ("Loopback callback rejects wrong and duplicate state before accepting a valid callback", async () =>
    {
        using var callback = new LoopbackCallback(0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var code = callback.WaitForCodeAsync("expected", timeout.Token);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        var prefix = $"http://127.0.0.1:{callback.Port}/callback?";
        using var wrong = await client.GetAsync(prefix + "code=bad&state=wrong", timeout.Token);
        Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using var duplicate = await client.GetAsync(prefix + "code=bad&state=expected&state=wrong", timeout.Token);
        Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using var valid = await client.GetAsync(prefix + "code=good&state=expected", timeout.Token);
        Equal(HttpStatusCode.OK, valid.StatusCode); Equal("good", await code);
    }),
    ("Windows token storage encrypts, round-trips, clears and rejects tampered data", () =>
    {
        var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "session.dat");
        var tokens = new TokenSet("dummy_access_token", "dummy_refresh_token", DateTimeOffset.UtcNow.AddHours(1));
        var store = new WindowsTokenStore(path);
        try
        {
            store.Save(tokens);
            Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("dummy_access_token"), "Token must not be stored in plaintext.");
            Equal(tokens, store.Load());
            File.WriteAllBytes(path, [1, 2, 3, 4]); Assert(store.Load() is null, "Tampering must fail closed.");
            store.Clear(); Assert(!File.Exists(path), "Sign-out must remove persisted tokens.");
        }
        finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(folder)) Directory.Delete(folder); }
        return Task.CompletedTask;
    })
};

var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + test.Name + " — " + ex.GetType().Name + ": " + ex.Message); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} checks passed.");
return failed == 0 ? 0 : 1;

static HttpResponseMessage Response(HttpStatusCode status, string? json = null) => new(status)
{ Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json") };
static SpotifyAuth Auth(HttpClient client, MemoryStore? store = null) => new(client,
    store ?? new MemoryStore(new("old-access", "old-refresh", DateTimeOffset.UtcNow.AddHours(1)))) { ClientId = new string('a', 32) };
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException("Unexpected result."); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{ try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

sealed class MemoryStore(TokenSet? initial) : ITokenStore
{
    public TokenSet? Value { get; private set; } = initial;
    public TokenSet? Load() => Value;
    public void Save(TokenSet tokens) => Value = tokens;
    public void Clear() => Value = null;
}
sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(respond(request));
}
sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
}

sealed class TestDirectory : IDisposable
{
    private readonly string _parent = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts", "tests"));
    public string Path { get; }
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(_parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }
    public void Dispose()
    {
        var resolved = System.IO.Path.GetFullPath(Path);
        if (!resolved.StartsWith(_parent + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(System.IO.Path.GetFileName(resolved), "N", out _))
            throw new InvalidOperationException("Refusing to delete a path outside the test fixture directory.");
        Directory.Delete(resolved, recursive: true);
    }
}

static class WindowsArguments
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr pointer);
}
