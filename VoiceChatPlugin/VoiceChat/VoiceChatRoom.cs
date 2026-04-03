using Concentus;
using Hazel;
using HarmonyLib;

using VoiceChatPlugin.Audio;

using NAudio.Wave;
using UnityEngine;
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
///   WaveInEvent (PCM 48 kHz mono, 16-bit)
///   -> Opus encoder (20 ms frames)
///   -> RPC 203 broadcast (Hazel reliable UDP)
///
/// Incoming pipeline:
///   RPC 203 arrives
///   -> Opus decoder
///   -> AudioRoutingInstance.AddSamples()
///   -> Interstellar routing graph (volume, stereo pan, ghost reverb, radio)
///   -> WasapiOut (WASAPI shared mode)
/// </summary>
public class VoiceChatRoom
{
    // RPC ID used for all voice-chat packets.
    // Must not collide with VoiceChatRoomSettings (201) or any vanilla game RPC.
    internal const byte AudioRpcId = 203;

    // ── Singleton ──────────────────────────────────────────────────────────────
    public static VoiceChatRoom? Current { get; private set; }

    // ── Interstellar audio routing graph ──────────────────────────────────────
    private readonly AudioManager          _audioManager;
    private readonly VolumeRouter.Property _masterVolumeProperty;

    private readonly StereoRouter     _imager;
    private readonly VolumeRouter     _normalVolume, _ghostVolume, _radioVolume, _clientVolume;
    private readonly LevelMeterRouter _levelMeter;

    // ── Remote clients ─────────────────────────────────────────────────────────
    // Key = Among Us ClientId (AmongUsClient.GetClientFromCharacter().Id)
    private readonly Dictionary<int, VCPlayer>     _clients  = new();
    private readonly Dictionary<int, IOpusDecoder> _decoders = new();
    private readonly float[] _decodeBuffer = new float[4096];

    public IEnumerable<VCPlayer> AllClients => _clients.Values;

    // ── Virtual components (camera / vent) ─────────────────────────────────────
    private readonly List<IVoiceComponent> _virtualMics     = new();
    private readonly List<IVoiceComponent> _virtualSpeakers = new();
    public void AddVirtualMicrophone(IVoiceComponent c)    => _virtualMics.Add(c);
    public void AddVirtualSpeaker(IVoiceComponent c)       => _virtualSpeakers.Add(c);
    public void RemoveVirtualMicrophone(IVoiceComponent c) => _virtualMics.Remove(c);
    public void RemoveVirtualSpeaker(IVoiceComponent c)    => _virtualSpeakers.Remove(c);

    // ── Microphone ─────────────────────────────────────────────────────────────
    private WaveInEvent?  _waveIn;
    private IOpusEncoder? _encoder;
    private readonly byte[] _encodeBuffer = new byte[4096];
    private float[] _pcmConvertBuf = null!;
    private float _micVolume = 1f;

    public bool  UsingMicrophone => _waveIn != null;
    public float LocalMicLevel   => _localMicLevel;
    private volatile float _localMicLevel;
    public bool  Mute  { get; private set; }
    public int   SampleRate => AudioHelpers.ClockRate;

    // ── Speaker ────────────────────────────────────────────────────────────────
    private WasapiOut? _waveOut;

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
    // Constructor – builds the Interstellar routing graph
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

        VoiceChatPluginMain.Logger.LogInfo("[VC] VoiceChatRoom constructed (Hazel transport, no external server).");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Device control
    // ══════════════════════════════════════════════════════════════════════════

    public void SetMasterVolume(float v) => _masterVolumeProperty.Volume = v;

    public void SetMicVolume(float v)
    {
        _micVolume = Math.Clamp(v, 0f, 2f);
    }

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
                $"[VC] Mic set: '{(string.IsNullOrEmpty(deviceName) ? "default" : deviceName)}'");
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

            var endpoint = _audioManager.Endpoint;
            if (endpoint == null)
            {
                VoiceChatPluginMain.Logger.LogError("[VC] Audio graph has no endpoint – speaker not started.");
                return;
            }

            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            NAudio.CoreAudioApi.MMDevice? device = null;
            if (!string.IsNullOrEmpty(deviceName))
            {
                foreach (var d in enumerator.EnumerateAudioEndPoints(
                             NAudio.CoreAudioApi.DataFlow.Render,
                             NAudio.CoreAudioApi.DeviceState.Active))
                {
                    if (d.FriendlyName == deviceName) { device = d; break; }
                }
            }
            device ??= enumerator.GetDefaultAudioEndpoint(
                NAudio.CoreAudioApi.DataFlow.Render,
                NAudio.CoreAudioApi.Role.Multimedia);

            _waveOut = new WasapiOut(device, NAudio.CoreAudioApi.AudioClientShareMode.Shared, false, 50);
            _waveOut.Init(endpoint);
            _waveOut.Play();

            VoiceChatPluginMain.Logger.LogInfo(
                $"[VC] Speaker set: '{(string.IsNullOrEmpty(deviceName) ? "default" : deviceName)}'");
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Speaker init failed: {ex.Message}");
            _waveOut = null;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Outgoing: mic capture -> Opus -> RPC broadcast
    // ══════════════════════════════════════════════════════════════════════════

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (Mute || _encoder == null) return;
        if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null) return;

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

        try
        {
            int encoded = _encoder.Encode(_pcmConvertBuf, samples, _encodeBuffer, _encodeBuffer.Length);
            if (encoded <= 0) return;

            var payload = new byte[encoded];
            Array.Copy(_encodeBuffer, payload, encoded);

            // Packet type 0 = audio frame
            var w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, AudioRpcId, SendOption.Reliable, -1);
            w.Write((byte)0);
            w.WriteBytesAndSize(payload);
            AmongUsClient.Instance.FinishRpcImmediately(w);
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Mic send error: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Incoming: RPC -> Opus decode -> audio graph
    // ══════════════════════════════════════════════════════════════════════════

    private void DispatchAudioFrame(int senderId, byte[] encoded)
    {
        if (!_decoders.TryGetValue(senderId, out var decoder))
        {
            decoder = AudioHelpers.GetOpusDecoder();
            _decoders[senderId] = decoder;
        }

        int decoded = decoder.Decode(encoded, _decodeBuffer, _decodeBuffer.Length);
        if (decoded <= 0) return;

        if (!_clients.TryGetValue(senderId, out var player))
        {
            var instance = _audioManager.Generate(senderId);
            player = new VCPlayer(this, instance,
                _imager, _normalVolume, _ghostVolume, _radioVolume, _clientVolume, _levelMeter);
            _clients[senderId] = player;
            player.TryResolveFromClientId(senderId);
            VoiceChatPluginMain.Logger.LogInfo($"[VC] New remote audio client {senderId}.");
        }

        player.AddSamples(_decodeBuffer, decoded);
    }

    private void DispatchProfileUpdate(int senderId, byte playerId, string playerName)
    {
        if (_clients.TryGetValue(senderId, out var player))
        {
            player.UpdateProfile(playerId, playerName);
            VoiceChatPluginMain.Logger.LogInfo(
                $"[VC] Client {senderId}: playerId={playerId} name={playerName}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Per-frame Update
    // ══════════════════════════════════════════════════════════════════════════

    public void Update()
    {
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

    // ══════════════════════════════════════════════════════════════════════════
    // Lifecycle
    // ══════════════════════════════════════════════════════════════════════════

    public void Rejoin()
    {
        foreach (var id in _clients.Keys.ToList())
        {
            _audioManager.Remove(id);
            _decoders.Remove(id);
        }
        _clients.Clear();
        foreach (var c in _clients.Values) c.ResetMapping();
        VoiceChatPluginMain.Logger.LogInfo("[VC] Rejoin: client state cleared.");
    }

    public void Close()
    {
        try { _waveIn?.StopRecording(); _waveIn?.Dispose(); } catch { }
        _waveIn = null;
        try { _waveOut?.Stop(); _waveOut?.Dispose(); } catch { }
        _waveOut = null;
        _encoder?.Dispose();
        _encoder = null;
        _clients.Clear();
        _decoders.Clear();
    }

    public bool TryGetPlayer(byte playerId, out VCPlayer? player)
    {
        foreach (var c in _clients.Values)
            if (c.PlayerId == playerId) { player = c; return true; }
        player = null;
        return false;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Local profile broadcast
    // ══════════════════════════════════════════════════════════════════════════

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
            // Packet type 1 = profile update
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

    // ── Harmony patch: intercept incoming RPC 203 ──────────────────────────────
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
    public static class AudioRpcPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerControl __instance, byte callId, MessageReader reader)
        {
            if (callId != AudioRpcId) return;
            if (Current == null) return;

            int senderId = -1;
            if (AmongUsClient.Instance != null)
            {
                var cl = AmongUsClient.Instance.GetClientFromCharacter(__instance);
                if (cl != null) senderId = cl.Id;
            }
            if (senderId < 0) return;
            // Skip our own RPC echo
            if (AmongUsClient.Instance != null &&
                senderId == AmongUsClient.Instance.ClientId) return;

            try
            {
                byte packetType = reader.ReadByte();
                if (packetType == 0)
                {
                    byte[] encoded = reader.ReadBytesAndSize();
                    if (encoded != null && encoded.Length > 0)
                        Current.DispatchAudioFrame(senderId, encoded);
                }
                else if (packetType == 1)
                {
                    byte   pid  = reader.ReadByte();
                    string name = reader.ReadString();
                    Current.DispatchProfileUpdate(senderId, pid, name);
                }
            }
            catch (Exception ex)
            {
                VoiceChatPluginMain.Logger.LogError($"[VC] RPC dispatch error: {ex.Message}");
            }
        }
    }
}
