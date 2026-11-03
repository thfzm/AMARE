using System.Net;
using System.Net.Sockets;
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
                string? line;
                do
                {
                    line = await reader.ReadLineAsync(_cts.Token);
                } while (!string.IsNullOrEmpty(line));

                var path = requestLine?.Split(' ').ElementAtOrDefault(1) ?? "/";
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
            "/slow.html" => """
                <!doctype html><html><head><title>Slow</title></head><body><h1>Slow</h1></body></html>
                """,
            "/download.txt" => "AMARE download probe",
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
        return ("200 OK", contentType, page, headers);
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
