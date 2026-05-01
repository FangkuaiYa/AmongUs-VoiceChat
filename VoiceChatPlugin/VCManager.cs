using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.SceneManagement;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin;

/// <summary>
/// Per-scene MonoBehaviour that drives the VC room lifecycle.
/// Mirrors Nebula's NebulaManager: created fresh for every scene load,
/// NOT DontDestroyOnLoad. The ResidentObject is separate and IS persistent.
///
/// Nebula pattern:
///   SceneManager.sceneLoaded += (scene, mode) =>
///       new GameObject("NebulaManager").AddComponent&lt;NebulaManager&gt;();
///
///   void Update() => OnUpdate(SceneManager.GetActiveScene().name);
///   void OnUpdate("OnlineGame"|"EndGame") => NoSVCRoom.Update();
///   void OnSceneChanged("MainMenu"|"MatchMaking") => NoSVCRoom.CloseCurrentRoom();
/// </summary>
internal class VCManager : MonoBehaviour
{
    static VCManager()
    {
        ClassInjector.RegisterTypeInIl2Cpp<VCManager>();
    }

    internal static void RegisterSceneHook()
    {
        SceneManager.sceneLoaded +=
            (UnityEngine.Events.UnityAction<Scene, LoadSceneMode>)OnSceneLoaded;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode _)
    {
        // Nebula: new GameObject("NebulaManager").AddComponent<NebulaManager>();
        new GameObject("VC_Manager").AddComponent<VCManager>();

        // Nebula: OnSceneChanged(sceneName)
        switch (scene.name)
        {
            case "MainMenu":
            case "MatchMaking":
                VoiceChatRoom.CloseCurrentRoom();
                break;
        }
    }

    void Update()
    {
        switch (SceneManager.GetActiveScene().name)
        {
            case "OnlineGame":
            case "EndGame":
                // Nebula: vc.UpdateVoiceChatInfo(); vc.UpdateInternal(); vc.UpdateRadio();
                VoiceChatHudState.UpdateHud();
                VoiceChatRoomDriver.Update();
                break;
        }
    }
}
