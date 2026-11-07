using System.Net.WebSockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quartermaster.Net;
using Quartermaster.Relay;

RelayApp.Build(args).Run();

namespace Quartermaster.Relay
{
    /// <summary>The relay: one WebSocket endpoint <c>/qm</c> over TLS on one port.</summary>
    public static class RelayApp
    {
        public const string EndpointPath = "/qm";

        /// <param name="configure">Test hook, run first (e.g. UseTestServer, a fake clock, log capture, settings).</param>
        public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
        {
            var builder = WebApplication.CreateBuilder(args);
            configure?.Invoke(builder);

            var options = builder.Configuration.GetSection("Relay").Get<RelayOptions>() ?? new RelayOptions();
            builder.Services.AddSingleton(options);
            builder.Services.TryAddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<Rooms>();

            var cert = Certificates.LoadOrCreate(options);
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Limits.MaxConcurrentConnections = options.MaxConnections;
                k.Limits.MaxConcurrentUpgradedConnections = options.MaxConnections;
                k.ListenAnyIP(options.Port, l => l.UseHttps(cert.Certificate));
            });

            var app = builder.Build();
            var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Quartermaster.Relay");
            if (cert.Created) log.LogInformation("Created a self-signed certificate in {DataDir}.", options.DataDir);
            log.LogInformation("Relay certificate fingerprint: {Fingerprint}", cert.Fingerprint);
            log.LogInformation("Relay listening on port {Port} (path {Path}).", options.Port, EndpointPath);

            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(15),
                KeepAliveTimeout = TimeSpan.FromSeconds(30),
            });
            app.Map(EndpointPath, (HttpContext ctx, Rooms rooms, TimeProvider time) => Serve(ctx, rooms, time, log, options));
            return app;
        }

        private static bool IsNetworkError(Exception e) =>
            e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException or ConnectionResetException;

        private static async Task Serve(HttpContext ctx, Rooms rooms, TimeProvider time, ILogger log, RelayOptions options)
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("Quartermaster relay: connect with the game.");
                return;
            }
            var bucket = Connection.Bucket(ctx.Connection.RemoteIpAddress);
            if (!rooms.TryAdmit(bucket))
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }
            try { await ServeAdmitted(ctx, rooms, time, log, bucket, options); }
            finally { rooms.Release(bucket); }
        }

        private static async Task ServeAdmitted(HttpContext ctx, Rooms rooms, TimeProvider time, ILogger log, string bucket, RelayOptions options)
        {
            var conn = new Connection(bucket, time)
            {
                HostMessagesPerSecond = Math.Max(Limits.MessagesPerSecond, options.HostMessagesPerSecond),
                HostBurst = Math.Max(Limits.Burst, options.HostBurst),
            };
            // Started before the handshake completes, so a client that never says Hello is always timed.
            using var helloTimer = time.CreateTimer(_ =>
            {
                if (!conn.Greeted) conn.Close(WebSocketCloseStatus.PolicyViolation, "No Hello received.");
            }, null, Limits.HelloTimeout, Timeout.InfiniteTimeSpan);
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            using var abort = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, conn.Aborted);
            var writer = WriteLoop(ws, conn, abort, log);
            try { await ReadLoop(ws, conn, rooms, abort.Token); }
            catch (Exception e) when (IsNetworkError(e)) { }
            catch (Exception e)
            {
                // Never the message text: it could echo client data.
                log.LogWarning("A connection failed ({Error}).", e.GetType().Name);
            }
            finally
            {
                rooms.Disconnect(conn);
                if (!conn.Closing) conn.Close(WebSocketCloseStatus.NormalClosure, "");
            }
            await writer;
        }

        private static async Task ReadLoop(WebSocket ws, Connection conn, Rooms rooms, CancellationToken ct)
        {
            const int Initial = 16 * 1024;
            var buf = new byte[Initial];
            int len = 0;
            while (!conn.Closing)
            {
                if (len == buf.Length) Array.Resize(ref buf, Math.Min(buf.Length * 2, Framing.MaxFrame + 1));
                var r = await ws.ReceiveAsync(buf.AsMemory(len), ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                len += r.Count;
                if (!conn.Greeted && len > Limits.PreHelloMaxFrame)
                {
                    conn.Close(WebSocketCloseStatus.MessageTooBig, "Say Hello first.");
                    return;
                }
                if (len > Framing.MaxFrame)
                {
                    conn.Close(WebSocketCloseStatus.MessageTooBig, "Message is too large.");
                    return;
                }
                if (!r.EndOfMessage) continue;
                if (!conn.TakeToken())
                {
                    conn.Close(WebSocketCloseStatus.PolicyViolation, "Too many messages.");
                    return;
                }
                rooms.Handle(conn, buf.AsSpan(0, len));
                len = 0;
                if (buf.Length > 1024 * 1024) buf = new byte[Initial];
            }
        }

        private static async Task WriteLoop(WebSocket ws, Connection conn, CancellationTokenSource abort, ILogger log)
        {
            try
            {
                await foreach (var frame in conn.Outgoing.ReadAllAsync(abort.Token))
                {
                    conn.Sent(frame);
                    await ws.SendAsync(frame, WebSocketMessageType.Text, true, abort.Token);
                }
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    var (status, reason) = conn.CloseRequest ?? (WebSocketCloseStatus.NormalClosure, "");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await ws.CloseOutputAsync(status, reason, timeout.Token);
                }
            }
            catch (Exception e) when (IsNetworkError(e)) { }
            catch (Exception e) { log.LogWarning("A connection failed while sending ({Error}).", e.GetType().Name); }
            finally
            {
                // The relay closed this client: give it a moment to answer, then stop reading.
                abort.CancelAfter(TimeSpan.FromSeconds(5));
            }
        }
    }
}
