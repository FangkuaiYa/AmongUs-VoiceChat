using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.SceneManagement;
using Interstellar.Voice;

namespace Interstellar;

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
        new GameObject("VC_Manager").AddComponent<VCManager>();

        switch (scene.name)
        {
            case "MainMenu":
            case "MatchMaking":
                VoiceRoom.CloseCurrentRoom();
                break;
        }
    }

    void Update()
    {
        switch (SceneManager.GetActiveScene().name)
        {
            case "OnlineGame":
            case "EndGame":
                InterstellarHudState.UpdateHud();
                InterstellarRoomDriver.Update();
                break;
        }
    }
}
