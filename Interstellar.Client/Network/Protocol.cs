// Protocol v2 — lightweight binary framing for Interstellar Voice Chat.
// Replaces the old SIPSorcery/WebRTC-based protocol with a simple relay model.
//
// WebSocket binary frame format:
//   [2 bytes: payload_length big-endian] [payload: one or more tagged messages]
//
// Tagged message format:
//   [1 byte: message_type] [type-specific payload]
//
// Audio frame (message_type = 0x09):
//   [1 byte: type=0x09] [1 byte: src_id] [4 bytes: seq big-endian]
//   [2 bytes: duration_rtp big-endian] [N bytes: opus_data]
namespace Interstellar.Network.Protocol;

internal static class MessageType
{
    public const byte JoinRequest  = 0x01;
    public const byte JoinResponse = 0x02;
    public const byte Leave        = 0x03;
    public const byte Profile      = 0x04;
    public const byte ProfileShare = 0x05;
    public const byte MuteStatus   = 0x06;
    public const byte MuteShare    = 0x07;
    public const byte HostSettings = 0x08;
    public const byte AudioData    = 0x09;
    public const byte ServerInfo   = 0x0A;
    public const byte CustomData   = 0x0B;
    public const byte Ping         = 0x0C;
    public const byte Pong         = 0x0D;
}

/// <summary>JoinRequest sent from client to server.</summary>
internal readonly struct JoinRequestMsg
{
    public readonly string RoomCode;
    public readonly string Region;

    public JoinRequestMsg(string roomCode, string region)
    { RoomCode = roomCode; Region = region; }

    public byte[] Encode()
    {
        var roomBytes = System.Text.Encoding.UTF8.GetBytes(RoomCode);
        var regionBytes = System.Text.Encoding.UTF8.GetBytes(Region);
        var buf = new byte[1 + 2 + roomBytes.Length + 2 + regionBytes.Length];
        int p = 0;
        buf[p++] = MessageType.JoinRequest;
        ProtoHelpers.WriteU16(buf, ref p, (ushort)roomBytes.Length);
        System.Buffer.BlockCopy(roomBytes, 0, buf, p, roomBytes.Length); p += roomBytes.Length;
        ProtoHelpers.WriteU16(buf, ref p, (ushort)regionBytes.Length);
        System.Buffer.BlockCopy(regionBytes, 0, buf, p, regionBytes.Length);
        return buf;
    }
}

/// <summary>JoinResponse received from server.</summary>
internal readonly struct JoinResponseMsg
{
    public readonly byte YourClientId;
    public readonly ClientInfoMsg[] Clients;
    public readonly byte[]? HostSettings;

    public JoinResponseMsg(byte yourId, ClientInfoMsg[] clients, byte[]? hostSettings)
    { YourClientId = yourId; Clients = clients; HostSettings = hostSettings; }

    public static JoinResponseMsg Decode(byte[] data, int offset, int length)
    {
        int p = offset + 1; // skip type
        byte yourId = data[p++];
        int count = ProtoHelpers.ReadU16(data, ref p);
        var clients = new ClientInfoMsg[count];
        for (int i = 0; i < count; i++)
        {
            byte cid = data[p++];
            int nameLen = ProtoHelpers.ReadU16(data, ref p);
            string name = System.Text.Encoding.UTF8.GetString(data, p, nameLen); p += nameLen;
            byte pid = data[p++];
            bool muted = data[p++] != 0;
            clients[i] = new ClientInfoMsg(cid, name, pid, muted);
        }
        int hsLen = ProtoHelpers.ReadU16(data, ref p);
        byte[]? hs = null;
        if (hsLen > 0)
        {
            hs = new byte[hsLen];
            System.Buffer.BlockCopy(data, p, hs, 0, hsLen);
            p += hsLen;
        }
        return new JoinResponseMsg(yourId, clients, hs);
    }
}

internal readonly struct ClientInfoMsg
{
    public readonly byte ClientId;
    public readonly string PlayerName;
    public readonly byte PlayerId;
    public readonly bool IsMuted;

    public ClientInfoMsg(byte cid, string name, byte pid, bool muted)
    { ClientId = cid; PlayerName = name; PlayerId = pid; IsMuted = muted; }
}

/// <summary>ServerInfo received from server.</summary>
internal readonly struct ServerInfoMsg
{
    public readonly int OptimalPlayers;
    public readonly int TotalClients;
    public readonly string ServerUrl;

    public ServerInfoMsg(int optimal, int total, string url)
    { OptimalPlayers = optimal; TotalClients = total; ServerUrl = url; }

    public static ServerInfoMsg Decode(byte[] data, int offset, int length)
    {
        int p = offset + 1;
        int optimal = ProtoHelpers.ReadU16(data, ref p);
        int total = ProtoHelpers.ReadU16(data, ref p);
        int urlLen = ProtoHelpers.ReadU16(data, ref p);
        string url = System.Text.Encoding.UTF8.GetString(data, p, urlLen);
        return new ServerInfoMsg(optimal, total, url);
    }
}

/// <summary>ProfileShare received from server (another client's profile).</summary>
internal readonly struct ProfileShareMsg
{
    public readonly byte ClientId;
    public readonly string PlayerName;
    public readonly byte PlayerId;

    public ProfileShareMsg(byte cid, string name, byte pid)
    { ClientId = cid; PlayerName = name; PlayerId = pid; }

    public static ProfileShareMsg Decode(byte[] data, int offset, int length)
    {
        int p = offset + 1; // skip type
        byte cid = data[p++];
        int nameLen = ProtoHelpers.ReadU16(data, ref p);
        string name = System.Text.Encoding.UTF8.GetString(data, p, nameLen); p += nameLen;
        byte pid = data[p++];
        return new ProfileShareMsg(cid, name, pid);
    }
}

/// <summary>MuteShare received from server.</summary>
internal readonly struct MuteShareMsg
{
    public readonly byte ClientId;
    public readonly bool IsMuted;
    public readonly bool IsImpostorRadio;

    public MuteShareMsg(byte cid, bool muted, bool radio)
    { ClientId = cid; IsMuted = muted; IsImpostorRadio = radio; }

    public static MuteShareMsg Decode(byte[] data, int offset, int length)
    {
        byte cid = data[offset + 1];
        byte flags = data[offset + 2];
        return new MuteShareMsg(cid, (flags & 1) != 0, (flags & 2) != 0);
    }
}

/// <summary>Audio frame (send and receive).</summary>
internal class AudioFrameMsg
{
    public byte SourceId;
    public uint SequenceNumber;
    public ushort DurationRtp;
    public byte[] OpusData = System.Array.Empty<byte>();

    /// <summary>Encode this frame into a tagged message byte array.</summary>
    public byte[] Encode()
    {
        var buf = new byte[1 + 1 + 4 + 2 + OpusData.Length];
        int p = 0;
        buf[p++] = MessageType.AudioData;
        buf[p++] = SourceId;
        ProtoHelpers.WriteU32(buf, ref p, SequenceNumber);
        ProtoHelpers.WriteU16(buf, ref p, DurationRtp);
        System.Buffer.BlockCopy(OpusData, 0, buf, p, OpusData.Length);
        return buf;
    }

    /// <summary>Decode an audio frame from raw data. Returns null if data is invalid or too small (DTX noise).</summary>
    public static AudioFrameMsg? Decode(byte[] data, int offset, int length)
    {
        // Minimum: type(1) + src(1) + seq(4) + dur(2) = 8 bytes
        int avail = length - (offset - 0);
        if (avail < 8) return null;

        int p = offset + 1; // skip type
        byte src = data[p++];
        uint seq = ProtoHelpers.ReadU32(data, ref p);
        ushort dur = ProtoHelpers.ReadU16(data, ref p);
        int opusLen = avail - (p - offset);
        if (opusLen <= 2) return null; // skip DTX / near-silence

        var opus = new byte[opusLen];
        System.Buffer.BlockCopy(data, p, opus, 0, opusLen);
        return new AudioFrameMsg { SourceId = src, SequenceNumber = seq, DurationRtp = dur, OpusData = opus };
    }
}

/// <summary>Leave notification from server.</summary>
internal readonly struct LeaveMsg
{
    public readonly byte ClientId;
    public LeaveMsg(byte cid) => ClientId = cid;
    public static LeaveMsg Decode(byte[] data, int offset, int length)
    { return new LeaveMsg(data[offset + 1]); }
}

// ── Helpers ────────────────────────────────────────────────────────

internal static class ProtoHelpers
{
    public static byte[] EncodeProfile(string playerName, byte playerId)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(playerName);
        var buf = new byte[1 + 2 + nameBytes.Length + 1];
        int p = 0;
        buf[p++] = MessageType.Profile;
        ProtoHelpers.WriteU16(buf, ref p, (ushort)nameBytes.Length);
        System.Buffer.BlockCopy(nameBytes, 0, buf, p, nameBytes.Length); p += nameBytes.Length;
        buf[p++] = playerId;
        return buf;
    }

    public static byte[] EncodeMuteStatus(bool muted, bool impostorRadio)
    {
        byte flags = 0;
        if (muted) flags |= 1;
        if (impostorRadio) flags |= 2;
        return new byte[] { MessageType.MuteStatus, flags };
    }

    public static byte[] EncodeHostSettings(byte[] raw)
    {
        var buf = new byte[1 + raw.Length];
        buf[0] = MessageType.HostSettings;
        System.Buffer.BlockCopy(raw, 0, buf, 1, raw.Length);
        return buf;
    }

    public static byte[] EncodeCustomData(byte[] raw)
    {
        var buf = new byte[1 + raw.Length];
        buf[0] = MessageType.CustomData;
        System.Buffer.BlockCopy(raw, 0, buf, 1, raw.Length);
        return buf;
    }

    public static byte[] EncodePing() => new byte[] { MessageType.Ping };

    // ── Length-prefixed frame ────────────────────────────────────

    /// <summary>Wraps a tagged message in a 2-byte big-endian length-prefixed frame.</summary>
    public static byte[] WrapFrame(byte[] tagged)
    {
        var buf = new byte[2 + tagged.Length];
        ProtoHelpers.WriteU16(buf, 0, (ushort)tagged.Length);
        System.Buffer.BlockCopy(tagged, 0, buf, 2, tagged.Length);
        return buf;
    }

    /// <summary>Wraps multiple tagged messages in a single frame.</summary>
    public static byte[] WrapFrame(byte[][] tagged)
    {
        int total = 0;
        foreach (var t in tagged) total += t.Length;
        var buf = new byte[2 + total];
        int p = 2;
        ProtoHelpers.WriteU16(buf, 0, (ushort)total);
        foreach (var t in tagged)
        {
            System.Buffer.BlockCopy(t, 0, buf, p, t.Length);
            p += t.Length;
        }
        return buf;
    }

    /// <summary>Dispatches each tagged message in a length-prefixed frame.</summary>
    public static void DispatchFrame(byte[] data, System.Action<byte, byte[], int, int> handler)
    {
        if (data.Length < 2) return;
        int payloadLen = (data[0] << 8) | data[1];
        if (2 + payloadLen > data.Length) return;

        int pos = 2;
        int end = 2 + payloadLen;
        while (pos < end)
        {
            if (pos >= end) break;
            byte msgType = data[pos];
            int start = pos;
            pos++;

            switch (msgType)
            {
                case MessageType.JoinRequest:
                    pos += 2; if (pos <= end) pos += ReadU16At(data, pos - 2);
                    pos += 2; if (pos <= end) pos += ReadU16At(data, pos - 2);
                    break;
                case MessageType.JoinResponse:
                    pos++; // your_id
                    if (pos + 2 <= end) { int cnt = ReadU16At(data, pos); pos += 2; for (int i = 0; i < cnt && pos < end; i++) { pos++; if (pos + 2 <= end) { int nl = ReadU16At(data, pos); pos += 2 + nl + 2; } } }
                    if (pos + 2 <= end) { int hsl = ReadU16At(data, pos); pos += 2 + hsl; }
                    break;
                case MessageType.Profile:
                    if (pos + 2 <= end) { int nl = ReadU16At(data, pos); pos += 2 + nl + 1; }
                    break;
                case MessageType.ProfileShare:
                    pos++; if (pos + 2 <= end) { int nl = ReadU16At(data, pos); pos += 2 + nl + 1; }
                    break;
                case MessageType.MuteStatus:
                    pos += 1;
                    break;
                case MessageType.MuteShare:
                    pos += 2;
                    break;
                case MessageType.HostSettings:
                case MessageType.CustomData:
                    pos = end; // rest of payload
                    break;
                case MessageType.AudioData:
                    // src(1) + seq(4) + dur(2) + opus
                    if (pos + 6 <= end) { pos += 6; pos = end; } // opus goes to end
                    else pos = end;
                    break;
                case MessageType.ServerInfo:
                    if (pos + 4 <= end) pos += 4; // optimal(2) + total(2)
                    if (pos + 2 <= end) { int ul = ReadU16At(data, pos); pos += 2 + ul; }
                    break;
                case MessageType.Leave:
                    pos += 1;
                    break;
                case MessageType.Ping:
                case MessageType.Pong:
                    break;
                default:
                    pos = end; // skip unknown
                    break;
            }

            if (pos > end) pos = end;
            handler(msgType, data, start, pos - start);
        }
    }

    // ── Primitive read/write ────────────────────────────────────

    internal static void WriteU16(byte[] buf, int pos, ushort v) { buf[pos] = (byte)(v >> 8); buf[pos + 1] = (byte)v; }
    internal static void WriteU16(byte[] buf, ref int pos, ushort v) { buf[pos++] = (byte)(v >> 8); buf[pos++] = (byte)v; }
    internal static void WriteU32(byte[] buf, ref int pos, uint v) { buf[pos++] = (byte)(v >> 24); buf[pos++] = (byte)(v >> 16); buf[pos++] = (byte)(v >> 8); buf[pos++] = (byte)v; }

    internal static ushort ReadU16(byte[] data, ref int pos) { ushort v = (ushort)((data[pos] << 8) | data[pos + 1]); pos += 2; return v; }
    internal static uint ReadU32(byte[] data, ref int pos) { uint v = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]); pos += 4; return v; }
    internal static ushort ReadU16At(byte[] data, int pos) => (ushort)((data[pos] << 8) | data[pos + 1]);
}
