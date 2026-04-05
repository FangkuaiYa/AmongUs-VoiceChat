using VoiceChatPlugin.Concentus;
using Hazel;
using HarmonyLib;
using VoiceChatPlugin.Audio;
using VoiceChatPlugin.NAudio.Wave;
using UnityEngine;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System;
using System.Linq;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Manages the in-game voice chat session.
///
/// Audio transport runs entirely over the existing Among Us / Impostor game
/// server via PlayerControl RPC ID 203.  No separate voice server is needed.
///
/// Outgoing pipeline:
///   WaveInEvent  (PCM 48 kHz mono, 16-bit, on WaveIn callback thread)
///   -> Opus encode
///   -> enqueue into _sendQueue  (thread-safe)
///   -> dequeued on Unity main thread and sent via Hazel RPC
///
/// Incoming pipeline:
///   RPC 203 arrives (on Hazel network thread)
///   -> enqueue into _receiveQueue  (thread-safe)
///   -> dequeued on Unity main thread in Update()
///   -> Opus decode (per-client decoder, own float[] buffer)
///   -> AudioRoutingInstance.AddSamples()
///   -> audio routing graph (volume, stereo, ghost reverb, radio)
///   -> WasapiOut (WASAPI shared mode, own playback thread)
///
/// Threading model
/// ───────────────
///  • Unity main thread  : Update(), profile broadcast, RPC send
///  • WaveIn thread      : OnMicDataAvailable()  – enqueues only
///  • Hazel recv thread  : AudioRpcPatch.Postfix() – enqueues only
///  • WasapiOut thread   : reads audio graph (AudioManager/ISampleProvider chain)
///
/// All mutable shared state (_clients, _decoders, _audioManager) is accessed
/// exclusively on the Unity main thread.  Cross-thread communication uses
/// ConcurrentQueue so no locks are needed in hot paths.
/// </summary>
public class VoiceChatRoom
{
    internal const byte AudioRpcId = 203;

    // ── Singleton ──────────────────────────────────────────────────────────────
    public static VoiceChatRoom? Current { get; private set; }

    // ── Audio routing graph (accessed only from Unity main thread + WasapiOut) ─
    private readonly AudioManager          _audioManager;
    private readonly VolumeRouter.Property _masterVolumeProperty;

    private readonly StereoRouter     _imager;
    private readonly VolumeRouter     _normalVolume, _ghostVolume, _radioVolume, _clientVolume;
    private readonly LevelMeterRouter _levelMeter;

    // ── Remote clients – Unity main thread only ────────────────────────────────
    private readonly Dictionary<int, VCPlayer>     _clients  = new();
    // Per-client decoders and per-client decode buffers to avoid sharing
    private readonly Dictionary<int, IOpusDecoder> _decoders = new();
    private readonly Dictionary<int, float[]>      _decodeBufs = new();

    public IEnumerable<VCPlayer> AllClients => _clients.Values;

    // ── Virtual components (camera / vent) ─────────────────────────────────────
    private readonly List<IVoiceComponent> _virtualMics     = new();
    private readonly List<IVoiceComponent> _virtualSpeakers = new();
    public void AddVirtualMicrophone(IVoiceComponent c)    => _virtualMics.Add(c);
    public void AddVirtualSpeaker(IVoiceComponent c)       => _virtualSpeakers.Add(c);
    public void RemoveVirtualMicrophone(IVoiceComponent c) => _virtualMics.Remove(c);
    public void RemoveVirtualSpeaker(IVoiceComponent c)    => _virtualSpeakers.Remove(c);

    // ── Microphone (WaveIn thread writes; main thread reads _sendQueue) ────────
    private WaveInEvent?  _waveIn;
    private IOpusEncoder? _encoder;
    private float[]       _pcmConvertBuf = null!;
    private readonly byte[] _encodeBuffer = new byte[4096];
    private float _micVolume = 1f;

    // Encoded packets queued from WaveIn thread, drained on main thread
    private readonly ConcurrentQueue<byte[]> _sendQueue = new();

    public bool  UsingMicrophone => _waveIn != null;
    public float LocalMicLevel   => _localMicLevel;
    private volatile float _localMicLevel;
    public bool  Mute  { get; private set; }
    public int   SampleRate => AudioHelpers.ClockRate;

    // ── Speaker ────────────────────────────────────────────────────────────────
    private WasapiOut? _waveOut;

    // ── Incoming packet queue: Hazel thread -> main thread ─────────────────────
    private readonly record struct IncomingPacket(int SenderId, byte PacketType, byte[] Data, byte PlayerId, string PlayerName);
    private readonly ConcurrentQueue<IncomingPacket> _receiveQueue = new();

    // ── Comms sabotage cache ───────────────────────────────────────────────────
    private bool  _commsSabActive;
    private float _commsSabCheckTimer;

    // ── Local profile tracking ─────────────────────────────────────────────────
    private byte   _lastId   = byte.MaxValue;
    private string _lastName = null!;

    // ══════════════════════════════════════════════════════════════════════════
    // Factory
    // ══════════════════════════════════════════════════════════════════════════

    public static VoiceChatRoom Start()
    {
        Current?.Close();
        Current = new VoiceChatRoom();
        return Current;
    }

    public static void CloseCurrentRoom()
    {
        Current?.Close();
        Current = null;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Constructor
    // ══════════════════════════════════════════════════════════════════════════

    private VoiceChatRoom()
    {
        SimpleRouter   source   = new();
        SimpleEndpoint endpoint = new();

        _imager       = new StereoRouter();
        _normalVolume = new VolumeRouter();
        _ghostVolume  = new VolumeRouter();
        _radioVolume  = new VolumeRouter();
        _clientVolume = new VolumeRouter();
        _levelMeter   = new LevelMeterRouter();

        FilterRouter     ghostLowpass  = FilterRouter.CreateLowPassFilter(1900f, 2f);
        ReverbRouter     ghostReverb1  = new(53,  0.7f, 0.2f) { IsGlobalRouter = true };
        ReverbRouter     ghostReverb2  = new(173, 0.4f, 0.6f) { IsGlobalRouter = true };
        FilterRouter     radioHighpass = FilterRouter.CreateHighPassFilter(650f, 3.2f);
        FilterRouter     radioLowpass  = FilterRouter.CreateLowPassFilter(800f, 2.1f);
        DistortionFilter radioDistort  = new() { IsGlobalRouter = true, DefaultThreshold = 0.55f };
        VolumeRouter     masterRouter  = new() { IsGlobalRouter = true };

        source.Connect(_clientVolume);
        _clientVolume.Connect(_imager);
        _imager.Connect(_normalVolume);
        _normalVolume.Connect(_levelMeter);
        _levelMeter.Connect(masterRouter);
        _imager.Connect(ghostLowpass);
        ghostLowpass.Connect(_ghostVolume);
        _ghostVolume.Connect(ghostReverb1);
        ghostReverb1.Connect(ghostReverb2);
        ghostReverb2.Connect(masterRouter);
        _clientVolume.Connect(radioHighpass);
        radioHighpass.Connect(radioLowpass);
        radioLowpass.Connect(_radioVolume);
        _radioVolume.Connect(radioDistort);
        radioDistort.Connect(masterRouter);
        masterRouter.Connect(endpoint);

        _audioManager = new AudioManager(source, 2048, 4096);
        _masterVolumeProperty = masterRouter.GetProperty(_audioManager);
        SetMasterVolume(VoiceChatConfig.MasterVolume);

        SetMicrophone(VoiceChatConfig.MicrophoneDevice);
        SetSpeaker(VoiceChatConfig.SpeakerDevice);

        VoiceChatPluginMain.Logger.LogInfo("[VC] VoiceChatRoom constructed (Hazel transport).");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Device control  (called from Unity main thread)
    // ══════════════════════════════════════════════════════════════════════════

    public void SetMasterVolume(float v) => _masterVolumeProperty.Volume = v;

    public void SetMicVolume(float v) => _micVolume = Math.Clamp(v, 0f, 2f);

    public void SetMute(bool mute) => Mute = mute;
    public void ToggleMute()       => SetMute(!Mute);
    public void SetLoopBack(bool lb) { }

    public void SetMicrophone(string deviceName)
    {
        try
        {
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            _waveIn = null;
            _encoder?.Dispose();
            _encoder = null;

            _encoder = AudioHelpers.GetOpusEncoder();

            int deviceNum = 0;
            int total = WaveInEvent.DeviceCount;
            for (int i = 0; i < total; i++)
            {
                if (WaveInEvent.GetCapabilities(i).ProductName == deviceName)
                { deviceNum = i; break; }
            }

            _waveIn = new WaveInEvent
            {
                DeviceNumber       = deviceNum,
                WaveFormat         = new WaveFormat(AudioHelpers.ClockRate, 16, 1),
                BufferMilliseconds = 20,
                NumberOfBuffers    = 4,
            };
            _waveIn.DataAvailable += OnMicDataAvailable;
            _waveIn.StartRecording();

            VoiceChatPluginMain.Logger.LogInfo(
                $"[VC] Mic: '{(string.IsNullOrEmpty(deviceName) ? "default" : deviceName)}'");
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Mic init failed: {ex.Message}");
            _waveIn  = null;
            _encoder = null;
        }
    }

    public void SetSpeaker(string deviceName)
    {
        try
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;

            var ep = _audioManager.Endpoint;
            if (ep == null)
            {
                VoiceChatPluginMain.Logger.LogError("[VC] Audio graph has no endpoint.");
                return;
            }

            var enumerator = new VoiceChatPlugin.NAudio.CoreAudioApi.MMDeviceEnumerator();
            VoiceChatPlugin.NAudio.CoreAudioApi.MMDevice? device = null;
            if (!string.IsNullOrEmpty(deviceName))
            {
                foreach (var d in enumerator.EnumerateAudioEndPoints(
                             VoiceChatPlugin.NAudio.CoreAudioApi.DataFlow.Render,
                             VoiceChatPlugin.NAudio.CoreAudioApi.DeviceState.Active))
                {
                    if (d.FriendlyName == deviceName) { device = d; break; }
                }
            }
            device ??= enumerator.GetDefaultAudioEndpoint(
                VoiceChatPlugin.NAudio.CoreAudioApi.DataFlow.Render,
                VoiceChatPlugin.NAudio.CoreAudioApi.Role.Multimedia);

            _waveOut = new WasapiOut(device, VoiceChatPlugin.NAudio.CoreAudioApi.AudioClientShareMode.Shared, false, 50);
            _waveOut.Init(ep);
            _waveOut.Play();

            VoiceChatPluginMain.Logger.LogInfo(
                $"[VC] Speaker: '{(string.IsNullOrEmpty(deviceName) ? "default" : deviceName)}'");
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Speaker init failed: {ex.Message}");
            _waveOut = null;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Outgoing: WaveIn thread → encode → enqueue (never touches AU client)
    // ══════════════════════════════════════════════════════════════════════════

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        // This runs on the WaveIn callback thread.
        // MUST NOT call any Unity / IL2CPP / AmongUsClient methods here.
        if (Mute || _encoder == null) return;

        int samples = e.BytesRecorded / 2;
        if (_pcmConvertBuf == null || _pcmConvertBuf.Length != samples)
            _pcmConvertBuf = new float[samples];

        float level = 0f;
        for (int i = 0; i < samples; i++)
        {
            float s = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f * _micVolume;
            _pcmConvertBuf[i] = s;
            float abs = s < 0 ? -s : s;
            if (abs > level) level = abs;
        }
        _localMicLevel = level;   // volatile write – safe

        try
        {
            int encoded = _encoder.Encode(_pcmConvertBuf, samples, _encodeBuffer, _encodeBuffer.Length);
            if (encoded <= 0) return;

            var payload = new byte[encoded];
            Array.Copy(_encodeBuffer, payload, encoded);
            _sendQueue.Enqueue(payload);  // ConcurrentQueue – safe
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Encode error: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Incoming: Hazel thread → enqueue only
    // ══════════════════════════════════════════════════════════════════════════

    // Called from AudioRpcPatch (Hazel network thread).
    internal static void EnqueueAudioPacket(int senderId, byte[] encoded)
    {
        Current?._receiveQueue.Enqueue(new IncomingPacket(senderId, 0, encoded, 0, ""));
    }

    internal static void EnqueueProfilePacket(int senderId, byte playerId, string playerName)
    {
        Current?._receiveQueue.Enqueue(new IncomingPacket(senderId, 1, Array.Empty<byte>(), playerId, playerName));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Per-frame Update  (Unity main thread)
    // ══════════════════════════════════════════════════════════════════════════

    public void Update()
    {
        // 1. Send any pending encoded audio frames via Hazel (main thread safe)
        DrainSendQueue();

        // 2. Process incoming packets (main thread only – modifies _clients/_decoders)
        DrainReceiveQueue();

        // 3. Prune disconnected clients
        PruneDisconnectedClients();

        // 4. Broadcast local profile if changed
        TryUpdateLocalProfile();

        // 5. Comms sabotage check (every 0.5 s)
        _commsSabCheckTimer -= Time.deltaTime;
        if (_commsSabCheckTimer <= 0f)
        {
            _commsSabCheckTimer = 0.5f;
            _commsSabActive     = CheckCommsSabotage();
        }

        // 6. Per-client volume/pan decisions
        var localPlayer  = PlayerControl.LocalPlayer;
        Vector2? listenerPos = localPlayer ? (Vector2)localPlayer.transform.position : null;
        bool localInVent = localPlayer != null && localPlayer.inVent;

        List<SpeakerCache> speakerCache = new();
        if (listenerPos.HasValue)
        {
            float maxRange = VoiceChatConfig.SyncedRoomSettings.MaxChatDistance;
            foreach (var v in _virtualSpeakers)
            {
                float d = Vector2.Distance(v.Position, listenerPos.Value);
                if (d < maxRange)
                    speakerCache.Add(new(v, GetVolume(d, maxRange), GetPan(listenerPos.Value.x, v.Position.x)));
            }
        }

        bool inLobby   = LobbyBehaviour.Instance != null;
        bool inMeeting = MeetingHud.Instance != null || ExileController.Instance != null;
        bool inGame    = ShipStatus.Instance != null;

        foreach (var client in _clients.Values)
        {
            if (inLobby || !inGame)
                client.UpdateLobby();
            else if (inMeeting)
                client.UpdateMeeting();
            else
                client.UpdateTaskPhase(listenerPos, speakerCache, _virtualMics, localInVent, _commsSabActive);
        }
    }

    private void DrainSendQueue()
    {
        if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null) return;

        // Limit how many we send per frame to avoid spikes
        const int maxPerFrame = 4;
        int sent = 0;
        while (sent < maxPerFrame && _sendQueue.TryDequeue(out var payload))
        {
            try
            {
                var w = AmongUsClient.Instance.StartRpcImmediately(
                    PlayerControl.LocalPlayer.NetId, AudioRpcId, SendOption.Reliable, -1);
                w.Write((byte)0);
                w.WriteBytesAndSize(payload);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                sent++;
            }
            catch (Exception ex)
            {
                VoiceChatPluginMain.Logger.LogError($"[VC] RPC send error: {ex.Message}");
            }
        }
    }

    private void DrainReceiveQueue()
    {
        // Process all pending incoming packets; runs on main thread only
        const int maxPerFrame = 16;
        int processed = 0;
        while (processed < maxPerFrame && _receiveQueue.TryDequeue(out var pkt))
        {
            processed++;
            if (pkt.PacketType == 0)
                ProcessAudioFrame(pkt.SenderId, pkt.Data);
            else if (pkt.PacketType == 1)
                ProcessProfileUpdate(pkt.SenderId, pkt.PlayerId, pkt.PlayerName);
        }
    }

    private void ProcessAudioFrame(int senderId, byte[] encoded)
    {
        if (!_decoders.TryGetValue(senderId, out var decoder))
        {
            decoder = AudioHelpers.GetOpusDecoder();
            _decoders[senderId] = decoder;
        }

        // Each sender gets its own decode buffer – never shared
        if (!_decodeBufs.TryGetValue(senderId, out var buf))
        {
            buf = new float[5760]; // max Opus frame at 48 kHz
            _decodeBufs[senderId] = buf;
        }

        int decoded;
        try
        {
            decoded = decoder.Decode(encoded, buf, buf.Length);
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Decode error client {senderId}: {ex.Message}");
            return;
        }
        if (decoded <= 0) return;

        if (!_clients.TryGetValue(senderId, out var player))
        {
            var instance = _audioManager.Generate(senderId);
            player = new VCPlayer(this, instance,
                _imager, _normalVolume, _ghostVolume, _radioVolume, _clientVolume, _levelMeter);
            _clients[senderId] = player;
            // FIX #1: Try to resolve PlayerControl on slot creation, but also
            // apply any buffered profile that arrived before audio started.
            player.TryResolveFromClientId(senderId);
            VoiceChatPluginMain.Logger.LogInfo($"[VC] New client {senderId}.");

            // Apply any profile that was buffered before the audio slot existed.
            if (_pendingProfiles.TryGetValue(senderId, out var pending))
            {
                player.UpdateProfile(pending.PlayerId, pending.PlayerName);
                _pendingProfiles.Remove(senderId);
                VoiceChatPluginMain.Logger.LogInfo(
                    $"[VC] Applied buffered profile to new client {senderId}: id={pending.PlayerId} name={pending.PlayerName}");
            }
        }

        // AddSamples writes to BufferedSampleProvider which uses CircularFloatBuffer (thread-safe).
        // The WasapiOut thread will read from it concurrently; CircularFloatBuffer uses a lock.
        player.AddSamples(buf, decoded);
    }

    // FIX Buffer for profile packets that arrive before the audio slot is created.
    // Key = Hazel clientId, Value = most recent profile update for that client.
    private readonly Dictionary<int, (byte PlayerId, string PlayerName)> _pendingProfiles = new();

    private void ProcessProfileUpdate(int senderId, byte playerId, string playerName)
    {
        if (_clients.TryGetValue(senderId, out var player))
        {
            // Audio slot already exists – apply immediately.
            player.UpdateProfile(playerId, playerName);
            VoiceChatPluginMain.Logger.LogInfo(
                $"[VC] Client {senderId}: id={playerId} name={playerName}");
        }
        else
        {
            // FIX Audio slot doesn't exist yet (profile arrived before any audio).
            // Buffer it; ProcessAudioFrame will apply it when the slot is created.
            _pendingProfiles[senderId] = (playerId, playerName);
            VoiceChatPluginMain.Logger.LogInfo(
                $"[VC] Buffered profile for future client {senderId}: id={playerId} name={playerName}");
        }
    }

    private void PruneDisconnectedClients()
    {
        if (AmongUsClient.Instance == null) return;
        List<int>? toRemove = null;
        foreach (var id in _clients.Keys)
        {
            bool alive = false;
            foreach (var cl in AmongUsClient.Instance.allClients)
                if (cl.Id == id) { alive = true; break; }
            if (!alive) (toRemove ??= new()).Add(id);
        }
        if (toRemove == null) return;
        foreach (var id in toRemove)
        {
            _clients.Remove(id);
            _decoders.Remove(id);
            _decodeBufs.Remove(id);
            _pendingProfiles.Remove(id);
            _audioManager.Remove(id);
            VoiceChatPluginMain.Logger.LogInfo($"[VC] Client {id} pruned.");
        }
    }

    private static bool CheckCommsSabotage()
    {
        if (ShipStatus.Instance == null) return false;
        foreach (var sys in ShipStatus.Instance.Systems.Values)
        {
            var hud = sys.TryCast<HudOverrideSystemType>();
            if (hud != null && hud.IsActive) return true;
        }
        return false;
    }

    public void Rejoin()
    {
        // Drain queues so stale data from the old game doesn't bleed in
        while (_sendQueue.TryDequeue(out _)) { }
        while (_receiveQueue.TryDequeue(out _)) { }

        foreach (var id in _clients.Keys.ToList())
        {
            _audioManager.Remove(id);
            _decoders.Remove(id);
            _decodeBufs.Remove(id);
        }
        _clients.Clear();
        _pendingProfiles.Clear();
        VoiceChatPluginMain.Logger.LogInfo("[VC] Rejoin: state cleared.");
    }

    public void Close()
    {
        // Stop mic first to prevent WaveIn thread from enqueueing after Close
        try { _waveIn?.StopRecording(); } catch { }
        try { _waveIn?.Dispose(); } catch { }
        _waveIn = null;

        // Drain send queue (nothing will be sent after this)
        while (_sendQueue.TryDequeue(out _)) { }
        while (_receiveQueue.TryDequeue(out _)) { }

        // Stop speaker before tearing down the audio graph
        try { _waveOut?.Stop(); _waveOut?.Dispose(); } catch { }
        _waveOut = null;

        _encoder?.Dispose();
        _encoder = null;

        _clients.Clear();
        _decoders.Clear();
        _decodeBufs.Clear();
        _pendingProfiles.Clear();
    }

    public bool TryGetPlayer(byte playerId, out VCPlayer? player)
    {
        foreach (var c in _clients.Values)
            if (c.PlayerId == playerId) { player = c; return true; }
        player = null;
        return false;
    }

    private void TryUpdateLocalProfile() => UpdateLocalProfile(false);

    private void UpdateLocalProfile(bool always)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return;
        if (!always && lp.PlayerId == _lastId && lp.name == _lastName) return;

        _lastId   = lp.PlayerId;
        _lastName = lp.name;

        try
        {
            if (AmongUsClient.Instance == null) return;
            var w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, AudioRpcId, SendOption.Reliable, -1);
            w.Write((byte)1);
            w.Write(_lastId);
            w.Write(_lastName);
            AmongUsClient.Instance.FinishRpcImmediately(w);
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Profile broadcast error: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Utility
    // ══════════════════════════════════════════════════════════════════════════

    internal static float GetVolume(float dist, float maxDist)
        => Math.Clamp(1f - dist / maxDist, 0f, 1f);

    internal static float GetPan(float micX, float spkX)
        => Math.Clamp((spkX - micX) / 3f, -1f, 1f);

    internal record SpeakerCache(IVoiceComponent Speaker, float Volume, float Pan);

    // ── Harmony patch: Hazel network thread – enqueue only ──────────────────────
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
    public static class AudioRpcPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerControl __instance, byte callId, MessageReader reader)
        {
            if (callId != AudioRpcId) return;
            if (Current == null) return;

            // Identify sender
            int senderId = -1;
            if (AmongUsClient.Instance != null)
            {
                var cl = AmongUsClient.Instance.GetClientFromCharacter(__instance);
                if (cl != null) senderId = cl.Id;
            }
            if (senderId < 0) return;
            // Ignore our own echo
            if (AmongUsClient.Instance != null &&
                senderId == AmongUsClient.Instance.ClientId) return;

            try
            {
                byte packetType = reader.ReadByte();
                if (packetType == 0)
                {
                    // Read the encoded bytes immediately on this thread (MessageReader is not thread-safe to defer)
                    byte[] encoded = reader.ReadBytesAndSize();
                    if (encoded != null && encoded.Length > 0)
                        EnqueueAudioPacket(senderId, encoded);
                }
                else if (packetType == 1)
                {
                    byte   pid  = reader.ReadByte();
                    string name = reader.ReadString();
                    EnqueueProfilePacket(senderId, pid, name);
                }
            }
            catch (Exception ex)
            {
                VoiceChatPluginMain.Logger.LogError($"[VC] RPC parse error: {ex.Message}");
            }
        }
    }
}
