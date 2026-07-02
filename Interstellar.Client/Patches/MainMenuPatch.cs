using HarmonyLib;
using System.Globalization;
using UnityEngine;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin;

/// <summary>
/// Adds a "Buy Me a Coffee" button on the main menu,
/// styled like TOR's Discord button, positioned directly above it.
/// </summary>
[HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Awake))]
public static class MainMenuCoffeeButtonPatch
{
    private static Sprite? _coffeeSprite;

    private static bool IsChinese()
    {
        try
        {
            var name = CultureInfo.CurrentUICulture.Name;
            if (name.StartsWith("zh")) return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static void Postfix(MainMenuManager __instance)
    {
        if (_coffeeSprite == null)
            _coffeeSprite = InterstellarHudState.LoadSpriteFromResources("VoiceChatPlugin.Resources.CoffeeButton.png", 100f);
        if (_coffeeSprite == null) return;

        var go = new GameObject("CoffeeButton");
        go.transform.SetParent(__instance.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.layer = LayerMask.NameToLayer("UI");

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _coffeeSprite;
        sr.sortingOrder = 32767;

        var aspect = go.AddComponent<AspectPosition>();
        aspect.Alignment = AspectPosition.EdgeAlignments.RightBottom;
        aspect.parentCam = HudManager.InstanceExists ? HudManager.Instance.UICamera : Camera.main;
        aspect.DistanceFromEdge = new Vector3(0.34f, 1.6f, -6f);
        aspect.AdjustPosition();

        var button = go.AddComponent<PassiveButton>();
        button.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        button.OnClick.AddListener((System.Action)(() =>
        {
            var url = IsChinese()
                ? "https://amongusclub.cn/archives/co-fi"
                : "https://ko-fi.com/fangkuaiya";
            // Application.OpenURL is unreliable in-game (silently no-ops on some
            // platforms/builds, especially Android). The game's own Constants.OpenUrl
            // is what the base game uses for its external link buttons and reliably
            // opens the link in the system/external browser.
            Constants.OpenURL(url);
        }));
        button.OnMouseOver = new UnityEngine.Events.UnityEvent();
        button.OnMouseOver.AddListener((System.Action)(() => sr.color = Color.green));
        button.OnMouseOut = new UnityEngine.Events.UnityEvent();
        button.OnMouseOut.AddListener((System.Action)(() => sr.color = Color.white));

        // Collider for click detection
        var col = go.AddComponent<CircleCollider2D>();
        col.radius = 0.25f;
    }
}
