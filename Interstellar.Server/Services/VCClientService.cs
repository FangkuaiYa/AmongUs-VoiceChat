using Interstellar.Messages;
using Interstellar.Messages.Variation;
using Interstellar.Server.VoiceChat;
using SIPSorcery.Net;
using System.Collections.Concurrent;
using System.Collections.Generic;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace Interstellar.Server.Services;

internal class VCClientService : WebSocketBehavior, IMessageProcessor
{
    internal static string? GlobalTurnUrl;
    internal static string? GlobalTurnUser;
    internal static string? GlobalTurnPass;

    private RTCPeerConnection connection;
    private Dictionary<int, MediaStreamTrack> streamTracks = new(32);
    private Dictionary<int, AudioStream> audioStreams = new(32);
    private VCClient? client = null;
    private bool IsJoined => client != null;
    private readonly ConcurrentQueue<IceCandMessage> pendingIceCandidates = new();

    // SDP deduplication: skip renegotiation if the mask hasn't changed
    private long lastSentMask = -1;

    public VCClientService()
    {
        connection = new(WebSocketHelpers.GetRTCConfiguration(GlobalTurnUrl, GlobalTurnUser, GlobalTurnPass));
        connection.OnAudioFrameReceived += frame =>
        {
            var durationRtpUnits = RtpTimestampExtensions.ToRtpUnits(frame.DurationMilliSeconds, AudioHelpers.ClockRate);
            client?.BroadcastAudio(durationRtpUnits, frame.EncodedAudio);
        };

        connection.onicecandidate += (candidate) =>
        {
            // ICE candidates are very noisy (dozens per connection).
            // Console.WriteLine("Client " + this.ID + " ICE candidate generated.");
            var msg = new IceCandMessage(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex, candidate.usernameFragment);
            // Check if the WebSocket is open yet
            if (this.Context?.WebSocket?.ReadyState == WebSocketState.Open)
            {
                SendMessage(msg);
            }
            else
            {
                pendingIceCandidates.Enqueue(msg);
            }
        };
    }

    protected override void OnOpen()
    {
        Console.WriteLine("Client " + this.ID + " connected.");
        // Send ICE candidates that were queued before the WebSocket opened
        while (pendingIceCandidates.TryDequeue(out var msg))
        {
            SendMessage(msg);
        }
    }

    protected override void OnMessage(MessageEventArgs e)
    {
        if (e.IsBinary)
        {
            try
            {
                MessagePacker.UnpackMessages(e.RawData, this);
            }catch(InvalidDataException ex)
            {
                Console.WriteLine("Error processing message from client " + this.ID + ": " + ex.Message);
            }
        }

    }

    protected override void OnClose(CloseEventArgs e)
    {
        Console.WriteLine("Client " + this.ID + " disconnected.");
        client?.Close();
        pendingIceCandidates.Clear();
    }

    int IMessageProcessor.Process(MessageTag tag, ReadOnlySpan<byte> bytes)
    {
        int read = -1;
        switch (tag)
        {
            case MessageTag.Join:
                // Console.WriteLine("Client " + this.ID + " requested to join a room.");
                JoinRoom(JoinMessage.DeserializeWithoutTag(bytes, out read));
                break;
            case MessageTag.SdpAnswer:
                // Console.WriteLine("Client " + this.ID + " sent SDP answer.");
                AcceptSdpAnswer(SdpAnswerMessage.DeserializeWithoutTag(bytes, out read));
                break;
            case MessageTag.AddIceCand:
                // Console.WriteLine("Client " + this.ID + " sent ICE candidate.");
                AddIceCandidate(IceCandMessage.DeserializeWithoutTag(bytes, out read));
                break;
            case MessageTag.Profile:
                if (client == null)
                {
                    Console.WriteLine("Client " + this.ID + " sent profile without joining room.");
                    break;
                }
                var profile = ProfileMessage.DeserializeWithoutTag(bytes, out read);
                // Console.WriteLine("Client " + this.ID + " sent profile (name: " + profile.PlayerName + "id: " + profile.PlayerId + ").");
                client.UpdateProfile(profile.PlayerName, profile.PlayerId);
                break;
            case MessageTag.Custom:
                CustomMessage.DeserializeForServerWithoutTag(bytes, out read);
                client?.BroadcastRawMessage(bytes);
                break;
            case MessageTag.RequestReload:
                read = 0;
                ResendConnectionInformation();
                break;
            case MessageTag.UpdateMuteStatus:
                var muteStatus = UpdateMuteStatusMessage.DeserializeWithoutTag(bytes, out read);
                client?.UpdateMuteStatus(muteStatus.Mute, muteStatus.IsImpostorRadio);
                break;
            case MessageTag.HostSettings:
                var hostSettings = HostSettingsMessage.DeserializeWithoutTag(bytes, out read);
                client?.BroadcastHostSettings(hostSettings);
                break;
            case MessageTag.ServerInfo:
                read = 0; // Server sends this, never receives — ignore
                break;
        }
        return read;
    }

    private void JoinRoom(JoinMessage message)
    {
        if (!IsJoined)
        {
            Console.WriteLine("Client " + this.ID + " joined room " + message.RoomCode + " in region " + message.Region);
            VCRoom room = RoomManager.GetRoom(message.Region, message.RoomCode);
            client = room.Join(this);

            // Add the receive track
            var format = AudioHelpers.GetOpusFormat(client.ClientId);
            var stream = new MediaStreamTrack(format, MediaStreamStatusEnum.RecvOnly);
            connection.addTrack(stream);

            long initMask = room.CurrentVoiceMask;
            lastSentMask = initMask;
            var joinMessages = new List<IMessage> { new ShareIdMessage(client.ClientId), UpdateTracks(initMask) };
            joinMessages.AddRange(client.ShareExistingProfiles());
            // Replay the last host settings so new clients start with correct room config
            if (room.LastHostSettings != null)
                joinMessages.Add(room.LastHostSettings);
            SendMessages(joinMessages);

            SendServerInfo();
            BroadcastServerInfo(room);
        }
    }

    private void AcceptSdpAnswer(SdpAnswerMessage message)
    {
        connection.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = message.Sdp });
    }

    private void AddIceCandidate(IceCandMessage message)
    {
        connection.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = message.Candidate,
            sdpMid = message.SdpMid,
            sdpMLineIndex = (ushort)message.SdpMLineIndex,
            usernameFragment = message.UsernameFragment
        });
    }

    private void ResendConnectionInformation()
    {
        if(client == null) return;
        long mask = client.Room.CurrentVoiceMask;
        lastSentMask = mask;
        var reloadMessages = new List<IMessage> { UpdateTracks(mask) };
        reloadMessages.AddRange(client.ShareExistingProfiles());
        // Replay the last host settings so reconnecting clients stay in sync
        if (client.Room.LastHostSettings != null)
            reloadMessages.Add(client.Room.LastHostSettings);
        SendMessages(reloadMessages);
    }

    private SdpOfferMessage UpdateTracks(long mask)
    {
        int myId = client!.ClientId;
        for(int i = 0; i < AudioHelpers.MaxTracks; i++)
        {
            if(i == myId) continue;
            bool shouldHave = (mask & (1L << i)) != 0;
            bool have = streamTracks.ContainsKey(i);
            if (shouldHave && !have)
            {
                var format = AudioHelpers.GetOpusFormat(i);
                var stream = new MediaStreamTrack(format, MediaStreamStatusEnum.SendOnly);
                streamTracks.Add(i, stream);
                connection.addTrack(stream);
            }
        }

        // Update AudioStreams
        audioStreams.Clear();
        foreach (var audioStream in connection.AudioStreamList)
        {
            audioStreams[audioStream.GetSendingFormat().ID] = audioStream;
        }
        

        var offer = connection.createOffer(null);
        connection.setLocalDescription(offer).Wait();
        return new SdpOfferMessage(offer.sdp, mask);
    }

    /// <summary>Sends current track info to the client. Skips if mask unchanged.</summary>
    public void SendTracksMask(long mask)
    {
        if (!IsJoined) return;
        if (mask == lastSentMask) return; // No change — skip renegotiation
        lastSentMask = mask;
        SendMessage(UpdateTracks(mask));
    }

    public void SendClientLeft(int clientId)
    {
        if (IsJoined)
        {
            SendMessage(new NoticeDisconnectMessage(clientId));
        }
    }

    /// <summary>Sends an audio frame to the client.</summary>
    public void SendAudio(int id, uint durationRtpUnits, byte[] encodedAudio)
    {
        if (audioStreams.TryGetValue(id, out var stream))
        {
            stream.SendAudio(durationRtpUnits, encodedAudio);
        }
        else
        {
            if(!lastError.HasValue || System.DateTime.Now.Subtract(lastError.Value).TotalMilliseconds > 500)
            {
                lastError = System.DateTime.Now;
                long mask = client!.Room.CurrentVoiceMask;
                // Always resend on error — the stream is missing regardless of mask change
                lastSentMask = mask;
                SendMessage(UpdateTracks(mask));
                return;
            }
        }
    }
    System.DateTime? lastError = null;

    public void SendMessage(IMessage message) => this.Send(MessagePacker.PackMessage(message).ToArray());

    public void SendMessages(IEnumerable<IMessage> messages) => this.Send(MessagePacker.PackMessages(messages).ToArray());

    public void SendRawMessage(byte[] message) => this.Send(message.ToArray());

    /// <summary>Sends ServerInfo to this specific client.</summary>
    public void SendServerInfo()
    {
        var vcUrl = Server.ServerUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/vc";
        var msg = new ServerInfoMessage(
            Server.OptimalPlayerCount,
            RoomManager.TotalClientCount,
            vcUrl);
        SendMessage(msg);
    }

    /// <summary>Broadcasts updated ServerInfo to all clients in the room.</summary>
    public static void BroadcastServerInfo(VCRoom room)
    {
        var vcUrl = Server.ServerUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/vc";
        var msg = new ServerInfoMessage(
            Server.OptimalPlayerCount,
            RoomManager.TotalClientCount,
            vcUrl);
        foreach (var c in room.Clients)
            c.Send(msg);
    }
}
