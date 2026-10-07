using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AicIv.Server;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var token = timeout.Token;
await using var app = ServerApp.Create("http://127.0.0.1:0");
await app.StartAsync(token);
try
{
    var uri = new Uri(app.Urls.Single().Replace("http://", "ws://"));
    using var http = new HttpClient();
    var health = await http.GetAsync(app.Urls.Single() + "/health", token);
    Check(health.IsSuccessStatusCode, "health check HTTP");
    var healthBody = JsonNode.Parse(await health.Content.ReadAsStringAsync(token));
    Check(healthBody?["status"]?.GetValue<string>() == "ok", "status do health check");
    using var a = new ClientWebSocket();
    using var b = new ClientWebSocket();
    await a.ConnectAsync(uri, token);
    var welcomeA = await Receive(a);
    Check(welcomeA["type"]!.GetValue<string>() == "welcome", "welcome A");
    await b.ConnectAsync(uri, token);
    var welcomeB = await Receive(b);
    Check(welcomeB["type"]!.GetValue<string>() == "welcome", "welcome B");
    Check(!JsonNode.DeepEquals(welcomeA["client_id"], welcomeB["client_id"]), "IDs únicos");
    Check((await Request(a, "{\"type\":\"ping\"}"))["type"]!.GetValue<string>() == "pong", "ping");
    Check(JsonNode.DeepEquals(await Request(a, "{\"type\":\"echo\",\"data\":{\"x\":12}}"),
        JsonNode.Parse("{\"type\":\"echo\",\"data\":{\"x\":12}}")), "echo");

    var receiveA = Receive(a);
    var receiveB = Receive(b);
    await Send(a, "{\"type\":\"broadcast\",\"data\":{\"text\":\"Olá\"}}");
    foreach (var response in await Task.WhenAll(receiveA, receiveB))
    {
        Check(response["type"]!.GetValue<string>() == "broadcast", "broadcast");
        Check(JsonNode.DeepEquals(response["from"], welcomeA["client_id"]), "remetente");
        Check(response["data"]!["text"]!.GetValue<string>() == "Olá", "payload");
    }
    await Task.WhenAll(
        Send(a, "{\"type\":\"broadcast\",\"data\":1}"),
        Send(b, "{\"type\":\"broadcast\",\"data\":2}"));
    foreach (var socket in new[] { a, b })
    {
        var first = await Receive(socket);
        var second = await Receive(socket);
        Check(first["type"]!.GetValue<string>() == "broadcast"
            && second["type"]!.GetValue<string>() == "broadcast", "broadcasts simultâneos");
        var values = new[] { first["data"]!.GetValue<int>(), second["data"]!.GetValue<int>() };
        Check(values.Order().SequenceEqual(new[] { 1, 2 }), "entrega dos dois broadcasts");
    }
    foreach (var invalid in new[] { "{", "null", "[]", "{\"type\":42}", "{\"type\":\"unknown\"}" })
        Check((await Request(a, invalid))["type"]!.GetValue<string>() == "error", "JSON inválido");
    await a.SendAsync(Encoding.UTF8.GetBytes("binary").AsMemory(), WebSocketMessageType.Binary, true, token);
    Check((await Receive(a))["type"]!.GetValue<string>() == "error", "binário");

    await a.SendAsync(Encoding.UTF8.GetBytes("{\"type\":").AsMemory(), WebSocketMessageType.Text, false, token);
    await a.SendAsync(Encoding.UTF8.GetBytes("\"ping\"}").AsMemory(), WebSocketMessageType.Text, true, token);
    Check((await Receive(a))["type"]!.GetValue<string>() == "pong", "mensagem fragmentada");

    using var large = new ClientWebSocket();
    await large.ConnectAsync(uri, token);
    await Receive(large);
    await Send(large, new string('x', 65537));
    var close = await large.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), token);
    Check(close.CloseStatus == WebSocketCloseStatus.MessageTooBig, "limite de mensagem");
    await a.CloseAsync(WebSocketCloseStatus.NormalClosure, "teste", token);
    await b.CloseAsync(WebSocketCloseStatus.NormalClosure, "teste", token);
    Console.WriteLine("OK: conexão, ping, echo, broadcasts simultâneos, validação, fragmentação, limite e desconexão.");
}
finally { await app.StopAsync(CancellationToken.None); }

async Task Send(ClientWebSocket socket, string message) =>
    await socket.SendAsync(Encoding.UTF8.GetBytes(message).AsMemory(), WebSocketMessageType.Text, true, token);

async Task<JsonNode> Request(ClientWebSocket socket, string message)
{
    await Send(socket, message);
    return await Receive(socket);
}

async Task<JsonNode> Receive(ClientWebSocket socket)
{
    using var stream = new MemoryStream();
    var buffer = new byte[4096];
    WebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
        Check(result.MessageType == WebSocketMessageType.Text, "resposta em texto");
        stream.Write(buffer, 0, result.Count);
    } while (!result.EndOfMessage);
    return JsonNode.Parse(stream.ToArray())!;
}

void Check(bool condition, string name)
{
    if (!condition) throw new Exception($"Falhou: {name}");
}
