using System.Net;
using System.Text;
using System.Text.Json;

namespace BeanBot.Commands;

/// <summary>
/// Loopback-only HTTP control server (default http://127.0.0.1:8777/) per P1.10 / Section 9.
/// Exposes GET /status and POST /command { "command": "..." }.
/// </summary>
public sealed class HttpControlServer : IAsyncDisposable
{
    private readonly CommandSurface _surface;
    private readonly Func<object> _statusProvider;
    private readonly HttpListener _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public Uri BaseUri { get; }

    public HttpControlServer(CommandSurface surface, BotClient bot, int port = 8777)
        : this(surface, () => bot.GetStatus(), port)
    {
    }

    public HttpControlServer(CommandSurface surface, IFleetControlTarget fleet, int port = 8777)
        : this(surface, fleet.GetFleetStatus, port)
    {
    }

    private HttpControlServer(CommandSurface surface, Func<object> statusProvider, int port)
    {
        _surface = surface;
        _statusProvider = statusProvider;
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUri.ToString());
    }

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(ctx, ct), CancellationToken.None);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "/";
            if (string.IsNullOrEmpty(path)) path = "/";

            if (ctx.Request.HttpMethod == "GET" && path is "/status" or "/")
            {
                await WriteJsonAsync(ctx.Response, 200, _statusProvider()).ConfigureAwait(false);
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/command")
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                string command;
                if (body.TrimStart().StartsWith('{'))
                {
                    using var doc = JsonDocument.Parse(body);
                    command = doc.RootElement.TryGetProperty("command", out var cmdProp)
                        ? cmdProp.GetString() ?? string.Empty
                        : string.Empty;
                }
                else
                {
                    command = body;
                }

                var result = await _surface.ExecuteAsync(command, ct).ConfigureAwait(false);
                await WriteJsonAsync(ctx.Response, result.Ok ? 200 : 400, result).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(ctx.Response, 404, new { error = "NotFound" }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                await WriteJsonAsync(ctx.Response, 500, new { error = ex.Message }).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions { WriteIndented = true });
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        try
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }

            _listener.Close();
        }
        catch
        {
        }

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }
    }
}
