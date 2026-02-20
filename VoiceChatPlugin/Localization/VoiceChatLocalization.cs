using AmongUs.Data;

namespace VoiceChatPlugin;

public static class VoiceChatLocalization
{
	private const uint SChinese = 13;
	private const uint TChinese = 14;

	private static readonly Dictionary<string, string> English = new()
	{
		["speaking"]             = "Speaking",
		["missingPlayers"]       = "No VC: {0}",
		["default"]              = "Default",
		["header"]               = "Voice Chat Options",
		["audioDevices"]         = "Audio Devices",
		["micVolume"]            = "Mic Volume",
		["microphone"]           = "Microphone",
		["speakerVolume"]        = "Speaker Volume",
		["speaker"]              = "Speaker",
		["roomSettings"]         = "Room Settings",
		["walls"]                = "Walls",
		["passThrough"]          = "Pass Through",
		["blocked"]              = "Blocked",
		["maxDistance"]          = "Max Distance",
		["wallsPass"]            = "Walls: Open",
		["wallsBlock"]           = "Walls: Block",
		["distanceMinus"]        = "Dist  −",
		["distancePlus"]         = "Dist  +",
		// page navigation
		["pageDevices"]          = "▷ Rules",
		["pageRules"]            = "◁ Devices",
		// flag toggles
		["flagOn"]               = "ON",
		["flagOff"]              = "OFF",
		["onlyHearInSight"]      = "Only Hear In Sight",
		["impostorHearGhosts"]   = "Impostor Hear Ghosts",
		["hearInVent"]           = "Hear Players In Vent",
		["ventPrivateChat"]      = "Vent Private Chat",
		["commsSabDisables"]     = "Comms Sab Disables VC",
		["cameraCanHear"]        = "Camera Hears Sounds",
		["impostorPrivateRadio"] = "Impostor Private Radio",
		["onlyGhostsCanTalk"]    = "Only Ghosts Can Talk",
		["onlyMeetingOrLobby"]   = "Only Meeting / Lobby",
	};

	private static readonly Dictionary<string, string> SimplifiedChinese = new()
	{
		["speaking"]             = "说话中",
		["missingPlayers"]       = "未装语音: {0}",
		["default"]              = "默认",
		["header"]               = "语音聊天设置",
		["audioDevices"]         = "音频设备",
		["micVolume"]            = "麦克风音量",
		["microphone"]           = "麦克风",
		["speakerVolume"]        = "扬声器音量",
		["speaker"]              = "扬声器",
		["roomSettings"]         = "房间设置",
		["walls"]                = "隔墙",
		["passThrough"]          = "可穿墙",
		["blocked"]              = "受阻挡",
		["maxDistance"]          = "最大聊天距离",
		["wallsPass"]            = "隔墙: 穿透",
		["wallsBlock"]           = "隔墙: 阻挡",
		["distanceMinus"]        = "距离  −",
		["distancePlus"]         = "距离  +",
		["pageDevices"]          = "▷ 规则",
		["pageRules"]            = "◁ 设备",
		["flagOn"]               = "开",
		["flagOff"]              = "关",
		["onlyHearInSight"]      = "仅能听见视野内的人",
		["impostorHearGhosts"]   = "内鬼能听见幽灵",
		["hearInVent"]           = "能听见管道里的人",
		["ventPrivateChat"]      = "管道内私聊",
		["commsSabDisables"]     = "破坏通讯会禁用语音",
		["cameraCanHear"]        = "监控可以收听声音",
		["impostorPrivateRadio"] = "内鬼私密通话",
		["onlyGhostsCanTalk"]    = "只有幽灵可以语音",
		["onlyMeetingOrLobby"]   = "仅限会议/大厅内语音",
	};

	private static readonly Dictionary<string, string> TraditionalChinese = new()
	{
		["speaking"]             = "說話中",
		["missingPlayers"]       = "未裝語音: {0}",
		["default"]              = "預設",
		["header"]               = "語音聊天設定",
		["audioDevices"]         = "音訊裝置",
		["micVolume"]            = "麥克風音量",
		["microphone"]           = "麥克風",
		["speakerVolume"]        = "喇叭音量",
		["speaker"]              = "喇叭",
		["roomSettings"]         = "房間設定",
		["walls"]                = "隔牆",
		["passThrough"]          = "可穿牆",
		["blocked"]              = "受阻擋",
		["maxDistance"]          = "最大距離",
		["wallsPass"]            = "隔牆: 穿透",
		["wallsBlock"]           = "隔牆: 阻擋",
		["distanceMinus"]        = "距離  −",
		["distancePlus"]         = "距離  +",
		["pageDevices"]          = "▷ 規則",
		["pageRules"]            = "◁ 裝置",
		["flagOn"]               = "開",
		["flagOff"]              = "關",
		["onlyHearInSight"]      = "僅能聽見視野內的人",
		["impostorHearGhosts"]   = "內鬼能聽見幽靈",
		["hearInVent"]           = "能聽見管道裡的人",
		["ventPrivateChat"]      = "管道內私聊",
		["commsSabDisables"]     = "破壞通訊禁用語音",
		["cameraCanHear"]        = "監控可以收聽聲音",
		["impostorPrivateRadio"] = "內鬼私密通話",
		["onlyGhostsCanTalk"]    = "只有幽靈可以語音",
		["onlyMeetingOrLobby"]   = "僅限會議/大廳內語音",
	};

	public static string Tr(string key)
	{
		var t = GetTable();
		if (t.TryGetValue(key, out var v)) return v;
		if (English.TryGetValue(key, out var e)) return e;
		return key;
	}

	private static Dictionary<string, string> GetTable()
	{
		uint lang = (uint)(DataManager.Settings?.Language?.CurrentLanguage ?? 0);
		return lang switch
		{
			SChinese => SimplifiedChinese,
			TChinese => TraditionalChinese,
			_        => English,
		};
	}
}
