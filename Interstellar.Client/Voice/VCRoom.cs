using Interstellar.Network;
using Interstellar.Routing;
using NAudio.Wave;
using Interstellar;
using Interstellar.Voice;

namespace Interstellar.Voice;

public class VCRoomParameters
{
    public VCRoom.OnConnectClient? OnConnectClient;
    public VCRoom.OnUpdateProfile? OnUpdateProfile;
    public VCRoom.CustomMessageHandler? MessageHandler;
    public VCRoom.OnDisconnect? OnDisconnect;
    public VCRoom.OnUpdateMuteStatus? OnUpdateMuteStatus;
    public int BufferMaxLength = 4096;
    public int BufferLength = 2048;

    public VCRoomParameters SetBufferLength(int length, int additional = 2048)
    {
        BufferLength = length;
        BufferMaxLength = length + additional;
        return this;
    }
}

public class VCRoom : IConnectionContext, IHasAudioPropertyNode, IMicrophoneContext, ISpeakerContext
{
    private ServerConnection connection;
    private AudioManager audioManager;
    private Dictionary<int, AudioRoutingInstance> audioInstances = new();
    private readonly object _audioLock = new();
    private readonly OnConnectClient? onConnectClient;
    private readonly OnUpdateProfile? onUpdateProfile;
    private readonly CustomMessageHandler? onCustomMessage;
    private readonly OnDisconnect? onDisconnect;
    private readonly OnUpdateMuteStatus? onUpdateMuteStatus;
    private bool loopBack = false;

    public delegate void OnConnectClient(int clientId, AudioRoutingInstance routing, bool isLocalClient);
    public delegate void OnUpdateProfile(int clientId, byte playerId, string playerName);
    public delegate void OnUpdateMuteStatus(int clientId, bool mute, bool isImpostorRadio);
    public delegate void OnDisconnect(int clientId);
    public delegate void CustomMessageHandler(byte[] message);

    private readonly Dictionary<int, bool> _clientImpostorRadio = new();
    private readonly Dictionary<int, bool> _clientMuted = new();

    public bool IsClientImpostorRadio(int clientId)
    {
        lock (_audioLock) { return _clientImpostorRadio.TryGetValue(clientId, out var v) && v; }
    }
    public bool IsClientMuted(int clientId)
    {
        lock (_audioLock) { return _clientMuted.TryGetValue(clientId, out var v) && v; }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="audioRouter"></param>
    /// <param name="roomCode"></param>
    /// <param name="region"></param>
    /// <param name="url"></param>
    /// <param name="onConnectClient"></param>
    /// <param name="onUpdateProfile">Called when a profile is updated. Even for previously shared profiles, it is guaranteed to be called after onConnectClient notifies the connection.</param>
    public VCRoom(AbstractAudioRouter audioRouter, string roomCode, string region, string url, VCRoomParameters? additionalParameters)
    {
        this.onConnectClient = additionalParameters?.OnConnectClient;
        this.onUpdateProfile = additionalParameters?.OnUpdateProfile;
        this.onCustomMessage = additionalParameters?.MessageHandler;
        this.onDisconnect = additionalParameters?.OnDisconnect;
        this.onUpdateMuteStatus = additionalParameters?.OnUpdateMuteStatus;

        this.connection = new ServerConnection(this, roomCode, region, url);
        this.audioManager = new AudioManager(audioRouter, additionalParameters?.BufferLength ?? 2048, additionalParameters?.BufferMaxLength ?? 4096);
    }

    public void SetLoopBack(bool enable) => this.loopBack = enable;

    /// <summary>
    /// Updates the local player profile.
    /// Call this after a game ends, when returning to the lobby, etc.
    /// </summary>
    /// <param name="playerName">Player name.</param>
    /// <param name="playerId">Player ID.</param>
    public void UpdateProfile(string playerName, byte playerId, int clientId)
        => this.connection.UpdateProfile(playerName, playerId, clientId);

    /// <summary>
    private float _localLevel;
    public float LocalLevel => _localLevel;
    private bool _firstAudioSent;

    /// Sends audio data.
    /// </summary>
    /// <param name="samples"></param>
    /// <param name="length"></param>
    void IMicrophoneContext.SendAudio(float[] samples, int samplesLength, double samplesMilliseconds, float coeff)
    {
        for(int i = 0; i < samplesLength; i++) samples[i] *= coeff;
        // Track local mic peak for self-speaking indicator (always, even without loopback)
        float max = 0f;
        for (int i = 0; i < samplesLength; i++)
        {
            float abs = Math.Abs(samples[i]);
            if (abs > max) max = abs;
        }
        _localLevel = max;
        if (!Mute)
        {
            if (!_firstAudioSent)
            {
                _firstAudioSent = true;
                InterstellarPlugin.Logger.LogInfo("[VC:MicTx] First audio frame sent to server.");
            }
            this.connection.SendAudio(samples, samplesLength, samplesMilliseconds);
        }
        OnAudioSent(samples, samplesLength);
    }

    ISampleProvider? ISpeakerContext.GetEndpoint() => audioManager.Endpoint;

    IMicrophone? microphone = null;
    public IMicrophone? Microphone
    {
        get => microphone;
        set
        {
            this.microphone?.Close();
            value?.Initialize(this);
            this.microphone = value;
        }
    }
    public void SetMicrophone(IMicrophone? microphone) => Microphone = microphone;

    ISpeaker? speaker = null;
    public ISpeaker? Speaker
    {
        get => speaker;
        set
        {
            this.speaker?.Close();
            value?.Initialize(this);
            this.speaker = value;
        }
    }
    public void SetSpeaker(ISpeaker? speaker) => Speaker = speaker;

    private AudioRoutingInstance GetOrCreateAudioInstance(int clientId, bool asLocalClient)
    {
        lock (_audioLock)
        {
            if (!audioInstances.TryGetValue(clientId, out var instance))
            {
                instance = audioManager.Generate(clientId);
                onConnectClient?.Invoke(clientId, instance, asLocalClient);
                audioInstances[clientId] = instance;
                if (pooledProfile.TryGetValue(clientId, out var profile))
                {
                    onUpdateProfile?.Invoke(clientId, profile.id, profile.name);
                    pooledProfile.Remove(clientId);
                }
            }
            return instance;
        }
    }

    private bool TryGetAudioInstance(int clientId, out AudioRoutingInstance? instance)
    {
        lock (_audioLock)
        {
            return audioInstances.TryGetValue(clientId, out instance);
        }
    }

    AudioRoutingInstanceNode IHasAudioPropertyNode.GetProperty(int propertyId) => (audioManager as IHasAudioPropertyNode).GetProperty(propertyId);

    void IConnectionContext.OnAudioFrameReceived(int clientId, float[] samples, int length)
    {
        var instance = GetOrCreateAudioInstance(clientId, false);
        instance.AddSamples(samples, 0, length);
    }

    void IConnectionContext.OnClientConnected(int clientId)
    {
        GetOrCreateAudioInstance(clientId, false);
    }

    void IConnectionContext.OnClientDisconnected(int clientId)
    {
        lock (_audioLock)
        {
            if(audioInstances.TryGetValue(clientId, out var instance))
            {
                audioManager.Remove(clientId);
                audioInstances.Remove(clientId);
                onDisconnect?.Invoke(clientId);
            }
        }
    }

    void IConnectionContext.OnCustomMessageReceived(byte[] message)
    {
        onCustomMessage?.Invoke(message);
    }

    public void SendCustomMessage(byte[] message)
    {
        connection.SendCustomMessage(message);
    }

    public void Rejoin()
    {
        // Rejoin by updating local profile — server will re-sync state.
        // The new protocol has no explicit reload; reconnect handles it.
    }

    Dictionary<int, (string name, byte id)> pooledProfile = [];
    void IConnectionContext.OnClientProfileUpdated(int clientId, string playerName, byte playerId)
    {
        lock (_audioLock)
        {
            if (audioInstances.TryGetValue(clientId, out _))
            {
                onUpdateProfile?.Invoke(clientId, playerId, playerName);
            }
            else
            {
                pooledProfile[clientId] = (playerName, playerId);
            }
        }
    }

    void IConnectionContext.OnReceiveMuteStatus(int clientId, bool isMute, bool isImpostorRadio)
    {
        lock (_audioLock)
        {
            _clientMuted[clientId] = isMute;
            _clientImpostorRadio[clientId] = isImpostorRadio;
        }
        onUpdateMuteStatus?.Invoke(clientId, isMute, isImpostorRadio);
    }

    void IConnectionContext.OnHostSettingsReceived(byte[] rawSettings)
    {
        // Deserialize host settings from legacy binary format
        // Format: [4 bytes float: MaxChatDistance][1 byte bool each for remaining 11 flags]
        if (rawSettings.Length < 4 + 11) return;
        var s = VoiceConfig.SyncedRoomSettings;
        int p = 0;
        s.MaxChatDistance = System.BitConverter.ToSingle(rawSettings, p); p += 4;
        s.WallsBlockSound = rawSettings[p++] != 0;
        s.OnlyHearInSight = rawSettings[p++] != 0;
        s.ImpostorHearGhosts = rawSettings[p++] != 0;
        s.OnlyGhostsCanTalk = rawSettings[p++] != 0;
        s.HearInVent = rawSettings[p++] != 0;
        s.HearVentPlayers = rawSettings[p++] != 0;
        s.VentPrivateChat = rawSettings[p++] != 0;
        s.CommsSabDisables = rawSettings[p++] != 0;
        s.CameraCanHear = rawSettings[p++] != 0;
        s.ImpostorPrivateRadio = rawSettings[p++] != 0;
        s.OnlyMeetingOrLobby = rawSettings[p++] != 0;
        VoiceConfig.OnSyncedSettingsChanged?.Invoke(s);
    }

    void IConnectionContext.OnServerInfoReceived(int optimalPlayers, int totalClients, string serverUrl)
    {
        VoiceServerState.Update(optimalPlayers, totalClients, serverUrl);
    }

    public void SendHostSettings(VoiceRoomSettings s)
    {
        // Serialize to binary: [4 bytes float: maxDist][11 bytes: bools]
        var raw = new byte[4 + 11];
        int p = 0;
        var distBytes = System.BitConverter.GetBytes(s.MaxChatDistance);
        System.Buffer.BlockCopy(distBytes, 0, raw, p, 4); p += 4;
        raw[p++] = (byte)(s.WallsBlockSound ? 1 : 0);
        raw[p++] = (byte)(s.OnlyHearInSight ? 1 : 0);
        raw[p++] = (byte)(s.ImpostorHearGhosts ? 1 : 0);
        raw[p++] = (byte)(s.OnlyGhostsCanTalk ? 1 : 0);
        raw[p++] = (byte)(s.HearInVent ? 1 : 0);
        raw[p++] = (byte)(s.HearVentPlayers ? 1 : 0);
        raw[p++] = (byte)(s.VentPrivateChat ? 1 : 0);
        raw[p++] = (byte)(s.CommsSabDisables ? 1 : 0);
        raw[p++] = (byte)(s.CameraCanHear ? 1 : 0);
        raw[p++] = (byte)(s.ImpostorPrivateRadio ? 1 : 0);
        raw[p++] = (byte)(s.OnlyMeetingOrLobby ? 1 : 0);
        connection.SendHostSettings(raw);
    }

    void OnAudioSent(float[] buffer, int count)
    {
        if (loopBack && connection.MyClientId != -1)
        {
            var instance = GetOrCreateAudioInstance(connection.MyClientId, true);
            instance.AddSamples(buffer, 0, count);
        }
    }

    private bool mute = false;
    public bool Mute => mute;
    public void SetMute(bool mute, bool isImpostorRadio = false)
    {
        if (this.mute == mute && _lastSentRadio == isImpostorRadio) return;
        this.mute = mute;
        _lastSentRadio = isImpostorRadio;
        connection.UpdateMuteStatus(mute, isImpostorRadio);
    }
    private bool _lastSentRadio;

    public void Disconnect()
    {
        connection.Disconnect();
        Microphone = null;
        Speaker = null;
    }

    // ── Public lobby ──────────────────────────────────────────

    public async System.Threading.Tasks.Task PublishLobby(string code, PublicLobbyManager.LobbyInfo info)
        => await connection.PublishLobby(code, info);

    public async System.Threading.Tasks.Task RemoveLobby(string code)
        => await connection.RemoveLobby(code);

    public async System.Threading.Tasks.Task JoinLobby(int lobbyId, Action<int, string, string> cb)
        => await connection.JoinLobby(lobbyId, cb);

    public async System.Threading.Tasks.Task WatchLobbyBrowser(bool watch)
        => await connection.WatchLobbyBrowser(watch);

    public const int SampleRateConst = 48000;
    public int SampleRate => SampleRateConst;
}
