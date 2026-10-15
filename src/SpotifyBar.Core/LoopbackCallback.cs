using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SpotifyBar.Core;

// Listen only on the IPv4 loopback interface; this requires no administrator URL reservation.
public sealed class LoopbackCallback : IDisposable
{
    private readonly TcpListener _listener;
    public const string RedirectUri = "http://127.0.0.1:43821/callback";

    public LoopbackCallback(int port = 43821)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start(4);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async Task<string> WaitForCodeAsync(string expectedState, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var stream = client.GetStream();
            try
            {
                var header = new List<byte>();
                var buffer = new byte[1];
                while (header.Count < 8192)
                {
                    if (await stream.ReadAsync(buffer, requestTimeout.Token).ConfigureAwait(false) == 0) break;
                    header.Add(buffer[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10
                        && header[^2] == 13 && header[^1] == 10) break;
                }
                var line = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")[0].Split(' ');
                if (line.Length != 3 || line[0] != "GET" || !line[1].StartsWith("/callback?", StringComparison.Ordinal)
                    || header.Count >= 8192)
                {
                    await RespondAsync(stream, 400, "Invalid callback.", requestTimeout.Token).ConfigureAwait(false);
                    continue;
                }
                var request = new Uri("http://127.0.0.1" + line[1]);
                var query = ParseQuery(request.Query);
                if (!query.TryGetValue("state", out var state) || !StateMatches(expectedState, state))
                {
                    await RespondAsync(stream, 400, "Invalid sign-in state. Return to Spotify Bar.", requestTimeout.Token).ConfigureAwait(false);
                    continue;
                }
                if (query.ContainsKey("error"))
                {
                    await RespondAsync(stream, 200, "Sign-in cancelled. You can close this tab.", requestTimeout.Token).ConfigureAwait(false);
                    throw new SignInRequiredException("Spotify sign-in was declined. You can try again.");
                }
                if (query.TryGetValue("code", out var code) && !string.IsNullOrWhiteSpace(code))
                {
                    await RespondAsync(stream, 200, "Connected to Spotify. You can close this tab and return to Spotify Bar.", requestTimeout.Token).ConfigureAwait(false);
                    return code;
                }
                await RespondAsync(stream, 400, "Missing authorization code.", requestTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (IOException) { }
            catch (UriFormatException) { }
        }
    }

    private static Dictionary<string, string> ParseQuery(string input)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in input.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var name = Uri.UnescapeDataString(parts[0]);
            if (!result.TryAdd(name, parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : ""))
                return new Dictionary<string, string>(); // Reject ambiguous duplicate parameters.
        }
        return result;
    }

    private static bool StateMatches(string expected, string actual) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    private static async Task RespondAsync(NetworkStream stream, int code, string text, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Spotify Bar</title>"
            + "<body style=\"font:18px system-ui;background:#141618;color:#f4f5f5;padding:48px\"><h1>Spotify Bar</h1><p>"
            + WebUtility.HtmlEncode(text) + "</p></body></html>");
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {(code == 200 ? "OK" : "Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'\r\n\r\n");
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    public void Dispose() => _listener.Stop();
}
