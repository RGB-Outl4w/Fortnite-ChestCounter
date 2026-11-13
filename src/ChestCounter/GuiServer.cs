using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ChestCounter;

/// <summary>
/// The graphical interface (prototype): a page in the look of the report, served by the counter itself on this PC only
/// (127.0.0.1, random port) and shown in an app window of Edge or Chrome. The page reads the state every 2 seconds and
/// sends the player's choices back; nothing leaves the PC.
/// Protection: a random token in the address and in every request (another web page cannot guess it), and only
/// requests whose Host is this address are answered (no DNS rebinding).
/// </summary>
public sealed class GuiServer
{
    /// <summary>Same port from one launch to the next when it is free: the window keeps its place and its settings.</summary>
    private const int PreferredPort = 47613;
    private TcpListener _listener = new(IPAddress.Loopback, PreferredPort);
    private readonly string _token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private readonly Func<object> _state;
    private readonly Func<string, JsonElement, object?> _action;
    private readonly string _dataFolder;
    private readonly string _reportPath;
    private readonly string _overlayPath;

    /// <param name="state">The whole state shown by the page (serialized to JSON).</param>
    /// <param name="action">Runs an action (name, value) and returns its result (or null).</param>
    public GuiServer(Func<object> state, Func<string, JsonElement, object?> action, string dataFolder, string reportPath, string overlayPath)
    {
        _overlayPath = overlayPath;
        _state = state;
        _action = action;
        _dataFolder = dataFolder;
        _reportPath = reportPath;
    }

    public int Port { get; private set; }
    public string Url => $"http://127.0.0.1:{Port}/?t={_token}";

    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (SocketException)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0); // taken (a second data folder, another program): any free port
            _listener.Start();
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(Loop) { IsBackground = true, Name = "Gui" }.Start();
    }

    /// <summary>Opens the interface: an app window of Edge or Chrome (no tabs, no address bar), else the default browser.</summary>
    public void Open()
    {
        foreach (var exe in new[] { "msedge.exe", "chrome.exe" })
        {
            if (AppPath(exe) is { } path)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(path, $"--app=\"{Url}\" --window-size=1180,860") { UseShellExecute = false })?.Dispose();
                    return;
                }
                catch
                {
                    // try the next one
                }
            }
        }
        try { Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true })?.Dispose(); } catch { }
    }

    private static string? AppPath(string exe)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (key?.GetValue(null) is string p && File.Exists(p.Trim('"')))
                {
                    return p.Trim('"');
                }
            }
            catch
            {
                // not readable: next one
            }
        }
        return null;
    }

    private void Loop()
    {
        while (true)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { return; }
            ThreadPool.QueueUserWorkItem(_ => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                var stream = client.GetStream();
                var (method, target, headers, body) = Read(stream);
                if (method == null)
                {
                    return;
                }
                var host = headers.GetValueOrDefault("host", "");
                if (host != $"127.0.0.1:{Port}" && host != $"localhost:{Port}")
                {
                    Write(stream, 403, "text/plain", "Forbidden"u8.ToArray());
                    return;
                }
                var path = target.Split('?')[0];
                var query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";
                var tokenOk = headers.GetValueOrDefault("x-token") == _token || query.Split('&').Contains("t=" + _token);

                if (method == "GET" && path == "/" && tokenOk)
                {
                    Write(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(GuiPage.Html(_token)));
                }
                else if (method == "GET" && path == "/api/state" && tokenOk)
                {
                    Write(stream, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(_state()));
                }
                else if (method == "POST" && path == "/api/action" && tokenOk)
                {
                    using var doc = JsonDocument.Parse(body.Length > 0 ? body : "{}"u8.ToArray());
                    var name = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                    var value = doc.RootElement.TryGetProperty("value", out var v) ? v.Clone() : default;
                    var result = _action(name, value);
                    Write(stream, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, result }));
                }
                else if (method == "GET" && path == "/report" && tokenOk && File.Exists(_reportPath))
                {
                    // Shown inside the window: no reload of its own, the window reloads it when the figures change.
                    var html = File.ReadAllText(_reportPath);
                    html = System.Text.RegularExpressions.Regex.Replace(html, "<meta http-equiv=\"refresh\"[^>]*>", "");
                    Write(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
                }
                else if (method == "GET" && path == "/overlay" && tokenOk && File.Exists(_overlayPath))
                {
                    Write(stream, 200, "text/html; charset=utf-8", File.ReadAllBytes(_overlayPath));
                }
                else if (method == "GET" && path.StartsWith("/images/", StringComparison.Ordinal))
                {
                    // Pictures of the report (no token: they are public pictures, and the name cannot leave the folder).
                    var name = Path.GetFileName(Uri.UnescapeDataString(path["/images/".Length..]));
                    var file = Path.Combine(_dataFolder, "images", name);
                    if (name.Length > 0 && File.Exists(file))
                    {
                        Write(stream, 200, name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/webp", File.ReadAllBytes(file));
                    }
                    else
                    {
                        Write(stream, 404, "text/plain", "Not found"u8.ToArray());
                    }
                }
                else
                {
                    Write(stream, tokenOk ? 404 : 403, "text/plain", "No"u8.ToArray());
                }
            }
            catch
            {
                // a closed window or a broken request: nothing to do
            }
        }
    }

    private static (string? Method, string Target, Dictionary<string, string> Headers, byte[] Body) Read(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        // Headers, up to the empty line (at most 32 KB).
        while (buffer.Length < 32768)
        {
            if (stream.Read(one, 0, 1) != 1)
            {
                return (null, "", new(), Array.Empty<byte>());
            }
            buffer.WriteByte(one[0]);
            var b = buffer.GetBuffer();
            var n = (int)buffer.Length;
            if (n >= 4 && b[n - 4] == '\r' && b[n - 3] == '\n' && b[n - 2] == '\r' && b[n - 1] == '\n')
            {
                break;
            }
        }
        var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2)
        {
            return (null, "", new(), Array.Empty<byte>());
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }
        }
        var length = int.TryParse(headers.GetValueOrDefault("content-length"), out var l) ? Math.Clamp(l, 0, 1 << 20) : 0;
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var r = stream.Read(body, read, length - read);
            if (r <= 0) break;
            read += r;
        }
        return (first[0], first[1], headers, body);
    }

    private static void Write(NetworkStream stream, int status, string type, byte[] content)
    {
        var reason = status switch { 200 => "OK", 403 => "Forbidden", _ => "Not Found" };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {content.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        stream.Write(Encoding.ASCII.GetBytes(head));
        stream.Write(content);
    }
}
