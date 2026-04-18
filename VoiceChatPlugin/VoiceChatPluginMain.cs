using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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

	// Embedded dependency DLLs are stored as resources with this prefix.
	private const string ResPrefix = "Lib.";
	private static readonly Dictionary<string, Assembly> _asmCache
		= new(StringComparer.OrdinalIgnoreCase);

	static VoiceChatPluginMain()
	{
		AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;
	}

	private static Assembly? ResolveEmbeddedAssembly(object? sender, ResolveEventArgs args)
	{
		var shortName = new AssemblyName(args.Name).Name;
		if (shortName == null) return null;

		if (_asmCache.TryGetValue(shortName, out var cached)) return cached;

		var resourceName = ResPrefix + shortName + ".dll";
		var asm = Assembly.GetExecutingAssembly();
		using var stream = asm.GetManifestResourceStream(resourceName);
		if (stream == null) return null;

		using var ms = new MemoryStream();
		stream.CopyTo(ms);
		var loaded = Assembly.Load(ms.ToArray());
		_asmCache[shortName] = loaded;
		return loaded;
	}

	public override void Load()
    {
		Logger = Log;
        Logger.LogInfo("[VC] Loading VoiceChatPlugin (source-inlined, Hazel transport).");

		LocalizationManager.Register(new HardCodedLocalizationProvider());
		VoiceChat.VoiceChatConfig.Init(Config);
		Options.SetupCustomSettings();
		Harmony.PatchAll(Assembly.GetExecutingAssembly());

		Logger.LogInfo("[VC] VoiceChatPlugin loaded.");
    }
}
