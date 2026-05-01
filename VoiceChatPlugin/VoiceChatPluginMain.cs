using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using VoiceChatPlugin.Reactor;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin;

[BepInPlugin(Id, "Voice Chat Plugin", "1.0.0")]
[BepInProcess("Among Us.exe")]
public class VoiceChatPluginMain : BasePlugin
{
    public const string Id = "com.voicechatplugin.cn";
    public static ManualLogSource Logger { get; private set; } = null!;
    public Harmony Harmony { get; } = new(Id);

    private const string ResPrefix = "Lib.";
    private static readonly Dictionary<string, Assembly> _asmCache
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Mirrors Nebula's ResidentBehaviour.gameObject:
    /// a DontDestroyOnLoad + MarkDontUnload persistent GameObject.
    /// The Android AudioSource speaker is AddComponent'd directly onto this.
    ///
    /// Nebula:
    ///   var residentObj = new GameObject("ResidentObject");
    ///   residentObj.AddComponent&lt;ResidentBehaviour&gt;().MarkDontUnload();
    ///   residentObj.MarkDontUnload();
    /// </summary>
    public static GameObject? ResidentObject { get; private set; }

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
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
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

        // Nebula:
        //   var residentObj = new GameObject("ResidentObject");
        //   residentObj.AddComponent<ResidentBehaviour>().MarkDontUnload();
        //   residentObj.MarkDontUnload();
        ResidentObject = new GameObject("VC_ResidentObject");
        GameObject.DontDestroyOnLoad(ResidentObject);
        ResidentObject.hideFlags |= HideFlags.DontUnloadUnusedAsset | HideFlags.HideAndDontSave;

        // Nebula: SceneManager.sceneLoaded += (scene, mode) => { new GameObject("NebulaManager").AddComponent<NebulaManager>(); }
        VCManager.RegisterSceneHook();

        // Init HUD state (registers scene-change cleanup for buttons/tooltips)
        VoiceChatHudState.Init();

        // Nebula: Harmony.PatchAll() — but we ONLY patch what Nebula also patches:
        // VoiceChatRoomSettings RPC (PlayerControl.HandleRpc) and
        // MeetingSpeakingIndicator and Options/VoiceVolumeMenu patches.
        // The core room lifecycle is driven by VCManager (MonoBehaviour), NOT patches.
        LocalizationManager.Register(new HardCodedLocalizationProvider());
        VoiceChatConfig.Init(Config);
        Options.SetupCustomSettings();
        Harmony.PatchAll(Assembly.GetExecutingAssembly());

        Logger.LogInfo("[VC] VoiceChatPlugin loaded.");
    }
}
