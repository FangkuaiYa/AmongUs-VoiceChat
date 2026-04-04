using HarmonyLib;
using TMPro;
using VoiceChatPlugin.VoiceChat;
using UnityEngine;
using Object = UnityEngine.Object;
using System.Collections.Generic;

namespace VoiceChatPlugin;

/// <summary>
/// BCL-style meeting speaking indicator.
///
/// Instead of placing a small text label next to the player name,
/// we draw a bright green outline/glow sprite behind each speaking
/// player's PlayerVoteArea — matching BetterCrewLink's coloured
/// border approach for talking avatars.
///
/// A TMP "speaking" label is still kept as a fallback for players
/// whose NameText reference is unavailable.
/// </summary>
[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
public static class MeetingSpeakingIndicatorPatch
{
    private const float SpeakingThreshold = 0.01f;

    // Green glow colour (BCL uses the player's colour; we use a universal green)
    private static readonly Color GlowColor  = new(0.18f, 1.00f, 0.38f, 0.85f);
    private static readonly Color GlowColorLocal = new(0.38f, 0.85f, 1.00f, 0.85f); // cyan for self

    // Per-player glow renderers
    private static readonly Dictionary<byte, SpriteRenderer> Glows = new();
    // Per-player TMP fallback labels
    private static readonly Dictionary<byte, TextMeshPro>    Labels = new();

    public static void Postfix(MeetingHud __instance)
    {
        if (__instance.playerStates == null) { HideAll(); return; }

        var room = VoiceChatRoom.Current;
        if (room == null) { HideAll(); return; }

        // Collect speaking player IDs
        HashSet<byte> speaking = new();
        foreach (var c in room.AllClients)
            if (c.PlayerId != byte.MaxValue && c.Level > SpeakingThreshold)
                speaking.Add(c.PlayerId);

        byte localId = PlayerControl.LocalPlayer ? PlayerControl.LocalPlayer.PlayerId : byte.MaxValue;
        if (PlayerControl.LocalPlayer && room.LocalMicLevel > SpeakingThreshold
            && localId != byte.MaxValue)
            speaking.Add(localId);

        // Update each vote card
        HashSet<byte> alive = new();
        foreach (var state in __instance.playerStates)
        {
            if (state == null) continue;
            alive.Add(state.TargetPlayerId);

            bool isSpeaking = speaking.Contains(state.TargetPlayerId);
            bool isLocal    = state.TargetPlayerId == localId;
            Color glowCol   = isLocal ? GlowColorLocal : GlowColor;

            UpdateGlow(state,  isSpeaking, glowCol);
            UpdateLabel(state, isSpeaking);
        }

        CleanStale(alive);
    }

    // ── Destroy all indicators when meeting ends ──────────────────────────────
    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.OnDestroy))]
    private static class DestroyPatch
    {
        private static void Postfix()
        {
            foreach (var sr in Glows.Values)
                if (sr != null) Object.Destroy(sr.gameObject);
            Glows.Clear();

            foreach (var v in Labels.Values)
                if (v != null) Object.Destroy(v.gameObject);
            Labels.Clear();
        }
    }

    // ── Glow outline (BCL-style border) ───────────────────────────────────────
    private static void UpdateGlow(PlayerVoteArea state, bool active, Color colour)
    {
        var sr = GetOrCreateGlow(state);
        if (sr == null) return;
        sr.color = active ? colour : Color.clear;
        sr.gameObject.SetActive(true);
    }

    private static SpriteRenderer? GetOrCreateGlow(PlayerVoteArea state)
    {
        if (Glows.TryGetValue(state.TargetPlayerId, out var existing) && existing != null)
            return existing;

        // Create a slightly oversized white quad behind the card to act as a glowing border
        var bg = state.transform.Find("Background") ?? state.transform.Find("Bg") ?? state.transform;

        var go = new GameObject("VC_GlowBorder");
        go.transform.SetParent(state.transform, false);
        go.transform.localPosition = new Vector3(0f, 0f, 1f); // behind the card content

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite       = CreateGlowSprite();
        sr.color        = Color.clear;
        sr.sortingOrder = -1; // behind other card sprites

        // Scale to cover the card with a small border margin
        go.transform.localScale = new Vector3(2.92f, 0.58f, 1f);

        Glows[state.TargetPlayerId] = sr;
        return sr;
    }

    // Rounded-rect sprite approximated with a plain white texture
    // (Among Us UI doesn't provide a sprite builder, so we use a solid quad
    //  and let the colour alpha create a subtle glow effect)
    private static Sprite? _glowSprite;
    private static Sprite CreateGlowSprite()
    {
        if (_glowSprite != null) return _glowSprite;
        var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        var pixels = new Color[16];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.white;
        tex.SetPixels(pixels);
        tex.Apply();
        tex.wrapMode = TextureWrapMode.Clamp;
        _glowSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 100f);
        return _glowSprite;
    }

    // ── TMP label fallback ────────────────────────────────────────────────────
    private static void UpdateLabel(PlayerVoteArea state, bool active)
    {
        var tmp = GetOrCreateLabel(state);
        if (tmp == null) return;
        tmp.gameObject.SetActive(active);
    }

    private static TextMeshPro? GetOrCreateLabel(PlayerVoteArea state)
    {
        if (Labels.TryGetValue(state.TargetPlayerId, out var ex) && ex != null)
            return ex;

        var template = state.NameText;
        TextMeshPro tmp;

        if (template == null)
        {
            var go = new GameObject("VC_SpeakingLabel");
            go.transform.SetParent(state.transform, false);
            tmp = go.AddComponent<TextMeshPro>();
            tmp.fontSize  = 1.8f;
            tmp.color     = GlowColor;
            tmp.alignment = TextAlignmentOptions.Center;
            go.transform.localPosition = new Vector3(-0.52f, 0.21f, -1f);
        }
        else
        {
            var go = Object.Instantiate(template.gameObject, state.transform);
            tmp = go.GetComponent<TextMeshPro>();
            tmp.name               = "VC_SpeakingLabel";
            tmp.color              = GlowColor;
            tmp.alignment          = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.fontSize           = template.fontSize * 0.85f;
            tmp.transform.localPosition = new Vector3(-0.52f, 0.21f, -1f);
            tmp.transform.localScale    = template.transform.localScale * 0.8f;
        }

        tmp.text = $"🎙 {VoiceChatLocalization.Tr("speaking")}";
        tmp.gameObject.SetActive(false);
        Labels[state.TargetPlayerId] = tmp;
        return tmp;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static void HideAll()
    {
        foreach (var sr in Glows.Values)
            if (sr != null) sr.color = Color.clear;
        foreach (var v in Labels.Values)
            if (v != null) v.gameObject.SetActive(false);
    }

    private static void CleanStale(HashSet<byte> alive)
    {
        List<byte> remove = new();
        foreach (var kv in Glows)
        {
            if (alive.Contains(kv.Key)) continue;
            if (kv.Value != null) Object.Destroy(kv.Value.gameObject);
            remove.Add(kv.Key);
        }
        foreach (var k in remove) { Glows.Remove(k); Labels.Remove(k); }

        remove.Clear();
        foreach (var kv in Labels)
        {
            if (alive.Contains(kv.Key)) continue;
            if (kv.Value != null) Object.Destroy(kv.Value.gameObject);
            remove.Add(kv.Key);
        }
        foreach (var k in remove) Labels.Remove(k);
    }
}
