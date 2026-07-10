using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin;

[BepInPlugin(Id, "Interstellar Voice Chat", "2.1.1")]
[BepInProcess("Among Us.exe")]
public class InterstellarPlugin : BasePlugin
{
    public const string Id = "com.voicechatplugin.cn";
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
        Logger.LogInfo("[VC] Loading VoiceChatPlugin.");

        VoiceChatConfig.Init(Config);
        TranslationHelper.Load();
        CustomServerLoader.Load();

        // Register splash runner early so JoinSplashScreen works on first join
        Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<JoinSplashScreen.SplashCoroutineRunner>();

        VCManager.RegisterSceneHook();
        InterstellarHudState.Init();

        Harmony harmony = new(Id);
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        Logger.LogInfo("[VC] VoiceChatPlugin loaded.");
    }
}
