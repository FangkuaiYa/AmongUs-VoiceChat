using Concentus;
using Interstellar.Network.Protocol;
using NAudio.Wave;
using VoiceChatPlugin;

namespace Interstellar.Network;

internal interface IConnectionContext
{
    void OnAudioFrameReceived(int clientId, float[] samples, int length);
    void OnClientConnected(int clientId);
    void OnClientDisconnected(int clientId);
    void OnClientProfileUpdated(int clientId, string playerName, byte playerId);
    void OnReceiveMuteStatus(int clientId, bool isMute, bool isImpostorRadio);
    void OnCustomMessageReceived(byte[] message);
    void OnHostSettingsReceived(byte[] rawSettings);
    void OnServerInfoReceived(int optimalPlayers, int totalClients, string serverUrl);
}

/// <summary>
/// Connects to the Go-based Interstellar Voice Server via raw binary WebSocket.
/// No WebRTC/SDP/ICE — pure Opus relay.
/// </summary>
internal class RoomConnection
{
    private readonly string roomCode;
    private readonly string region;
    private readonly WebSocketSharp.WebSocket socket;
    private readonly IConnectionContext context;

    private int? myClientId;
    public int MyClientId => myClientId ?? -1;

    private string? pendingName;
    private byte pendingPid;

    private readonly IOpusEncoder encoder;
    private readonly byte[] encodedBuffer = new byte[2048];
    private uint audioSeq;

    private readonly Dictionary<int, IOpusDecoder> decoders = new(64);
    private readonly HashSet<int> decodeErrors = new();
    private bool firstAudioFrame = true;
    private readonly HashSet<int> newAudioSources = new();

    public RoomConnection(IConnectionContext context, string roomCode, string region, string url)
    {
        this.context = context;
        this.roomCode = roomCode;
        this.region = region;

        encoder = CreateOpusEncoder();

        socket = new WebSocketSharp.WebSocket(url)
        {
            Compression = WebSocketSharp.CompressionMethod.Deflate
        };
        if (url.StartsWith("wss:", System.StringComparison.OrdinalIgnoreCase))
            socket.SslConfiguration.EnabledSslProtocols =
                System.Security.Authentication.SslProtocols.Tls11 |
                System.Security.Authentication.SslProtocols.Tls12;

        socket.OnOpen += OnSocketOpen;
        socket.OnMessage += OnSocketMessage;
        socket.OnError += (_, e) =>
            InterstellarPlugin.Logger.LogError($"[VC] WebSocket error: {e.Message}");
        socket.OnClose += (_, e) =>
            InterstellarPlugin.Logger.LogWarning($"[VC] WebSocket closed: code={e.Code} reason={e.Reason}");

        socket.Connect();
    }

    private void OnSocketOpen(object? sender, System.EventArgs e)
    {
        InterstellarPlugin.Logger.LogInfo($"[VC] Connected (room={roomCode} region={region}).");
        var joinReq = new JoinRequestMsg(roomCode, region);
        SendRaw(ProtoHelpers.WrapFrame(joinReq.Encode()));
        FlushProfile();
    }

    private void OnSocketMessage(object? sender, WebSocketSharp.MessageEventArgs e)
    {
        if (!e.IsBinary || e.RawData == null) return;

        var rawData = e.RawData;

        // Decrypt if encryption is enabled
        if (CryptoHelper.IsEnabled)
        {
            var decrypted = CryptoHelper.DecryptFrame(rawData);
            if (decrypted == null) return;
            rawData = decrypted;
        }

        ProtoHelpers.DispatchFrame(rawData, (msgType, data, offset, length) =>
        {
            switch (msgType)
            {
                case MessageType.JoinResponse: HandleJoinResponse(data, offset, length); break;
                case MessageType.ProfileShare: HandleProfileShare(data, offset, length); break;
                case MessageType.MuteShare:    HandleMuteShare(data, offset, length); break;
                case MessageType.AudioData:    HandleAudioData(data, offset, length); break;
                case MessageType.Leave:        HandleLeave(data, offset, length); break;
                case MessageType.HostSettings: HandleHostSettings(data, offset, length); break;
                case MessageType.ServerInfo:   HandleServerInfo(data, offset, length); break;
                case MessageType.CustomData:   HandleCustomData(data, offset, length); break;
                case MessageType.Ping:         SendRaw(ProtoHelpers.WrapFrame(ProtoHelpers.EncodePing())); break;
            }
        });
    }

    private void HandleJoinResponse(byte[] data, int offset, int length)
    {
        var resp = JoinResponseMsg.Decode(data, offset, length);
        myClientId = resp.YourClientId;
        InterstellarPlugin.Logger.LogInfo($"[VC] Assigned client ID: {resp.YourClientId}");
        foreach (var c in resp.Clients)
        {
            if (c.ClientId != resp.YourClientId)
            {
                context.OnClientConnected(c.ClientId);
                context.OnClientProfileUpdated(c.ClientId, c.PlayerName, c.PlayerId);
                context.OnReceiveMuteStatus(c.ClientId, c.IsMuted, false);
            }
        }
        if (resp.HostSettings != null && resp.HostSettings.Length > 0)
            context.OnHostSettingsReceived(resp.HostSettings);
    }

    private void HandleProfileShare(byte[] data, int offset, int length)
    {
        var msg = ProfileShareMsg.Decode(data, offset, length);
        context.OnClientConnected(msg.ClientId);
        context.OnClientProfileUpdated(msg.ClientId, msg.PlayerName, msg.PlayerId);
    }

    private void HandleMuteShare(byte[] data, int offset, int length)
    {
        var msg = MuteShareMsg.Decode(data, offset, length);
        context.OnReceiveMuteStatus(msg.ClientId, msg.IsMuted, msg.IsImpostorRadio);
    }

    private void HandleAudioData(byte[] data, int offset, int length)
    {
        var frame = AudioFrameMsg.Decode(data, offset, length);
        if (frame == null) return;

        if (firstAudioFrame)
        {
            firstAudioFrame = false;
            InterstellarPlugin.Logger.LogInfo($"[VC:AudioRx] First audio frame (client={frame.SourceId}, bytes={frame.OpusData.Length}).");
        }
        if (newAudioSources.Add(frame.SourceId))
            InterstellarPlugin.Logger.LogInfo($"[VC:AudioRx] New audio source: client {frame.SourceId}.");

        try
        {
            if (!decoders.TryGetValue(frame.SourceId, out var decoder))
            {
                decoder = CreateOpusDecoder();
                decoders[frame.SourceId] = decoder;
            }
            float[] buf = new float[2048];
            int samples = decoder.Decode(frame.OpusData, buf, buf.Length);
            context.OnAudioFrameReceived(frame.SourceId, buf, samples);
        }
        catch (System.Exception ex)
        {
            if (decodeErrors.Add(frame.SourceId))
                InterstellarPlugin.Logger.LogWarning($"[VC] Opus decode error (client {frame.SourceId}): {ex.Message}");
        }
    }

    private void HandleLeave(byte[] data, int offset, int length)
    {
        var msg = LeaveMsg.Decode(data, offset, length);
        context.OnClientDisconnected(msg.ClientId);
        decoders.Remove(msg.ClientId);
        newAudioSources.Remove(msg.ClientId);
    }

    private void HandleHostSettings(byte[] data, int offset, int length)
    {
        int rawLen = length - 1;
        if (rawLen <= 0) return;
        var raw = new byte[rawLen];
        System.Buffer.BlockCopy(data, offset + 1, raw, 0, rawLen);
        context.OnHostSettingsReceived(raw);
    }

    private void HandleServerInfo(byte[] data, int offset, int length)
    {
        var msg = ServerInfoMsg.Decode(data, offset, length);
        context.OnServerInfoReceived(msg.OptimalPlayers, msg.TotalClients, msg.ServerUrl);
    }

    private void HandleCustomData(byte[] data, int offset, int length)
    {
        int rawLen = length - 1;
        if (rawLen <= 0) return;
        var raw = new byte[rawLen];
        System.Buffer.BlockCopy(data, offset + 1, raw, 0, rawLen);
        context.OnCustomMessageReceived(raw);
    }

    // ── Outgoing ──────────────────────────────────────────────

    public void UpdateProfile(string playerName, byte playerId)
    {
        pendingName = playerName;
        pendingPid = playerId;
        if (socket.ReadyState == WebSocketSharp.WebSocketState.Open)
            FlushProfile();
    }

    private void FlushProfile()
    {
        if (pendingName == null) return;
        SendRaw(ProtoHelpers.WrapFrame(ProtoHelpers.EncodeProfile(pendingName, pendingPid)));
        pendingName = null;
    }

    public void UpdateMuteStatus(bool mute, bool isImpostorRadio = false)
    {
        SendRaw(ProtoHelpers.WrapFrame(ProtoHelpers.EncodeMuteStatus(mute, isImpostorRadio)));
    }

    public void SendAudio(float[] sampleBuffer, int sampleLength, double bufferMilliseconds)
    {
        int encodedLen = encoder.Encode(sampleBuffer, sampleLength, encodedBuffer, encodedBuffer.Length);
        if (encodedLen <= 2) return;

        var opus = new byte[encodedLen];
        System.Buffer.BlockCopy(encodedBuffer, 0, opus, 0, encodedLen);

        var frame = new AudioFrameMsg
        {
            SourceId = (byte)(myClientId ?? 0),
            SequenceNumber = audioSeq++,
            DurationRtp = (ushort)(bufferMilliseconds * 48),
            OpusData = opus
        };

        SendRaw(ProtoHelpers.WrapFrame(frame.Encode()));
    }

    public void SendCustomMessage(byte[] message)
        => SendRaw(ProtoHelpers.WrapFrame(ProtoHelpers.EncodeCustomData(message)));

    public void SendHostSettings(byte[] rawSettings)
        => SendRaw(ProtoHelpers.WrapFrame(ProtoHelpers.EncodeHostSettings(rawSettings)));

    public void Disconnect() => socket.Close();

    private void SendRaw(byte[] data)
    {
        if (socket.ReadyState != WebSocketSharp.WebSocketState.Open)
            return;

        // Encrypt if enabled
        if (CryptoHelper.IsEnabled)
            data = CryptoHelper.EncryptFrame(data);

        socket.Send(data);
    }

    // ── Opus codec ────────────────────────────────────────────

    private static IOpusEncoder CreateOpusEncoder()
    {
        var enc = OpusCodecFactory.CreateEncoder(48000, 1,
            Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
        enc.Bitrate = 64000;
        enc.UseVBR = true;
        enc.UseDTX = false;
        enc.UseInbandFEC = true;
        enc.SignalType = Concentus.Enums.OpusSignal.OPUS_SIGNAL_VOICE;
        return enc;
    }

    private static IOpusDecoder CreateOpusDecoder()
        => OpusCodecFactory.CreateDecoder(48000, 1);
}
