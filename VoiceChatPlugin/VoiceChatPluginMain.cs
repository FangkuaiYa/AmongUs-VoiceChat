using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using VoiceChatPlugin.Reactor;

namespace VoiceChatPlugin;

[BepInPlugin(Id, "Voice Chat Plugin", "1.0.0")]
[BepInProcess("Among Us.exe")]
public class VoiceChatPluginMain : BasePlugin
{
    public const string Id = "com.voicechatplugin.cn";
    public static ManualLogSource Logger { get; private set; } = null!;
	public Harmony Harmony { get; } = new(Id);

	public override void Load()
    {
        Logger = Log;
        Logger.LogInfo("[VC] Loading VoiceChatPlugin (source-inlined, Hazel transport).");

		LocalizationManager.Register(new HardCodedLocalizationProvider());
		VoiceChat.VoiceChatConfig.Init(Config);
		Options.SetupCustomSettings();
		Harmony.PatchAll();

		Logger.LogInfo("[VC] VoiceChatPlugin loaded.");
    }
}
