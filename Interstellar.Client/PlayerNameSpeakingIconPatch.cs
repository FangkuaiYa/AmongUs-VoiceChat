using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using VoiceChatPlugin.VoiceChat;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin;

/// <summary>
/// Displays a microphone icon (Speaking.png) to the left of the name of each
/// player who is currently speaking, and a NoConnect.png icon for players
/// who are not connected to the voice service.
/// </summary>
[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
public static class PlayerNameSpeakingIconPatch
{
    private const float SpeakingThreshold = 0.01f;
    private const string SpeakingIconName = "VC_SpeakingIcon";
    private const string NoConnectIconName = "VC_NoConnectIcon";

    /// <summary>Per-player speaking icon GameObject, keyed by PlayerId.</summary>
    private static readonly Dictionary<byte, GameObject> SpeakingIconCache = new();

    /// <summary>Per-player NoConnect icon GameObject, keyed by PlayerId.</summary>
    private static readonly Dictionary<byte, GameObject> NoConnectIconCache = new();

    /// <summary>Loaded once on first use.</summary>
    private static Sprite? _speakingSprite;
    private static Sprite? _noConnectSprite;

    static void Postfix()
    {
        var room = VoiceChatRoom.Current;
        if (room == null)
        {
            ClearAllIcons();
            return;
        }

        // ----- load sprites -----
        if (_speakingSprite == null)
            _speakingSprite = InterstellarHudState.LoadSpriteFromResources(
                "VoiceChatPlugin.Resources.Speaking.png", 100f);
        if (_noConnectSprite == null)
            _noConnectSprite = InterstellarHudState.LoadSpriteFromResources(
                "VoiceChatPlugin.Resources.NoConnect.png", 100f);

        // ----- build set of voice-connected player IDs -----
        var connectedIds = new HashSet<byte>();
        foreach (var c in room.AllClients)
            if (c.PlayerId != byte.MaxValue)
                connectedIds.Add(c.PlayerId);
        // Local player is always connected when the room exists
        if (PlayerControl.LocalPlayer != null)
            connectedIds.Add(PlayerControl.LocalPlayer.PlayerId);

        // ----- work out who is speaking -----
        var speakingIds = new HashSet<byte>();

        // Only track speaking when speaker is not muted
        if (!InterstellarHudState.IsSpeakerMuted)
        {
            foreach (var c in room.AllClients)
                if (c.PlayerId != byte.MaxValue && c.Level > SpeakingThreshold && c.IsAudible)
                    speakingIds.Add(c.PlayerId);

            // Don't show self-speaking indicator when locally muted
            if (PlayerControl.LocalPlayer != null
                && room.LocalMicLevel > SpeakingThreshold
                && !room.Mute)
                speakingIds.Add(PlayerControl.LocalPlayer.PlayerId);
        }

        // ----- update icons for all players in the game -----
        var processedIds = new HashSet<byte>();

        foreach (var pc in PlayerControl.AllPlayerControls)
        {
            if (pc == null) continue;
            byte id = pc.PlayerId;
            processedIds.Add(id);

            if (pc.cosmetics?.nameText == null) continue;

            // Don't show icon if the player's name or body is hidden from view
            // (e.g. player is off-screen, in a vent, or invisible via a mod role).
            bool nameHidden = !pc.cosmetics.nameText.gameObject.activeInHierarchy;
            float bodyAlpha = pc.cosmetics.currentBodySprite?.BodySprite?.color.a ?? 1f;
            if (nameHidden || pc.inVent || bodyAlpha < 0.01f)
            {
                RemoveSpeakingIcon(id);
                RemoveNoConnectIcon(id);
                continue;
            }

            if (!connectedIds.Contains(id))
            {
                // Player is NOT connected to voice service → show NoConnect
                RemoveSpeakingIcon(id);
                EnsureNoConnectIcon(pc, id);
            }
            else if (speakingIds.Contains(id))
            {
                // Connected and speaking → show Speaking icon
                RemoveNoConnectIcon(id);
                EnsureSpeakingIcon(pc, id);
            }
            else
            {
                // Connected but silent → remove all icons
                RemoveSpeakingIcon(id);
                RemoveNoConnectIcon(id);
            }
        }

        // ----- clean up icons for players who left the game -----
        var toRemoveSpeaking = new List<byte>();
        foreach (var kv in SpeakingIconCache)
            if (!processedIds.Contains(kv.Key))
                toRemoveSpeaking.Add(kv.Key);
        foreach (var id in toRemoveSpeaking)
            RemoveSpeakingIcon(id);

        var toRemoveNoConnect = new List<byte>();
        foreach (var kv in NoConnectIconCache)
            if (!processedIds.Contains(kv.Key))
                toRemoveNoConnect.Add(kv.Key);
        foreach (var id in toRemoveNoConnect)
            RemoveNoConnectIcon(id);
    }

    // ================================================================
    //  Speaking icon helpers
    // ================================================================

    private static void EnsureSpeakingIcon(PlayerControl pc, byte playerId)
    {
        if (_speakingSprite == null) return;

        var nameParent = pc.cosmetics.nameText.transform.parent;
        if (nameParent == null) return;

        if (SpeakingIconCache.TryGetValue(playerId, out var existing))
        {
            if (existing == null)
            {
                SpeakingIconCache.Remove(playerId);
            }
            else if (existing.transform.parent != nameParent)
            {
                // Player object changed (e.g. re-spawn) — rebuild.
                Object.Destroy(existing);
                SpeakingIconCache.Remove(playerId);
            }
            else
            {
                UpdateIconPosition(existing, pc);
                return;
            }
        }

        CreateIcon(pc, playerId, SpeakingIconName, _speakingSprite, SpeakingIconCache);
    }

    private static void RemoveSpeakingIcon(byte playerId)
    {
        if (SpeakingIconCache.TryGetValue(playerId, out var go))
        {
            if (go != null) Object.Destroy(go);
            SpeakingIconCache.Remove(playerId);
        }
    }

    // ================================================================
    //  NoConnect icon helpers
    // ================================================================

    private static void EnsureNoConnectIcon(PlayerControl pc, byte playerId)
    {
        if (_noConnectSprite == null) return;

        var nameParent = pc.cosmetics.nameText.transform.parent;
        if (nameParent == null) return;

        if (NoConnectIconCache.TryGetValue(playerId, out var existing))
        {
            if (existing == null)
            {
                NoConnectIconCache.Remove(playerId);
            }
            else if (existing.transform.parent != nameParent)
            {
                Object.Destroy(existing);
                NoConnectIconCache.Remove(playerId);
            }
            else
            {
                UpdateIconPosition(existing, pc);
                return;
            }
        }

        CreateIcon(pc, playerId, NoConnectIconName, _noConnectSprite, NoConnectIconCache);
    }

    private static void RemoveNoConnectIcon(byte playerId)
    {
        if (NoConnectIconCache.TryGetValue(playerId, out var go))
        {
            if (go != null) Object.Destroy(go);
            NoConnectIconCache.Remove(playerId);
        }
    }

    // ================================================================
    //  Shared helpers
    // ================================================================

    private static void CreateIcon(PlayerControl pc, byte playerId, string iconName,
        Sprite sprite, Dictionary<byte, GameObject> cache)
    {
        // Parent to the nameText's parent (sibling of nameText) instead of
        // nameText itself.  This prevents other mods that copy/modify nameText
        // from accidentally cloning the mic icon as well.
        var nameParent = pc.cosmetics.nameText.transform.parent;
        if (nameParent == null) return;

        var go = new GameObject(iconName);
        go.transform.SetParent(nameParent, false);
        go.transform.localScale = Vector3.one * 0.5f;

        // Use the same layer as the name text so shadows/stencil affect both identically.
        go.layer = pc.cosmetics.nameText.gameObject.layer;

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;

        // Match the name text's sorting layer and order so the rendering
        // pipeline groups the icon with the name text.
        var nameMr = pc.cosmetics.nameText.GetComponent<MeshRenderer>();
        if (nameMr != null)
        {
            sr.sortingLayerName = nameMr.sortingLayerName;
            sr.sortingLayerID   = nameMr.sortingLayerID;
            sr.sortingOrder     = nameMr.sortingOrder;
        }
        else
        {
            sr.sortingOrder = 10;
        }

        cache[playerId] = go;

        // Set initial position based on current text width.
        UpdateIconPosition(go, pc);
    }

    /// <summary>
    /// Positions the icon to the left of the name text, following the
    /// text's rendered width each frame so the icon never overlaps
    /// the name even when it changes (mod role text, long names, etc.).
    /// </summary>
    private static void UpdateIconPosition(GameObject icon, PlayerControl pc)
    {
        var nameText = pc.cosmetics.nameText;
        if (nameText == null) return;

        // Use the rendered text bounds width (local-space) to position
        // the icon just to the left of the text.
        float textHalfWidth = nameText.textBounds.size.x * 0.5f;
        // Fallback if bounds aren't ready yet (first frame after spawn, etc.)
        if (textHalfWidth < 0.01f) textHalfWidth = 0.5f;
        icon.transform.localPosition = new Vector3(-textHalfWidth - 0.25f, 0f, -0.1f);
    }

    private static void ClearAllIcons()
    {
        foreach (var kv in SpeakingIconCache)
        {
            if (kv.Value != null) Object.Destroy(kv.Value);
        }
        SpeakingIconCache.Clear();

        foreach (var kv in NoConnectIconCache)
        {
            if (kv.Value != null) Object.Destroy(kv.Value);
        }
        NoConnectIconCache.Clear();
    }

    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
    private static class HudStartCleanup
    {
        private static void Postfix() => ClearAllIcons();
    }
}
