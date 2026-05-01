using Concentus;
using Hazel;
using HarmonyLib;
using VoiceChatPlugin.Audio;
#if WINDOWS
using NAudio.Wave;
using NAudio.CoreAudioApi;
#endif
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
/// Platform support:
///   WINDOWS  - Outgoing mic: NAudio WaveInEvent on its own callback thread.
///              Incoming speaker: NAudio WasapiOut on its own playback thread.
///   ANDROID  - Outgoing mic: Unity Microphone API polled each frame (main thread).
///              Incoming speaker: Unity AudioSource + streaming AudioClip
///              (PCMReaderCallback on Unity audio thread).
///
/// Outgoing pipeline (both platforms):
///   PCM float samples (48 kHz mono)
///   -> Opus encode
///   -> enqueue into _sendQueue  (thread-safe)
///   -> dequeued on Unity main thread and sent via Hazel RPC
///
/// Incoming pipeline (both platforms):
///   RPC 203 arrives (on Hazel network thread)
///   -> enqueue into _receiveQueue  (thread-safe)
///   -> dequeued on Unity main thread in Update()
///   -> Opus decode (per-client decoder, own float[] buffer)
///   -> AudioRoutingInstance.AddSamples() + AndroidSpeaker.WriteMono() [ANDROID]
///   -> audio routing graph (volume, stereo, ghost reverb, radio)
///   -> WasapiOut playback thread [WINDOWS] / Unity AudioSource [ANDROID]
/// </summary>
public class VoiceChatRoom
{
    internal const byte AudioRpcId = 203;

    // Singleton
    public static VoiceChatRoom? Current { get; private set; }

    // Audio routing graph
    private readonly AudioManager          _audioManager;
    private readonly VolumeRouter.Property _masterVolumeProperty;

    private readonly StereoRouter     _imager;
    private readonly VolumeRouter     _normalVolume, _ghostVolume, _radioVolume, _clientVolume;
    private readonly LevelMeterRouter _levelMeter;

    // Remote clients - Unity main thread only
    private readonly Dictionary<int, VCPlayer>     _clients    = new();
    private readonly Dictionary<int, IOpusDecoder> _decoders   = new();
    private readonly Dictionary<int, float[]>      _decodeBufs = new();

    public IEnumerable<VCPlayer> AllClients => _clients.Values;

    // Virtual components (camera / vent)
    private readonly List<IVoiceComponent> _virtualMics     = new();
    private readonly List<IVoiceComponent> _virtualSpeakers = new();
    public void AddVirtualMicrophone(IVoiceComponent c)    => _virtualMics.Add(c);
    public void AddVirtualSpeaker(IVoiceComponent c)       => _virtualSpeakers.Add(c);
    public void RemoveVirtualMicrophone(IVoiceComponent c) => _virtualMics.Remove(c);
    public void RemoveVirtualSpeaker(IVoiceComponent c)    => _virtualSpeakers.Remove(c);

    // Microphone - platform-specific capture backend
    private IOpusEncoder? _encoder;
    private float[]       _pcmConvertBuf = null!;
    private readonly byte[] _encodeBuffer = new byte[4096];
    private float _micVolume = 1f;

    // Encoded packets queued from mic capture path, drained on main thread
    private readonly ConcurrentQueue<byte[]> _sendQueue = new();

#if WINDOWS
    private WaveInEvent? _waveIn;
    public bool UsingMicrophone => _waveIn != null;
#elif ANDROID
    private AndroidMicrophone? _androidMic;
    public bool UsingMicrophone => _androidMic != null;
#else
    public bool UsingMicrophone => false;
#endif

    public float LocalMicLevel   => _localMicLevel;
    private volatile float _localMicLevel;
    public bool Mute  { get; private set; }
    public int  SampleRate => AudioHelpers.ClockRate;

    // Speaker - platform-specific output backend
#if WINDOWS
    private WasapiOut? _waveOut;
#elif ANDROID
    private AndroidSpeaker? _androidSpeaker;
#endif

    // Incoming packet queue: Hazel thread -> main thread
    private readonly record struct IncomingPacket(int SenderId, byte PacketType, byte[] Data, byte PlayerId, string PlayerName);
    private readonly ConcurrentQueue<IncomingPacket> _receiveQueue = new();

    // Comms sabotage cache
    private bool  _commsSabActive;
    private float _commsSabCheckTimer;

    // Local profile tracking
    private byte   _lastId   = byte.MaxValue;
    private string _lastName = null!;

    // ======================================================================
    // Factory
    // ======================================================================

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

    // ======================================================================
    // Constructor
    // ======================================================================

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

#if WINDOWS
        SetSpeaker(VoiceChatConfig.SpeakerDevice);
#elif ANDROID
        // Nebula pattern: use the persistent ResidentObject (DontDestroyOnLoad) as the
        // AudioSource host so the speaker is never accidentally destroyed when
        // HudManager rebuilds on scene transitions.
        // Nebula: AudioSource added to ResidentBehaviour.gameObject + MarkDontUnload.
        // AndroidSpeaker() replicates this via VoiceChatPluginMain.ResidentObject.
        if (VoiceChatPluginMain.ResidentObject != null)
            InitAndroidSpeaker();
        else
            VoiceChatPluginMain.Logger.LogWarning("[VC] Android: ResidentObject not available.");
#endif

        VoiceChatPluginMain.Logger.LogInfo("[VC] VoiceChatRoom constructed (Hazel transport).");
    }

    // ======================================================================
    // Device control  (called from Unity main thread)
    // ======================================================================

    public void SetMasterVolume(float v)
    {
        _masterVolumeProperty.Volume = v;
#if ANDROID
        _androidSpeaker?.SetMasterVolume(v);
#endif
    }

    public void SetMicVolume(float v) => _micVolume = Math.Clamp(v, 0f, 2f);

    public void SetMute(bool mute)
    {
        Mute = mute;
        if (mute) _localMicLevel = 0f;
    }
    public void ToggleMute() => SetMute(!Mute);
    public void SetLoopBack(bool lb) { }

    // Microphone

    public void SetMicrophone(string deviceName)
    {
#if WINDOWS
        SetMicrophoneWindows(deviceName);
#elif ANDROID
        SetMicrophoneAndroid(deviceName);
#endif
    }

#if WINDOWS
    private void SetMicrophoneWindows(string deviceName)
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
            _waveIn.DataAvailable += OnMicDataAvailableWindows;
            _waveIn.StartRecording();

            VoiceChatPluginMain.Logger.LogInfo(
                $"[VC] Windows mic: '{(string.IsNullOrEmpty(deviceName) ? "default" : deviceName)}'");
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Windows mic init failed: {ex.Message}");
            _waveIn  = null;
            _encoder = null;
        }
    }
#endif

#if ANDROID
    private void SetMicrophoneAndroid(string deviceName)
    {
        // Nebula: CheckAndShowConfirmPopup wraps microphone start on Android.
        // Nebula's latest code shows a "noMic" confirmation dialog before allowing mic.
        // We use a coroutine to request Android microphone permission first,
        // then start capturing once authorized — mirroring Nebula's intent.
        if (VoiceChatPluginMain.ResidentObject != null)
        {
            var behaviour = VoiceChatPluginMain.ResidentObject.GetComponent<PermissionHelper>()
                ?? VoiceChatPluginMain.ResidentObject.AddComponent<PermissionHelper>();
            behaviour.RequestMicAndStart(this, deviceName ?? "");
        }
        else
        {
            StartMicNow(deviceName ?? "");
        }
    }

    // Called by PermissionHelper after permission is granted (or immediately if already granted).
    internal void StartMicNow(string deviceName)
    {
        try
        {
            _androidMic?.Stop();
            _androidMic?.Dispose();
            _encoder?.Dispose();

            _encoder    = AudioHelpers.GetOpusEncoder();
            // Nebula: interstellarRoom.Microphone = new ManualMicrophone();
            _androidMic = new AndroidMicrophone();
            _androidMic.DataAvailable += OnAndroidMicData;
            _androidMic.Start(deviceName);
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Android mic init failed: {ex.Message}");
            _androidMic = null;
            _encoder    = null;
        }
    }
#endif

    // Speaker

#if WINDOWS
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

            var enumerator = new MMDeviceEnumerator();
            MMDevice? device = null;
            if (!string.IsNullOrEmpty(deviceName))
            {
                foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (d.FriendlyName == deviceName) { device = d; break; }
                }
            }
            device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            _waveOut = new WasapiOut(device, AudioClientShareMode.Shared, false, 50);
            _waveOut.Init(ep);
            _waveOut.Play();

            VoiceChatPluginMain.Logger.LogInfo(
                $"[VC] Windows speaker: '{(string.IsNullOrEmpty(deviceName) ? "default" : deviceName)}'");
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Windows speaker init failed: {ex.Message}");
            _waveOut = null;
        }
    }
#endif

#if ANDROID
    private void InitAndroidSpeaker()
    {
        try
        {
            _androidSpeaker?.Dispose();
            // Nebula: ManualSpeaker.Read() pulls from the Interstellar graph endpoint.
            // We pass _audioManager.Endpoint so the PCMReaderCallback drives the
            // full audio routing graph (volume, pan, reverb, radio) — same as Nebula.
            if (_audioManager.Endpoint == null)
                throw new InvalidOperationException("AudioManager endpoint is null");
            _androidSpeaker = new AndroidSpeaker(_audioManager.Endpoint);
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Android speaker init failed: {ex.Message}");
            _androidSpeaker = null;
        }
    }
#endif

    // ======================================================================
    // Outgoing mic data capture
    // ======================================================================

#if WINDOWS
    private void OnMicDataAvailableWindows(object? sender, WaveInEventArgs e)
    {
        if (Mute || _encoder == null)
        {
            _localMicLevel = 0f;
            return;
        }

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
        _localMicLevel = level;

        EncodeAndEnqueue(_pcmConvertBuf, samples);
    }
#endif

#if ANDROID
    // DataAvailable callback from AndroidMicrophoneInput - fires on main thread via Tick().
    private void OnAndroidMicData(float[] buf, int length)
    {
        if (Mute || _encoder == null)
        {
            _localMicLevel = 0f;
            return;
        }

        const int frameSize = 960; // 20 ms @ 48 kHz
        int offset = 0;
        while (offset + frameSize <= length)
        {
            if (_pcmConvertBuf == null || _pcmConvertBuf.Length < frameSize)
                _pcmConvertBuf = new float[frameSize];

            float level = 0f;
            for (int i = 0; i < frameSize; i++)
            {
                float s = buf[offset + i] * _micVolume;
                _pcmConvertBuf[i] = s;
                float abs = s < 0 ? -s : s;
                if (abs > level) level = abs;
            }
            _localMicLevel = level;
            EncodeAndEnqueue(_pcmConvertBuf, frameSize);
            offset += frameSize;
        }
    }
#endif

    private void EncodeAndEnqueue(float[] pcm, int sampleCount)
    {
        if (_encoder == null) return;
        try
        {
            int encoded = _encoder.Encode(pcm, sampleCount, _encodeBuffer, _encodeBuffer.Length);
            if (encoded <= 0) return;
            var payload = new byte[encoded];
            Array.Copy(_encodeBuffer, payload, encoded);
            _sendQueue.Enqueue(payload);
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Encode error: {ex.Message}");
        }
    }

    // ======================================================================
    // Incoming: Hazel thread -> enqueue only
    // ======================================================================

    internal static void EnqueueAudioPacket(int senderId, byte[] encoded)
    {
        Current?._receiveQueue.Enqueue(new IncomingPacket(senderId, 0, encoded, 0, ""));
    }

    internal static void EnqueueProfilePacket(int senderId, byte playerId, string playerName)
    {
        Current?._receiveQueue.Enqueue(new IncomingPacket(senderId, 1, Array.Empty<byte>(), playerId, playerName));
    }

    // ======================================================================
    // Per-frame Update  (Unity main thread)
    // ======================================================================

    public void Update()
    {
#if ANDROID
        // Nebula pattern: PushAudioData() is called each frame from UpdateInternal().
        // We replicate this with Tick() which drains new mic samples and fires DataAvailable.
        PollAndroidMic();
#endif

        DrainSendQueue();
        DrainReceiveQueue();
        PruneDisconnectedClients();
        TryUpdateLocalProfile();

        _commsSabCheckTimer -= Time.deltaTime;
        if (_commsSabCheckTimer <= 0f)
        {
            _commsSabCheckTimer = 0.5f;
            _commsSabActive     = CheckCommsSabotage();
        }

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

#if ANDROID
    private void PollAndroidMic()
    {
        _androidMic?.Tick();
    }
#endif

    private void DrainSendQueue()
    {
        if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null) return;

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

        if (!_decodeBufs.TryGetValue(senderId, out var buf))
        {
            buf = new float[5760];
            _decodeBufs[senderId] = buf;
        }

        int decoded;
        try { decoded = decoder.Decode(encoded, buf, buf.Length); }
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
            player.TryResolveFromClientId(senderId);
            VoiceChatPluginMain.Logger.LogInfo($"[VC] New client {senderId}.");

            if (_pendingProfiles.TryGetValue(senderId, out var pending))
            {
                player.UpdateProfile(pending.PlayerId, pending.PlayerName);
                _pendingProfiles.Remove(senderId);
                VoiceChatPluginMain.Logger.LogInfo(
                    $"[VC] Applied buffered profile to new client {senderId}: id={pending.PlayerId} name={pending.PlayerName}");
            }
        }

        // Feed decoded PCM into audio routing graph.
        // On Windows, WasapiOut pulls from the graph endpoint on its own thread.
        // On Android, we additionally push to AndroidSpeaker which drives the
        // Unity AudioSource streaming clip via its PCMReaderCallback.
        player.AddSamples(buf, decoded);

// Android: no WriteMono needed — AndroidSpeaker.Read() pulls from _audioManager.Endpoint
        // directly via PCMReaderCallback, exactly as Nebula's ManualSpeaker does.
    }

    private readonly Dictionary<int, (byte PlayerId, string PlayerName)> _pendingProfiles = new();

    private void ProcessProfileUpdate(int senderId, byte playerId, string playerName)
    {
        if (_clients.TryGetValue(senderId, out var player))
        {
            player.UpdateProfile(playerId, playerName);
            VoiceChatPluginMain.Logger.LogInfo($"[VC] Client {senderId}: id={playerId} name={playerName}");
        }
        else
        {
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
        while (_sendQueue.TryDequeue(out _)) { }
        while (_receiveQueue.TryDequeue(out _)) { }
        foreach (var id in _clients.Keys.ToList())
        {
            _audioManager.Remove(id);
            _decoders.Remove(id);
            _decodeBufs.Remove(id);
        }
        _clients.Clear();
        _decoders.Clear();
        _decodeBufs.Clear();
        _pendingProfiles.Clear();
        // Nebula: clients.Values.Do(c => c.ResetMapping())
        // Already cleared above; broadcast profile so others can re-map us
        _lastId   = byte.MaxValue;
        _lastName = null!;
        VoiceChatPluginMain.Logger.LogInfo("[VC] Rejoin: state cleared.");
    }

    public void Close()
    {
#if WINDOWS
        try { _waveIn?.StopRecording(); } catch { }
        try { _waveIn?.Dispose(); } catch { }
        _waveIn = null;
#elif ANDROID
        try { _androidMic?.Stop(); } catch { }
        try { _androidMic?.Dispose(); } catch { }
        _androidMic = null;
        try { _androidSpeaker?.Dispose(); } catch { }
        _androidSpeaker = null;
#endif

        while (_sendQueue.TryDequeue(out _)) { }
        while (_receiveQueue.TryDequeue(out _)) { }

#if WINDOWS
        try { _waveOut?.Stop(); _waveOut?.Dispose(); } catch { }
        _waveOut = null;
#endif

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

    internal void ForceUpdateLocalProfile() => UpdateLocalProfile(true);

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

    // ======================================================================
    // Utility
    // ======================================================================

    internal static float GetVolume(float dist, float maxDist)
        => Math.Clamp(1f - dist / maxDist, 0f, 1f);

    internal static float GetPan(float micX, float spkX)
        => Math.Clamp((spkX - micX) / 3f, -1f, 1f);

    internal record SpeakerCache(IVoiceComponent Speaker, float Volume, float Pan);

    // Harmony patch: Hazel network thread - enqueue only.
    // Self-filter uses LocalPlayer.NetId comparison (reliable on Android IL2CPP).
    // GetClientFromCharacter can fail on Android so we avoid it for the self-check.
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
    public static class AudioRpcPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerControl __instance, byte callId, MessageReader reader)
        {
            if (callId != AudioRpcId) return;
            if (Current == null) return;

            // Filter out our own PlayerControl (self-loopback prevention).
            // Comparing NetId is reliable on both PC and Android IL2CPP.
            var localPlayer = PlayerControl.LocalPlayer;
            if (localPlayer != null && __instance.NetId == localPlayer.NetId) return;

            // Derive senderId from the PlayerControl's owning client.
            int senderId = -1;
            if (AmongUsClient.Instance != null)
            {
                var cl = AmongUsClient.Instance.GetClientFromCharacter(__instance);
                if (cl != null) senderId = cl.Id;
            }
            // Fallback: use PlayerId as a unique key if GetClientFromCharacter fails
            if (senderId < 0)
                senderId = __instance.PlayerId + 1000; // offset to avoid collision with real clientIds

            try
            {
                byte packetType = reader.ReadByte();
                if (packetType == 0)
                {
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
