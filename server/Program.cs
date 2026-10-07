using AicIv.Server;

var host = Environment.GetEnvironmentVariable("HOST") ?? "127.0.0.1";
var portText = Environment.GetEnvironmentVariable("PORT") ?? "8080";
if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
{
    Console.Error.WriteLine("PORT deve ser um número entre 1 e 65535.");
    return 1;
}

await using var app = ServerApp.Create($"http://{host}:{port}", args);
Console.WriteLine($"Servidor WebSocket: ws://{host}:{port}");
await app.RunAsync();
return 0;
