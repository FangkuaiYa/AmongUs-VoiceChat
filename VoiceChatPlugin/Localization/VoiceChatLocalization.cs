using AmongUs.Data;

namespace VoiceChatPlugin;

public static class VoiceChatLocalization
{
	private const uint SChinese = 13;
	private const uint TChinese = 14;

	private static readonly Dictionary<string, string> English = new()
	{
		["speaking"] = "Speaking",
		["missingPlayers"] = "No VC plugin: {0}",
		["default"] = "Default",
		["header"] = "Voice Chat Options",
		["audioDevices"] = "Audio Devices",
		["micVolume"] = "Mic Volume",
		["microphone"] = "Microphone",
		["speakerVolume"] = "Speaker Volume",
		["speaker"] = "Speaker",
		["roomSettings"] = "Room Settings",
		["walls"] = "Walls",
		["passThrough"] = "✓ Pass Through",
		["blocked"] = "× Blocked",
		["maxDistance"] = "Max Distance",
		["wallsPass"] = "Walls: Pass ✓",
		["wallsBlock"] = "Walls: Block ×",
		["distanceMinus"] = "Distance  −",
		["distancePlus"] = "Distance  +",
	};

	private static readonly Dictionary<string, string> SimplifiedChinese = new()
	{
		["speaking"] = "说话中",
		["missingPlayers"] = "未装语音插件: {0}",
		["default"] = "默认",
		["header"] = "语音聊天设置",
		["audioDevices"] = "音频设备",
		["micVolume"] = "麦克风音量",
		["microphone"] = "麦克风",
		["speakerVolume"] = "扬声器音量",
		["speaker"] = "扬声器",
		["roomSettings"] = "房间设置",
		["walls"] = "隔墙",
		["passThrough"] = "✓ 可穿墙",
		["blocked"] = "× 受阻挡",
		["maxDistance"] = "最大聊天距离",
		["wallsPass"] = "隔墙: 可穿透 ✓",
		["wallsBlock"] = "隔墙: 阻挡 ×",
		["distanceMinus"] = "距离  −",
		["distancePlus"] = "距离  +",
	};

	private static readonly Dictionary<string, string> TraditionalChinese = new()
	{
		["speaking"] = "說話中",
		["missingPlayers"] = "未裝語音插件: {0}",
		["default"] = "預設",
		["header"] = "語音聊天設定",
		["audioDevices"] = "音訊裝置",
		["micVolume"] = "麥克風音量",
		["microphone"] = "麥克風",
		["speakerVolume"] = "喇叭音量",
		["speaker"] = "喇叭",
		["roomSettings"] = "房間設定",
		["walls"] = "隔牆",
		["passThrough"] = "✓ 可穿牆",
		["blocked"] = "× 受阻擋",
		["maxDistance"] = "最大距離",
		["wallsPass"] = "隔牆: 可穿透 ✓",
		["wallsBlock"] = "隔牆: 阻擋 ×",
		["distanceMinus"] = "距離  −",
		["distancePlus"] = "距離  +",
	};

	public static string Tr(string key)
	{
		var table = GetTable();
		if (table.TryGetValue(key, out var text)) return text;
		if (English.TryGetValue(key, out var fallback)) return fallback;
		return key;
	}

	private static Dictionary<string, string> GetTable()
	{
		uint langId = (uint)(DataManager.Settings?.Language?.CurrentLanguage ?? 0);
		return langId switch
		{
			SChinese => SimplifiedChinese,
			TChinese => TraditionalChinese,
			_ => English,
		};
	}
}
