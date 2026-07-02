using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using VoiceChatPlugin.VoiceChat;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceChatSettingsMenu
{
    static VoiceChatSettingsMenu()
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<VoiceChatSettingsWindow>();
        Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<JoinSplashScreen.SplashCoroutineRunner>();

        SceneManager.sceneLoaded += (Action<Scene, LoadSceneMode>)((_, __) =>
        {
            Window?.Close();
        });
    }

    private static VoiceChatSettingsWindow? _window;
    private static VoiceChatSettingsWindow? Window
    {
        get
        {
            if (!_window)
            {
                _window = Object.FindObjectOfType<VoiceChatSettingsWindow>();
                if (!_window)
                {
                    var hostGO = new GameObject("VC_SettingsHost");
                    Object.DontDestroyOnLoad(hostGO);
                    _window = hostGO.AddComponent<VoiceChatSettingsWindow>();
                    VoiceChatConfig.RefreshDeviceCaches();
                }
            }
            return _window;
        }
    }

    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Start))]
    [HarmonyPostfix]
    static void OptionsMenu_Start(OptionsMenuBehaviour __instance)
    {
        if (!__instance.CensorChatButton) return;
        var parent = __instance.CensorChatButton.transform.parent;
        if (parent == null || parent.Find("VC_SettingsBtn")) return;

        var src = __instance.CensorChatButton;
        var btn = Object.Instantiate(src.gameObject, parent).GetComponent<PassiveButton>();
        btn.name = "VC_SettingsBtn";

        // AmongUsClient.GameState has 4 values: NotJoined, Joined, Started, Ended.
        // "Joined" = waiting lobby, "Started" = game actually started. Both of those
        // should use the same button position; only the main menu (NotJoined) differs.
        var state = AmongUsClient.Instance?.GameState ?? InnerNet.InnerNetClient.GameStates.NotJoined;
        bool inMainMenu = state == InnerNet.InnerNetClient.GameStates.NotJoined;
        btn.transform.localPosition = inMainMenu
            ? new Vector3(-1.34f, 2.99f, 0f)
            : new Vector3(-1.94f, -1.58f, 0f);
        btn.transform.localScale = new Vector3(0.49f, 0.82f, 1f);

        var label = btn.GetComponentInChildren<TextMeshPro>();
        if (label)
        {
            label.text = TranslationHelper.Get("vc.settings.btnVC", "VC");
            label.transform.localScale = new Vector3(1.8f, 0.95f, 1f);
        }

        btn.gameObject.SetActive(true);
        btn.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        btn.OnClick.AddListener((Action)Open);
    }

    static void Open()
    {
        Window?.Toggle();
    }
}
