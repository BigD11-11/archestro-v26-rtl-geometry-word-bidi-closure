using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Archestro.MeetingVault.Services;

public sealed class ObsWebSocketClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();

    public async Task ConnectAsync(int port, string password, CancellationToken ct)
    {
        _socket.Options.AddSubProtocol("obswebsocket.json");
        await _socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), ct);

        var hello = JsonDocument.Parse(await ReceiveTextAsync(ct)).RootElement;
        if (hello.GetProperty("op").GetInt32() != 0)
            throw new InvalidOperationException("OBS WebSocket returned an invalid hello message.");

        var d = hello.GetProperty("d");
        var identifyData = new Dictionary<string, object>
        {
            ["rpcVersion"] = 1,
            ["eventSubscriptions"] = 0
        };

        if (d.TryGetProperty("authentication", out var auth))
        {
            if (string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException("OBS WebSocket requires authentication.");

            identifyData["authentication"] = CreateAuth(
                password,
                auth.GetProperty("salt").GetString()!,
                auth.GetProperty("challenge").GetString()!);
        }

        await SendJsonAsync(new { op = 1, d = identifyData }, ct);
        var identified = JsonDocument.Parse(await ReceiveTextAsync(ct)).RootElement;
        if (identified.GetProperty("op").GetInt32() != 2)
            throw new InvalidOperationException("OBS WebSocket authentication failed.");
    }

    public async Task<JsonElement> RequestAsync(string requestType, object? requestData, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var d = requestData is null
            ? new Dictionary<string, object> { ["requestType"] = requestType, ["requestId"] = requestId }
            : new Dictionary<string, object> { ["requestType"] = requestType, ["requestId"] = requestId, ["requestData"] = requestData };

        await SendJsonAsync(new { op = 6, d }, ct);

        while (true)
        {
            using var doc = JsonDocument.Parse(await ReceiveTextAsync(ct));
            var root = doc.RootElement;
            if (root.GetProperty("op").GetInt32() != 7) continue;

            var response = root.GetProperty("d");
            if (!string.Equals(response.GetProperty("requestId").GetString(), requestId, StringComparison.Ordinal))
                continue;

            var status = response.GetProperty("requestStatus");
            if (!status.GetProperty("result").GetBoolean())
            {
                var comment = status.TryGetProperty("comment", out var c) ? c.GetString() : "OBS request failed.";
                throw new InvalidOperationException($"{requestType}: {comment}");
            }

            if (response.TryGetProperty("responseData", out var data))
                return data.Clone();
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    private async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        var segment = new ArraySegment<byte>(bytes);
        await _socket.SendAsync(segment, WebSocketMessageType.Text, true, ct);
    }

    private async Task<string> ReceiveTextAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        var segment = new ArraySegment<byte>(buffer);
        do
        {
            result = await _socket.ReceiveAsync(segment, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("OBS WebSocket closed the connection.");
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string CreateAuth(string password, string salt, string challenge)
    {
        var secretHash = SHA256.HashData(Encoding.UTF8.GetBytes(password + salt));
        var secret = Convert.ToBase64String(secretHash);
        var authHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge));
        return Convert.ToBase64String(authHash);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "EMV disconnect", CancellationToken.None);
        }
        catch { }
        _socket.Dispose();
    }
}
