using BepInEx.Configuration;

namespace VoiceChatPlugin.VoiceChat;

public static class VoiceChatConfig
{
	private static ConfigFile? _cfg;

	public static string MicrophoneDevice => _mic?.Value ?? "";
	public static string SpeakerDevice => _speaker?.Value ?? "";
	public static string ServerAddress => _server?.Value ?? "";
	public static float MasterVolume => _masterVol?.Value ?? 1f;
	public static float MicVolume => _micVol?.Value ?? 1f;
	public static bool HostCanTalkThroughWalls => _hostCanTalkThroughWalls?.Value ?? true;
	public static float HostMaxChatDistance => _hostMaxChatDistance?.Value ?? 6f;

	public static VoiceChatRoomSettings SyncedRoomSettings { get; } = new(true, 6f);

	private static ConfigEntry<string>? _mic, _speaker, _server;
	private static ConfigEntry<float>? _masterVol, _micVol;
	private static ConfigEntry<bool>? _hostCanTalkThroughWalls;
	private static ConfigEntry<float>? _hostMaxChatDistance;

	public static void Init(ConfigFile config)
	{
		_cfg = config;
		_mic = config.Bind("VoiceChat", "MicrophoneDevice", "",
						"Microphone device name to use. Leave empty for default device.");
		_speaker = config.Bind("VoiceChat", "SpeakerDevice", "",
						"Speaker device name to use. Leave empty for default device.");
		_server = config.Bind("VoiceChat", "ServerAddress", "",
						"VC server address (e.g., ws://example.com:22010). Leave empty to use official server.");
		_masterVol = config.Bind("VoiceChat", "MasterVolume", 1f,
						new ConfigDescription("Master output volume [0.0 – 2.0]",
							new AcceptableValueRange<float>(0.1f, 2f)));
		_micVol = config.Bind("VoiceChat", "MicVolume", 1f,
						new ConfigDescription("Microphone input volume [0.0 – 2.0]",
							new AcceptableValueRange<float>(0.1f, 2f)));

		_hostCanTalkThroughWalls = config.Bind("VoiceChat.RoomHost", "CanTalkThroughWalls", true,
						"(Host only) Whether walls block proximity voice chat.");
		_hostMaxChatDistance = config.Bind("VoiceChat.RoomHost", "MaxChatDistance", 6f,
						new ConfigDescription("(Host only) Maximum distance for hearing players [1.5 – 20.0]",
							new AcceptableValueRange<float>(1.5f, 20f)));

		ApplyLocalHostSettingsToSynced();
	}

	public static void SetMicrophoneDevice(string device) => _mic!.Value = device;
	public static void SetSpeakerDevice(string device) => _speaker!.Value = device;
	public static void SetServerAddress(string address) => _server!.Value = address;
	public static void SetMasterVolume(float v) => _masterVol!.Value = v;
	public static void SetMicVolume(float v) => _micVol!.Value = v;
	public static void SetHostCanTalkThroughWalls(bool canTalk) => _hostCanTalkThroughWalls!.Value = canTalk;
	public static void SetHostMaxChatDistance(float distance) => _hostMaxChatDistance!.Value = distance;

	public static void ApplyLocalHostSettingsToSynced()
		=> SyncedRoomSettings.Apply(HostCanTalkThroughWalls, HostMaxChatDistance);

	public static void ApplySyncedRoomSettings(bool canTalkThroughWalls, float maxChatDistance)
		=> SyncedRoomSettings.Apply(canTalkThroughWalls, maxChatDistance);
}
