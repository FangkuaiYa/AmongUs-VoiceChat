using System.Collections;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using TMPro;
using UnityEngine;
using Interstellar.Voice;
using static Interstellar.Voice.TranslationHelper;
using Object = UnityEngine.Object;

namespace Interstellar;

/// <summary>
/// Shows a splash screen overlay when joining a voice room.
/// Displays the voice server address, optimal player count, and current player count.
/// Triggered from InterstellarRoomDriver when VoiceRoom.Start() succeeds.
/// </summary>
public static class JoinSplashScreen
{
    private static bool _isShowing;

    /// <summary>
    /// Call when VoiceRoom.Start() succeeds. Shows a fade-in → hold → fade-out overlay.
    /// Does NOT require ServerInfo to be received yet — shows what's available.
    /// </summary>
    private static MonoBehaviour? _runner;

    public static void Show()
    {
        if (_isShowing) return;

        _isShowing = true;

        if (!_runner)
        {
            var go = new GameObject("VC_SplashRunner");
            Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<SplashCoroutineRunner>();
        }

        _runner!.StartCoroutine(ShowSplash().WrapToIl2Cpp());
    }

    private static IEnumerator ShowSplash()
    {
        // Wait for camera to exist (scene may still be loading)
        Camera? cam = null;
        var waited = 0f;
        const float maxWait = 3f;
        while (cam == null && waited < maxWait)
        {
            cam = Camera.main;
            if (cam == null)
            {
                yield return null;
                waited += Time.deltaTime;
            }
        }

        if (cam == null)
        {
            InterstellarPlugin.Logger?.LogWarning("[VC] JoinSplash: no camera after 3s, giving up.");
            _isShowing = false;
            yield break;
        }

        var overlay = new GameObject("VC_JoinSplashOverlay");
        overlay.transform.SetParent(cam.transform, false);
        overlay.transform.localPosition = new Vector3(0f, 0f, 3f);
        overlay.transform.localRotation = Quaternion.identity;
        overlay.transform.localScale = new Vector3(30f, 30f, 1f);

        var bgSr = overlay.AddComponent<SpriteRenderer>();
        bgSr.sprite = CreateSolidSprite();
        bgSr.sortingOrder = 32766;
        bgSr.color = Color.clear;

        var textGo = new GameObject("VC_JoinSplashText");
        textGo.transform.SetParent(cam.transform, false);
        textGo.transform.localPosition = new Vector3(0f, 0f, 3.1f);
        textGo.transform.localRotation = Quaternion.identity;

        var textTmp = textGo.AddComponent<TextMeshPro>();
        textTmp.fontSize = 2.0f;
        textTmp.fontStyle = FontStyles.Bold;
        textTmp.alignment = TextAlignmentOptions.Center;
        textTmp.color = new Color(1f, 1f, 1f, 0f);
        textTmp.sortingOrder = 32767;

        textTmp.text = BuildSplashText();

        // Fade in (0.4s)
        const float fadeInDuration = 0.4f;
        var elapsed = 0f;
        while (elapsed < fadeInDuration)
        {
            // The overlay/text are parented to the camera. If a scene change happens
            // mid-fade (disconnect, kick, game end, etc.) Unity destroys them along
            // with the old camera, but this coroutine keeps running — writing to a
            // destroyed SpriteRenderer/TMP throws a NullReferenceException deep in
            // IL2CPP. Bail out cleanly instead of touching destroyed objects.
            if (!bgSr || !textTmp)
            {
                _isShowing = false;
                yield break;
            }

            elapsed += Time.deltaTime;
            var t = Mathf.SmoothStep(0f, 1f, elapsed / fadeInDuration);
            bgSr.color = Color.Lerp(Color.clear, new Color(0f, 0f, 0f, 0.88f), t);
            textTmp.color = Color.Lerp(new Color(1f, 1f, 1f, 0f), new Color(1f, 1f, 1f, 0.8f), t);
            yield return null;
        }

        if (!bgSr || !textTmp)
        {
            _isShowing = false;
            yield break;
        }

        bgSr.color = new Color(0f, 0f, 0f, 0.88f);
        textTmp.color = new Color(1f, 1f, 1f, 0.8f);

        // Hold (2.5s) — poll for ServerInfo arrival and update text
        var holdEnd = Time.time + 2.5f;
        var hadInfo = VoiceServerState.HasInfo;
        while (Time.time < holdEnd)
        {
            if (!textTmp)
            {
                _isShowing = false;
                yield break;
            }

            if (!hadInfo && VoiceServerState.HasInfo)
            {
                textTmp.text = BuildSplashText();
                hadInfo = true;
            }
            yield return null;
        }

        if (!bgSr || !textTmp)
        {
            _isShowing = false;
            yield break;
        }

        // Fade out (0.8s)
        const float fadeOutDuration = 0.8f;
        elapsed = 0f;
        var startBg = bgSr.color;
        var startText = textTmp.color;
        while (elapsed < fadeOutDuration)
        {
            if (!bgSr || !textTmp)
            {
                _isShowing = false;
                yield break;
            }

            elapsed += Time.deltaTime;
            var t = Mathf.SmoothStep(0f, 1f, elapsed / fadeOutDuration);
            bgSr.color = Color.Lerp(startBg, Color.clear, t);
            textTmp.color = Color.Lerp(startText, new Color(1f, 1f, 1f, 0f), t);
            yield return null;
        }

        if (overlay) Object.Destroy(overlay);
        if (textGo) Object.Destroy(textGo);
        _isShowing = false;
    }

    private static string BuildSplashText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"<b>{Get("vc.splash.title", "Interstellar Voice Chat")}</b>");
        sb.AppendLine();

        if (VoiceServerState.HasInfo)
        {
            var locLabel = Get("vc.splash.vcServer", "VC Server");
            var opLabel = Get("vc.splash.optimalPlayers", "Optimal Players");
            var cpLabel = Get("vc.splash.currentPlayers", "Current Players");
            var loc = VoiceConfig.GetServerLocationName(VoiceServerState.VoiceServerUrl) ?? VoiceServerState.VoiceServerUrl;
            sb.AppendLine($"<size=80%>{locLabel}: <color=#58a6ff>{loc}</color></size>");
            sb.AppendLine($"<size=80%>{opLabel}: <color=#3fb950>{VoiceServerState.OptimalPlayers}</color></size>");
            sb.AppendLine($"<size=80%>{cpLabel}: <color=#d29922>{VoiceServerState.CurrentTotalPlayers}</color></size>");
        }
        else
        {
            sb.AppendLine($"<size=70%>{Get("vc.splash.madeBy", "Made by")} <color=#00ffff>FangkuaiYa</color>, <color=#00ffff>HayaiUme</color></size>");
            sb.AppendLine($"<size=70%>{Get("vc.splash.sponsorBy", "Sponsor by")} <color=#ff44ff>TAIKongguo</color></size>");
        }

        return sb.ToString();
    }

    private static Sprite? _solidSprite;

    private static Sprite CreateSolidSprite()
    {
        if (_solidSprite != null) return _solidSprite;
        var tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        _solidSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
        _solidSprite.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
        return _solidSprite;
    }

    public class SplashCoroutineRunner : MonoBehaviour { }
}
