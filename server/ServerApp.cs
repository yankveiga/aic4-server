using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace AicIv.Server;

public static class ServerApp
{
    private const int MaxMessageBytes = 64 * 1024;

    public static WebApplication Create(string url, string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        builder.WebHost.UseUrls(url);
        var app = builder.Build();
        var clients = new ConcurrentDictionary<string, ClientConnection>();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.Map("/", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Conecte usando WebSocket.");
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, app.Lifetime.ApplicationStopping);
            var token = lifetime.Token;
            var client = new ClientConnection(socket);
            try
            {
                // O welcome deve chegar antes de qualquer broadcast.
                await client.SendAsync(new { type = "welcome", client_id = client.Id }, token);
                clients[client.Id] = client;
                var buffer = new byte[4096];
                while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await client.CloseAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, "", token);
                            return;
                        }
                        if (message.Length + result.Count > MaxMessageBytes)
                        {
                            await client.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Limite de 64 KiB", token);
                            return;
                        }
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        await client.SendAsync(new { type = "error", message = "Envie JSON como texto." }, token);
                        continue;
                    }

                    JsonDocument document;
                    try { document = JsonDocument.Parse(message.ToArray()); }
                    catch (JsonException)
                    {
                        await client.SendAsync(new { type = "error", message = "JSON inválido." }, token);
                        continue;
                    }
                    using (document)
                    {
                        var root = document.RootElement;
                        if (root.ValueKind != JsonValueKind.Object)
                        {
                            await client.SendAsync(new { type = "error", message = "A mensagem deve ser um objeto JSON." }, token);
                            continue;
                        }
                        var type = root.TryGetProperty("type", out var property) && property.ValueKind == JsonValueKind.String
                            ? property.GetString() : null;
                        object? data = root.TryGetProperty("data", out var payload) ? payload : null;
                        switch (type)
                        {
                            case "ping":
                                await client.SendAsync(new { type = "pong", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, token);
                                break;
                            case "echo":
                                await client.SendAsync(new { type = "echo", data }, token);
                                break;
                            case "broadcast":
                                var outgoing = new { type = "broadcast", from = client.Id, data };
                                await Task.WhenAll(clients.Values.Select(peer => peer.SendAsync(outgoing, token)));
                                break;
                            default:
                                await client.SendAsync(new { type = "error", message = "Tipo desconhecido. Use ping, echo ou broadcast." }, token);
                                break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException error)
            {
                app.Logger.LogDebug(error, "Cliente {ClientId} desconectou", client.Id);
            }
            finally
            {
                clients.TryRemove(client.Id, out _);
                socket.Abort();
            }
        });
        return app;
    }

    private sealed class ClientConnection(WebSocket socket)
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        // WebSocket permite apenas um envio simultâneo por conexão.
        private readonly SemaphoreSlim sendLock = new(1, 1);

        public async Task SendAsync(object message, CancellationToken token)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var entered = false;
            try
            {
                await sendLock.WaitAsync(timeout.Token);
                entered = true;
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token);
            }
            catch (OperationCanceledException) { socket.Abort(); }
            catch (WebSocketException) { socket.Abort(); }
            catch (ObjectDisposedException) { }
            finally { if (entered) sendLock.Release(); }
        }

        public async Task CloseAsync(WebSocketCloseStatus status, string reason, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await sendLock.WaitAsync(timeout.Token);
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(status, reason, timeout.Token);
            }
            finally { sendLock.Release(); }
        }
    }
}
