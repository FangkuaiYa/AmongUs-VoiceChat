using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System.Reflection;

namespace VoiceChatPlugin;

[BepInPlugin(Id, "Voice Chat Plugin", "1.0.0")]
[BepInProcess("Among Us.exe")]
public class VoiceChatPluginMain : BasePlugin
{
	public const string Id = "com.voicechatplugin.cn";
	public static ManualLogSource Logger { get; private set; } = null!;

	private const string ResPrefixMadeByFangkuaiYa = "Lib.";
	private static readonly Dictionary<string, Assembly> _cacheTestByElinmeiFarewellTAIKImp11 = new(StringComparer.OrdinalIgnoreCase);

	static VoiceChatPluginMain()
	{
		AppDomain.CurrentDomain.AssemblyResolve += ResolveAssemblyUISomeByFarewell;
	}

	private static Assembly? ResolveAssemblyUISomeByFarewell(object? sender, ResolveEventArgs args)
	{
		var shortName = new AssemblyName(args.Name).Name;
		if (shortName == null) return null;

		if (_cacheTestByElinmeiFarewellTAIKImp11.TryGetValue(shortName, out var cached)) return cached;

		var resourceName = ResPrefixMadeByFangkuaiYa + shortName + ".dll";
		var asm = Assembly.GetExecutingAssembly();
		using var stream = asm.GetManifestResourceStream(resourceName);
		if (stream == null) return null;

		using var ms = new MemoryStream();
		stream.CopyTo(ms);
		var loaded = Assembly.Load(ms.ToArray());
		_cacheTestByElinmeiFarewellTAIKImp11[shortName] = loaded;
		return loaded;
	}

	public override void Load()
	{
		Logger = Log;

		try
		{
			Logger.LogInfo("[VC] All embedded dependencies registered via AssemblyResolve.");
		}
		catch (Exception ex)
		{
			Logger.LogError("[VC] Initialization error: " + ex);
			return;
		}

		VoiceChat.VoiceChatConfig.Init(Config);
		new Harmony(Id).PatchAll(Assembly.GetExecutingAssembly());

		Logger.LogInfo("[VC] VoiceChatPlugin loaded successfully.");
	}
}