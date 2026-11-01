using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Pebble.Services;

/// <summary>
/// 极简静态文件服务器：以 Git 仓库根为根目录，让电脑浏览器预览手机上的网页。
///
/// 没有用 HttpListener：它在 Windows 上必须先执行
/// <c>netsh http add urlacl url=http://+:8080/ user=Everyone</c>（管理员），
/// 否则 Start() 直接抛 Access Denied；Android 上行为又不完全一致。
/// TcpListener 自己实现 HTTP/1.1 反而更小、更可控，两端表现一致。
/// </summary>
public sealed class LocalServerService : IDisposable
{
    public static LocalServerService Shared { get; } = new();

    private TcpListener? _tcp;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public bool IsRunning => _tcp is not null;
    public int Port { get; private set; } = 8080;
    public string Root { get; private set; } = string.Empty;
    public string? LanIp { get; private set; }

    public string Url => LanIp is null ? $"http://127.0.0.1:{Port}/" : $"http://{LanIp}:{Port}/";

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".mjs"] = "application/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".xml"] = "application/xml; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/plain; charset=utf-8",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".wasm"] = "application/wasm",
        [".pdf"] = "application/pdf",
        [".mp4"] = "video/mp4",
        [".mp3"] = "audio/mpeg"
    };

    /// <summary>启动服务并返回可访问 URL。端口从 preferredPort 起自动找空位（最多 20 个）。</summary>
    public string Start(string rootPath, int preferredPort = 8080)
    {
        Stop();

        Root = Path.GetFullPath(rootPath);
        LanIp = GetLanIp();

        SocketException? lastError = null;
        for (var port = preferredPort; port < preferredPort + 20; port++)
        {
            try
            {
                var candidate = new TcpListener(IPAddress.Any, port);
                candidate.Start();
                _tcp = candidate;
                Port = port;
                break;
            }
            catch (SocketException ex)
            {
                lastError = ex;   // 端口被占（本机常有代理软件监听 8080），换下一个
            }
        }

        if (_tcp is null)
        {
            throw new IOException(
                $"端口 {preferredPort}–{preferredPort + 19} 都被占用：{lastError?.Message}", lastError);
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(_tcp, _cts.Token));

        return Url;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch (Exception) { }
        try { _tcp?.Stop(); } catch (Exception) { }

        _tcp = null;
        _loop = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                break;   // Stop() 会让 Accept 抛异常，这里正常退出
            }

            _ = Task.Run(() => HandleClientAsync(client), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 10000;

                await using var stream = client.GetStream();

                var requestLine = await ReadRequestHeadAsync(stream);
                if (requestLine is null) return;

                var parts = requestLine.Split(' ');
                if (parts.Length < 2) return;

                var method = parts[0];
                var target = parts[1];

                if (method is not ("GET" or "HEAD"))
                {
                    await WriteTextAsync(stream, 405, "405 Method Not Allowed");
                    return;
                }

                var path = target.Split('?')[0];
                var requestPath = Uri.UnescapeDataString(path);

                // 只拦 .git 这一段，别误伤 .gitignore 这类文件
                if (requestPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                {
                    await WriteTextAsync(stream, 403, "403 Forbidden");
                    return;
                }

                var full = Path.GetFullPath(Path.Combine(Root, requestPath.TrimStart('/')));

                // 目录穿越防护
                if (!full.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTextAsync(stream, 403, "403 Forbidden");
                    return;
                }

                if (Directory.Exists(full))
                {
                    var index = Path.Combine(full, "index.html");
                    if (File.Exists(index))
                    {
                        await WriteFileAsync(stream, index, method == "HEAD");
                        return;
                    }

                    await WriteHtmlAsync(stream, BuildDirectoryListing(full, requestPath));
                    return;
                }

                if (!File.Exists(full))
                {
                    await WriteTextAsync(stream, 404, $"404 Not Found\n{requestPath}");
                    return;
                }

                await WriteFileAsync(stream, full, method == "HEAD");
            }
            catch (Exception ex)
            {
                AppLog.Error("http", ex);
            }
        }
    }

    /// <summary>读到空行为止，只返回首行（请求行），其余头直接丢弃。</summary>
    private static async Task<string?> ReadRequestHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var received = 0;

        while (received < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(received, buffer.Length - received));
            if (read == 0) break;
            received += read;

            var text = Encoding.ASCII.GetString(buffer, 0, received);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
                return text[..text.IndexOf("\r\n", StringComparison.Ordinal)];

            if (text.Contains("\n\n"))   // 少数客户端只发 \n
                return text[..text.IndexOf('\n')];
        }

        return null;
    }

    private static async Task WriteHeadersAsync(NetworkStream stream, int status, string reason, string contentType, long length)
    {
        var header = $"HTTP/1.1 {status} {reason}\r\n" +
                     $"Content-Type: {contentType}\r\n" +
                     $"Content-Length: {length}\r\n" +
                     "Cache-Control: no-store\r\n" +
                     "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
    }

    private static async Task WriteTextAsync(NetworkStream stream, int status, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await WriteHeadersAsync(stream, status, ReasonOf(status), "text/plain; charset=utf-8", bytes.Length);
        await stream.WriteAsync(bytes);
    }

    private static async Task WriteHtmlAsync(NetworkStream stream, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        await WriteHeadersAsync(stream, 200, "OK", "text/html; charset=utf-8", bytes.Length);
        await stream.WriteAsync(bytes);
    }

    private static async Task WriteFileAsync(NetworkStream stream, string path, bool headOnly)
    {
        var info = new FileInfo(path);
        var contentType = MimeTypes.TryGetValue(info.Extension, out var mime)
            ? mime
            : "application/octet-stream";

        await WriteHeadersAsync(stream, 200, "OK", contentType, info.Length);

        if (headOnly) return;

        await using var file = File.OpenRead(path);
        await file.CopyToAsync(stream);
    }

    private static string ReasonOf(int status) => status switch
    {
        200 => "OK",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        _ => "Error"
    };

    private string BuildDirectoryListing(string directory, string requestPath)
    {
        var builder = new StringBuilder();
        builder.Append("<!DOCTYPE html><meta charset=\"utf-8\">")
               .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
               .Append("<style>body{font:14px/1.6 -apple-system,system-ui,sans-serif;background:#0b0e16;color:#e9ecf5;padding:24px}")
               .Append("a{color:#8b7cf6;text-decoration:none}li{margin:6px 0}code{color:#9aa1b2}</style>")
               .Append($"<h3>/{WebUtility.HtmlEncode(requestPath.TrimStart('/'))}</h3><ul>");

        if (requestPath != "/")
            builder.Append("<li><a href=\"../\">../</a></li>");

        foreach (var dir in Directory.GetDirectories(directory)
                     .Where(d => !Path.GetFileName(d).Equals(".git", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(Path.GetFileName))
        {
            var name = Path.GetFileName(dir);
            builder.Append($"<li><a href=\"{Uri.EscapeDataString(name)}/\">{WebUtility.HtmlEncode(name)}/</a></li>");
        }

        foreach (var file in Directory.GetFiles(directory).OrderBy(Path.GetFileName))
        {
            var name = Path.GetFileName(file);
            builder.Append($"<li><a href=\"{Uri.EscapeDataString(name)}\">{WebUtility.HtmlEncode(name)}</a> ")
                   .Append($"<code>{FileOperations.FormatSize(FileOperations.SizeOf(file))}</code></li>");
        }

        builder.Append("</ul>");
        return builder.ToString();
    }

    /// <summary>取手机在局域网里的 IPv4（跳过 127.x 与 169.254.x）。</summary>
    public static string? GetLanIp()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211
                                                 or NetworkInterfaceType.Ethernet)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Where(ip => !ip.StartsWith("127.", StringComparison.Ordinal))
                .Where(ip => !ip.StartsWith("169.254.", StringComparison.Ordinal))
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            AppLog.Error("lan-ip", ex);
            return null;
        }
    }

    public void Dispose() => Stop();
}
