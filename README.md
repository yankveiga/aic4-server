# AIC4 — Servidor WebSocket em C#

Servidor C# com ASP.NET Core e .NET 10 para clientes Godot usando
WebSocketPeer e mensagens JSON. O código do jogo fica em outro repositório.

## Executar localmente

Instale o SDK .NET 10 e execute na raiz:

```powershell
dotnet run --project server
```

No Windows, também pode usar `powershell -ExecutionPolicy Bypass -File server/run.ps1`.
O servidor escuta em `ws://127.0.0.1:8080`. Configure `HOST` e `PORT` para
alterar o endereço. `GET /health` retorna `{"status":"ok"}`.

## Publicar no Render

1. No painel do Render, escolha **New > Blueprint**.
2. Conecte o repositório **yankveiga/aic4-server**.
3. Selecione a branch **main** e o arquivo **render.yaml** na raiz.
4. Confira o serviço Docker no plano Free e clique em **Deploy Blueprint**.
5. Após ficar Live, abra `https://SEU-SERVICO.onrender.com/health`.
6. No cliente Godot, use `wss://SEU-SERVICO.onrender.com`, sem `/health`
   e sem a porta interna.

O container usa `HOST=0.0.0.0` e respeita a variável `PORT` do Render.
O Render termina o TLS; o servidor dentro do container usa HTTP.
O plano Free suspende após 15 minutos sem tráfego de entrada e pode levar
cerca de um minuto para voltar. Abra `/health` e espere responder antes de
conectar o jogo. O cliente deve reconectar após interrupções.

Referências: [Blueprints](https://render.com/docs/infrastructure-as-code),
[WebSockets](https://render.com/docs/websocket) e
[plano Free](https://render.com/docs/free).

## Protocolo

Use objetos JSON em frames de texto, com limite de 64 KiB.

| Envio | Resposta |
| --- | --- |
| `{"type":"ping"}` | `{"type":"pong","timestamp":...}` |
| `{"type":"echo","data":{"x":10}}` | `{"type":"echo","data":{"x":10}}` |
| `{"type":"broadcast","data":{"x":10,"y":20}}` | `{"type":"broadcast","from":"id","data":...}` para todos, incluindo o remetente |

Ao conectar, o servidor envia `{"type":"welcome","client_id":"..."}`.
Mensagens inválidas recebem `{"type":"error","message":"..."}`.
Este exemplo não implementa autenticação nem o protocolo RPC do Godot.
As conexões ficam em memória; use uma única instância para compartilhar
broadcasts entre todos os clientes.

## Testes

```powershell
dotnet run --project server-tests
```

Valida health check, conexão, ping, echo, broadcasts simultâneos, mensagens
inválidas, fragmentação, limite de tamanho e desconexão.

## Docker

```powershell
docker build -f server/Dockerfile -t aic4-server .
docker run --rm -p 8080:10000 aic4-server
```
