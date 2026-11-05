using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WindowsVerify;

internal sealed class LocalHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private LocalHttpServer(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _loop = AcceptLoopAsync();
    }

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public int ActiveWebSockets => Volatile.Read(ref _activeWebSockets);
    private int _activeWebSockets;

    public static Task<LocalHttpServer> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new LocalHttpServer(listener));
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = HandleAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException) when (_cts.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(_cts.Token);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? line;
                do
                {
                    line = await reader.ReadLineAsync(_cts.Token);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        var separator = line.IndexOf(':');
                        if (separator > 0)
                        {
                            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                        }
                    }
                } while (!string.IsNullOrEmpty(line));

                var path = requestLine?.Split(' ').ElementAtOrDefault(1) ?? "/";
                if (path == "/ws"
                    && headers.TryGetValue("Upgrade", out var upgrade)
                    && upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleWebSocketAsync(stream, headers, _cts.Token);
                    return;
                }
                if (path == "/media.webm")
                {
                    var media = await File.ReadAllBytesAsync(
                        Path.Combine(AppContext.BaseDirectory, "assets", "sample.webm"), _cts.Token);
                    var start = 0;
                    var end = media.Length - 1;
                    var partial = false;
                    if (headers.TryGetValue("Range", out var range)
                        && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = range[6..].Split('-', 2);
                        if (int.TryParse(parts[0], out var requestedStart))
                        {
                            start = Math.Clamp(requestedStart, 0, media.Length - 1);
                            if (parts.Length > 1 && int.TryParse(parts[1], out var requestedEnd))
                                end = Math.Clamp(requestedEnd, start, media.Length - 1);
                            partial = true;
                        }
                    }
                    var length = end - start + 1;
                    var status = partial ? "206 Partial Content" : "200 OK";
                    var mediaHeader = $"HTTP/1.1 {status}\r\nContent-Type: video/webm\r\n" +
                                      "Accept-Ranges: bytes\r\n" +
                                      (partial ? $"Content-Range: bytes {start}-{end}/{media.Length}\r\n" : "") +
                                      $"Content-Length: {length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(mediaHeader), _cts.Token);
                    await stream.WriteAsync(media.AsMemory(start, length), _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                    return;
                }
                if (path == "/slow.html")
                {
                    await Task.Delay(3_000, _cts.Token);
                }
                var (status, contentType, body, extraHeaders) = Response(path);
                var bytes = Encoding.UTF8.GetBytes(body);
                var header = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}; charset=utf-8\r\n" +
                             extraHeaders +
                             $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                var headerBytes = Encoding.ASCII.GetBytes(header);
                await stream.WriteAsync(headerBytes, _cts.Token);
                await stream.WriteAsync(bytes, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    private static (string Status, string ContentType, string Body, string ExtraHeaders) Response(string path)
    {
        var page = path switch
        {
            "/" or "/page1.html" => """
                <!doctype html><html><head><title>Page One</title></head><body>
                <h1>Page One</h1><a id="next" href="/page2.html">next</a>
                <button id="popup" onclick="window.open('/popup.html','_blank')">popup</button>
                </body></html>
                """,
            "/page2.html" => """
                <!doctype html><html><head><title>Page Two</title></head><body><h1>Page Two</h1></body></html>
                """,
            "/popup.html" => """
                <!doctype html><html><head><title>Popup</title></head><body><h1>Popup</h1></body></html>
                """,
            "/compat.html" => """
                <!doctype html><html><head><title>Compatibility</title></head><body>
                <h1>Compatibility</h1>
                <button id="spa-button" onclick="document.getElementById('spa').textContent='spa-updated'">spa</button>
                <div id="spa">initial</div>
                <input id="upload" type="file" />
                <video id="video" controls src="/media.webm"></video>
                <button id="video-play" onclick="document.getElementById('video').play().catch(()=>{})">video</button>
                <button id="fullscreen" onclick="document.getElementById('video').requestFullscreen().catch(()=>{})">fullscreen</button>
                <button id="pip" onclick="document.getElementById('video').requestPictureInPicture().catch(()=>{})">pip</button>
                <audio id="audio" controls></audio>
                <button id="audio-play" onclick="document.getElementById('audio').play().catch(()=>{})">audio</button>
                <a id="redirect" href="/redirect">redirect</a>
                </body></html>
                """,
            "/slow.html" => """
                <!doctype html><html><head><title>Slow</title></head><body><h1>Slow</h1></body></html>
                """,
            "/download.txt" => "AMARE download probe",
            "/redirect" => "",
            "/ws-hold.html" => """
                <!doctype html><html><head><title>WebSocket Hold</title></head><body>
                <h1>WebSocket Hold</h1>
                <script>
                window.__wsOpen = false;
                const ws = new WebSocket('ws://' + location.host + '/ws');
                ws.onopen = () => { window.__wsOpen = true; };
                </script>
                </body></html>
                """,
            "/ws.html" => """
                <!doctype html><html><head><title>WebSocket</title></head><body>
                <h1>WebSocket</h1>
                <script>
                window.__wsState = 'connecting';
                window.__wsMessage = '';
                window.__wsReconnect = '';
                function connect(suffix) {
                  const ws = new WebSocket('ws://' + location.host + '/ws');
                  ws.onopen = () => { window.__wsState = 'open'; ws.send('amare-' + suffix); };
                  ws.onmessage = (event) => {
                    if (suffix === 'ping') {
                      window.__wsMessage = event.data;
                      ws.close();
                      setTimeout(() => connect('reconnect'), 100);
                    } else {
                      window.__wsReconnect = event.data;
                      window.__wsState = 'closed';
                    }
                  };
                  ws.onerror = () => { window.__wsState = 'error'; };
                }
                connect('ping');
                </script>
                </body></html>
                """,
            _ => null,
        };
        if (page is null)
        {
            return ("404 Not Found", "text/html", "<html><body>not found</body></html>", "");
        }
        var headers = path == "/download.txt"
            ? "Content-Disposition: attachment; filename=\"probe.txt\"\r\n"
            : "";
        var contentType = path == "/download.txt" ? "text/plain" : "text/html";
        if (path == "/redirect")
        {
            return ("302 Found", "text/html", "", "Location: /page2.html\r\n");
        }
        return ("200 OK", contentType, page, headers);
    }

    private async Task HandleWebSocketAsync(
        NetworkStream stream,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key))
        {
            return;
        }
        var accept = Convert.ToBase64String(SHA1.HashData(
            Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var handshake = "HTTP/1.1 101 Switching Protocols\r\n" +
                        "Upgrade: websocket\r\n" +
                        "Connection: Upgrade\r\n" +
                        $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), cancellationToken);
        await stream.FlushAsync(cancellationToken);
        Interlocked.Increment(ref _activeWebSockets);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var payload = await ReadFrameAsync(stream, cancellationToken);
                if (payload == null) return;
                await WriteTextFrameAsync(stream, Encoding.UTF8.GetString(payload), cancellationToken);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeWebSockets);
        }
    }

    private static async Task<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var opcode = header[0] & 0x0F;
        var masked = (header[1] & 0x80) != 0;
        long length = header[1] & 0x7F;
        if (length == 126)
        {
            var extended = new byte[2];
            await stream.ReadExactlyAsync(extended, cancellationToken);
            length = (extended[0] << 8) | extended[1];
        }
        else if (length == 127)
        {
            var extended = new byte[8];
            await stream.ReadExactlyAsync(extended, cancellationToken);
            length = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(extended);
        }
        if (length > 1_000_000) return null;
        var mask = new byte[4];
        if (masked) await stream.ReadExactlyAsync(mask, cancellationToken);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        if (masked)
        {
            for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
        }
        if (opcode == 0x8)
        {
            await WriteFrameAsync(stream, 0x8, Array.Empty<byte>(), cancellationToken);
            return null;
        }
        return opcode == 0x1 ? payload : null;
    }

    private static Task WriteTextFrameAsync(NetworkStream stream, string text, CancellationToken cancellationToken) =>
        WriteFrameAsync(stream, 0x1, Encoding.UTF8.GetBytes(text), cancellationToken);

    private static async Task WriteFrameAsync(
        NetworkStream stream,
        int opcode,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        output.WriteByte((byte)(0x80 | opcode));
        if (payload.Length < 126)
        {
            output.WriteByte((byte)payload.Length);
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            output.WriteByte(126);
            output.WriteByte((byte)(payload.Length >> 8));
            output.WriteByte((byte)payload.Length);
        }
        else
        {
            output.WriteByte(127);
            output.Write(new byte[4]);
            output.WriteByte((byte)(payload.Length >> 24));
            output.WriteByte((byte)(payload.Length >> 16));
            output.WriteByte((byte)(payload.Length >> 8));
            output.WriteByte((byte)payload.Length);
        }
        output.Write(payload);
        await stream.WriteAsync(output.ToArray(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch
        {
        }
        _cts.Dispose();
    }
}
