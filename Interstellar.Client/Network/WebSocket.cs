using System.Net.WebSockets;
using System.Text;

namespace Interstellar.Network;

public enum WebSocketState { None, Connecting, Open, CloseSent, CloseReceived, Closed }
public enum WebSocketMessageType { Text, Binary, Close }

/// <summary>
/// Thin wrapper around the .NET WebSocket client to keep the socket.io transport reliable.
/// </summary>
internal sealed class WebSocket : IDisposable
{
    private readonly ClientWebSocket _inner;
    private readonly string _url;
    private readonly string _origin;
    private readonly CancellationToken _token;

    public WebSocketState State { get; private set; } = WebSocketState.None;

    public WebSocket(string url, string origin, CancellationToken token)
    {
        _url = url;
        _origin = origin;
        _token = token;
        _inner = new ClientWebSocket();
        _inner.Options.SetRequestHeader("Origin", _origin);
        // Hard-coded to the official BetterCrewLink client version so the
        // voice server does not reject this connection as an unknown client.
        _inner.Options.SetRequestHeader("User-Agent", "BetterCrewLink/3.1.4 (win32)");
    }

    public async Task ConnectAsync()
    {
        State = WebSocketState.Connecting;
        var uri = new Uri(_url);
        await _inner.ConnectAsync(uri, _token);
        State = WebSocketState.Open;
    }

    public async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync()
    {
        if (State != WebSocketState.Open)
            return (WebSocketMessageType.Close, Array.Empty<byte>());

        var buffer = new byte[8192];
        var segment = new ArraySegment<byte>(buffer);
        WebSocketReceiveResult result;
        using var ms = new MemoryStream();

        do
        {
            result = await _inner.ReceiveAsync(segment, _token);
            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
            {
                State = WebSocketState.Closed;
                return (WebSocketMessageType.Close, Array.Empty<byte>());
            }

            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        var payload = ms.ToArray();
        return result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text
            ? (WebSocketMessageType.Text, payload)
            : (WebSocketMessageType.Binary, payload);
    }

    public async Task SendAsync(byte[] data, WebSocketMessageType type)
    {
        if (State != WebSocketState.Open) return;
        var messageType = type == WebSocketMessageType.Text ? System.Net.WebSockets.WebSocketMessageType.Text : System.Net.WebSockets.WebSocketMessageType.Binary;
        await _inner.SendAsync(new ArraySegment<byte>(data), messageType, true, _token);
    }

    public async Task CloseAsync()
    {
        if (State != WebSocketState.Open) return;
        State = WebSocketState.CloseSent;
        try { await _inner.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, _token); }
        catch { }
        State = WebSocketState.Closed;
    }

    public void Dispose()
    {
        State = WebSocketState.Closed;
        try { _inner.Dispose(); } catch { }
    }
}
