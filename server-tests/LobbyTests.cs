using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AicIv.Server;

internal static class LobbyTests
{
    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        await using var app = ServerApp.Create("http://127.0.0.1:0");
        await app.StartAsync(token);
        var sockets = new List<ClientWebSocket>();
        try
        {
            var uri = new Uri(app.Urls.Single().Replace("http://", "ws://"));
            async Task<ClientWebSocket> Connect()
            {
                var socket = new ClientWebSocket();
                sockets.Add(socket);
                await socket.ConnectAsync(uri, token);
                var hello = await Read(socket, "welcome");
                Check(hello["lobby_version"]!.GetValue<int>() == 1, "versão do lobby");
                return socket;
            }
            async Task Send(ClientWebSocket socket, object data) => await socket.SendAsync(
                Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(data)).AsMemory(),
                WebSocketMessageType.Text, true, token);
            async Task<JsonNode> Read(ClientWebSocket socket, string expected)
            {
                while (true)
                {
                    using var stream = new MemoryStream();
                    var buffer = new byte[4096];
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer.AsMemory(), token);
                        Check(result.MessageType == WebSocketMessageType.Text, "frame de texto");
                        stream.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    var message = JsonNode.Parse(stream.ToArray())!;
                    var type = message["type"]!.GetValue<string>();
                    if (type == expected) return message;
                    Check(type != "error", "erro inesperado: " + message);
                }
            }
            var a = await Connect();
            var b = await Connect();
            var c = await Connect();
            var d = await Connect();
            await Send(a, new { type = "room_create", name = "Yan" });
            var first = (await Read(a, "room_state"))["room"]!;
            var code = first["code"]!.GetValue<string>();
            Check(first["players"]!.AsArray().Count == 1, "criação de sala");
            Check(first["capacity"]!.GetValue<int>() == 2, "capacidade de dois jogadores");
            await Send(b, new { type = "room_join", code = "ZZZZZZ", name = "B" });
            await Read(b, "error");
            await Send(b, new { type = "room_join", code = code.ToLowerInvariant(), name = "B" });
            Check((await Read(a, "room_state"))["room"]!["players"]!.AsArray().Count == 2, "entrada notifica anfitrião");
            var joined = (await Read(b, "room_state"))["room"]!;
            await Send(d, new { type = "room_join", code, name = "Terceiro" });
            Check((await Read(d, "error"))["message"]!.GetValue<string>() == "A sala está cheia.", "terceiro jogador rejeitado");
            var bId = joined["players"]![1]!["id"]!.GetValue<string>();
            await Send(c, new { type = "room_create", name = "Outra equipe" });
            var otherCode = (await Read(c, "room_state"))["room"]!["code"]!.GetValue<string>();
            Check(otherCode != code, "códigos únicos");
            await Send(a, new { type = "lobby_list" });
            Check((await Read(a, "rooms_list"))["rooms"]!.AsArray().Count == 2, "listagem de salas");
            await Send(b, new { type = "game_start" });
            await Read(b, "error");
            await Send(a, new { type = "game_start" });
            await Read(a, "error");
            await Send(a, new { type = "room_ready", ready = true });
            await Read(a, "room_state"); await Read(b, "room_state");
            await Send(b, new { type = "room_ready", ready = true });
            await Read(a, "room_state"); await Read(b, "room_state");
            await Send(a, new { type = "game_start" });
            Check((await Read(a, "game_started"))["room"]!["started"]!.GetValue<bool>(), "início pelo anfitrião");
            await Read(b, "game_started");
            await Send(d, new { type = "room_join", code, name = "D" });
            await Read(d, "error");
            await Send(a, new { type = "lobby_list" });
            Check((await Read(a, "rooms_list"))["rooms"]!.AsArray().Count == 1, "partidas iniciadas não aparecem");
            await Send(a, new { type = "room_leave" });
            await Read(a, "room_left");
            Check((await Read(b, "room_state"))["room"]!["host_id"]!.GetValue<string>() == bId, "transferência de anfitrião");
            await Send(b, new { type = "room_leave" }); await Read(b, "room_left");
            // A segunda sala não recebeu notificações da primeira.
            await Send(c, new { type = "room_ready", ready = true });
            var isolated = (await Read(c, "room_state"))["room"]!;
            Check(isolated["players"]!.AsArray().Count == 1 && isolated["players"]![0]!["ready"]!.GetValue<bool>(), "isolamento de salas");
            await Send(a, new { type = "room_join", code = otherCode, name = "Yan" });
            await Read(a, "room_state"); await Read(c, "room_state");
            await c.CloseAsync(WebSocketCloseStatus.NormalClosure, "teste", token);
            var promoted = (await Read(a, "room_state"))["room"]!;
            Check(promoted["players"]!.AsArray().Count == 1, "limpeza ao desconectar");
            Check(promoted["host_id"]!.GetValue<string>() == promoted["players"]![0]!["id"]!.GetValue<string>(), "promoção após desconexão");
            foreach (var peer in new[] { b })
            {
                await Send(peer, new { type = "room_join", code = otherCode, name = "Peer" });
                await Read(peer, "room_state");
            }
            var e = await Connect();
            await Send(e, new { type = "room_join", code = otherCode, name = "E" }); await Read(e, "error");
            var f = await Connect();
            await Send(f, new { type = "room_join", code = otherCode, name = "F" }); await Read(f, "error");
            await Send(f, new { type = "room_create", name = " " }); await Read(f, "error");
            await Send(f, new { type = "room_create", name = new string('x', 25) }); await Read(f, "error");
            Console.WriteLine("OK LOBBY: salas, entrada, capacidade, nomes, isolamento, pronto, permissão, início e desconexão.");
        }
        finally
        {
            foreach (var socket in sockets) { socket.Abort(); socket.Dispose(); }
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("Falhou no lobby: " + name);
    }
}
