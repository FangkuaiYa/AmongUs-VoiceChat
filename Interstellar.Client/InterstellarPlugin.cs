using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Interstellar.Voice;

namespace Interstellar;

[BepInPlugin(Id, "Interstellar Voice Chat", PluginVersion)]
[BepInProcess("Among Us.exe")]
public class InterstellarPlugin : BasePlugin
{
    public const string Id = "com.interstellar.voice";
    public const string PluginVersion = "3.1.2";
    public static ManualLogSource Logger { get; private set; } = null!;

    private const string ResPrefix = "Lib.";
    private static readonly Dictionary<string, Assembly> _asmCache
        = new(StringComparer.OrdinalIgnoreCase);

    static InterstellarPlugin()
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
        Logger.LogInfo("[VC] Loading Interstellar.");

        VoiceConfig.Init(Config);
        TranslationHelper.Load();

        // Register IL2CPP types
        ClassInjector.RegisterTypeInIl2Cpp<JoinSplashScreen.SplashCoroutineRunner>();
        ClassInjector.RegisterTypeInIl2Cpp<VoiceSettingsWindow>();
        ClassInjector.RegisterTypeInIl2Cpp<PublicLobbyWindow>();
        ClassInjector.RegisterTypeInIl2Cpp<PlayerVolumeWindow>();

        var settingsWindow = this.AddComponent<VoiceSettingsWindow>();
        var lobbyWindow = this.AddComponent<PublicLobbyWindow>();
        var playerVolumeWindow = this.AddComponent<PlayerVolumeWindow>();

        _ = settingsWindow;
        _ = lobbyWindow;
        _ = playerVolumeWindow;

        VCManager.RegisterSceneHook();
        InterstellarHudState.Init();

        Harmony harmony = new(Id);
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        Logger.LogInfo("[VC] Interstellar loaded.");
    }
}
