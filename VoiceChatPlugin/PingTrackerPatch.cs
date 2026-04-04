using HarmonyLib;
using TMPro;
using System.Linq;
using System.Collections.Generic;
using VoiceChatPlugin.VoiceChat;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin;

/// <summary>
/// BCL-style speaking bar at the top of the screen.
///
/// When players are talking a row of PoolablePlayer icons (cloned from the
/// existing MeetingHud vote-area player icons) appears at the top, one icon
/// per speaking player, each tinted in that player's Palette colour.
/// Outside of meetings we fall back to coloured name-text because
/// PoolablePlayer icons require the MeetingHud prefab chain to be alive.
///
/// The original PingTracker ping/FPS text is hidden.
/// </summary>
[HarmonyPatch(typeof(PingTracker), nameof(PingTracker.Update))]
public static class PingTrackerPatch
{
    private const float SpeakingThreshold = 0.01f;

    // Root container anchored to top-centre of screen
    private static GameObject?    _barRoot;
    private static AspectPosition? _barAspect;

    // One slot per speaking player  (keyed by playerId)
    private static readonly Dictionary<byte, SpeakerSlot> _slots = new();

    // ── PingTracker.Update postfix ─────────────────────────────────────────
    static void Postfix(PingTracker __instance)
    {
        if (__instance?.text == null) return;
        __instance.text.text = string.Empty; // hide original ping text

        EnsureBar(__instance);
        if (_barRoot == null) return;

        var room = VoiceChatRoom.Current;

        // Build set of speaking playerIds
        var speakingIds = new HashSet<byte>();
        if (room != null)
        {
            foreach (var c in room.AllClients)
                if (c.PlayerId != byte.MaxValue && c.Level > SpeakingThreshold)
                    speakingIds.Add(c.PlayerId);

            byte localId = PlayerControl.LocalPlayer
                ? PlayerControl.LocalPlayer.PlayerId : byte.MaxValue;
            if (PlayerControl.LocalPlayer && room.LocalMicLevel > SpeakingThreshold
                && localId != byte.MaxValue)
                speakingIds.Add(localId);
        }

        // Remove slots for players who stopped talking
        var toRemove = new List<byte>();
        foreach (var kv in _slots)
            if (!speakingIds.Contains(kv.Key)) toRemove.Add(kv.Key);
        foreach (var id in toRemove) RemoveSlot(id);

        // Add/refresh slots for speaking players
        foreach (byte id in speakingIds)
        {
            if (!_slots.ContainsKey(id))
                AddSlot(id);
        }

        // Reposition slots in a horizontal row
        LayoutSlots();

        _barRoot.SetActive(speakingIds.Count > 0);
    }

    // ── Bar root ────────────────────────────────────────────────────────────
    private static void EnsureBar(PingTracker template)
    {
        if (_barRoot != null && _barRoot) return;

        _barRoot      = new GameObject("VC_SpeakingBar");
        _barRoot.transform.SetParent(template.transform.parent, false);

        _barAspect    = _barRoot.AddComponent<AspectPosition>();
        _barAspect.Alignment        = AspectPosition.EdgeAlignments.Top;
        _barAspect.DistanceFromEdge = new Vector3(0f, 0.25f, 0f);
        _barAspect.AdjustPosition();

        _barRoot.SetActive(false);
    }

    // ── Slot management ─────────────────────────────────────────────────────
    private static void AddSlot(byte playerId)
    {
        if (_barRoot == null) return;

        var slot = new SpeakerSlot();

        // Try to get the player's cosmetics via their PlayerControl
        PlayerControl? pc = FindPlayer(playerId);
        Color playerColor = GetPaletteColor(pc);

        // ── Icon: clone PlayerIcon from MeetingHud if available ─────────────
        bool gotIcon = false;
        if (MeetingHud.Instance != null)
        {
            foreach (var state in MeetingHud.Instance.playerStates)
            {
                if (state == null || state.TargetPlayerId != playerId) continue;
                if (state.PlayerIcon == null) break;

                // Clone the PoolablePlayer icon
                var clone = Object.Instantiate(
                    state.PlayerIcon.gameObject, _barRoot.transform);
                clone.SetActive(true);
                // Scale down to ~0.35 world-units wide (vote-area icon is ~0.75)
                clone.transform.localScale = Vector3.one * 0.45f;
                // Disable the mask so it renders normally outside the vote panel
                foreach (var sr in clone.GetComponentsInChildren<SpriteRenderer>())
                    sr.maskInteraction = SpriteMaskInteraction.None;

                slot.IconGO = clone;
                gotIcon = true;
                break;
            }
        }

        // ── Fallback: coloured circle + name text ───────────────────────────
        if (!gotIcon)
        {
            var circleGO = new GameObject("Circle");
            circleGO.transform.SetParent(_barRoot.transform, false);
            var sr = circleGO.AddComponent<SpriteRenderer>();
            sr.sprite       = CreateCircleSprite();
            sr.color        = playerColor;
            sr.sortingOrder = 10;
            circleGO.transform.localScale = Vector3.one * 0.28f;
            slot.IconGO = circleGO;
        }

        // ── Name label below the icon ────────────────────────────────────────
        string name = pc?.Data?.PlayerName ?? "?";
        var labelGO = new GameObject("Label");
        labelGO.transform.SetParent(_barRoot.transform, false);
        var tmp = labelGO.AddComponent<TextMeshPro>();
        tmp.text               = name;
        tmp.fontSize           = 1.3f;
        tmp.alignment          = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.sortingOrder       = 11;
        tmp.color              = Color.white;
        tmp.rectTransform.sizeDelta = new Vector2(1.8f, 0.6f);
        slot.LabelTMP = tmp;

        _slots[playerId] = slot;
    }

    private static void RemoveSlot(byte id)
    {
        if (_slots.TryGetValue(id, out var slot))
        {
            if (slot.IconGO  != null) Object.Destroy(slot.IconGO);
            if (slot.LabelTMP != null) Object.Destroy(slot.LabelTMP.gameObject);
            _slots.Remove(id);
        }
    }

    private static void LayoutSlots()
    {
        float slotWidth = 0.75f;
        float totalWidth = _slots.Count * slotWidth;
        float startX = -totalWidth * 0.5f + slotWidth * 0.5f;

        int i = 0;
        foreach (var kv in _slots)
        {
            float x = startX + i * slotWidth;
            if (kv.Value.IconGO != null)
                kv.Value.IconGO.transform.localPosition  = new Vector3(x, 0f, 0f);
            if (kv.Value.LabelTMP != null)
                kv.Value.LabelTMP.transform.localPosition = new Vector3(x, -0.32f, 0f);
            i++;
        }
    }

    // ── HudManager.Start: reset when scenes reload ──────────────────────────
    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
    private static class HudStartPatch
    {
        private static void Postfix()
        {
            foreach (var kv in _slots)
            {
                if (kv.Value.IconGO   != null) Object.Destroy(kv.Value.IconGO);
                if (kv.Value.LabelTMP != null) Object.Destroy(kv.Value.LabelTMP.gameObject);
            }
            _slots.Clear();

            if (_barRoot != null) { Object.Destroy(_barRoot); _barRoot = null; }
            _barAspect = null;
        }
    }

    // ── Utilities ────────────────────────────────────────────────────────────
    private static PlayerControl? FindPlayer(byte id)
    {
        foreach (var pc in PlayerControl.AllPlayerControls)
            if (pc != null && pc.PlayerId == id) return pc;
        return null;
    }

    private static Color GetPaletteColor(PlayerControl? pc)
    {
        if (pc?.Data == null) return new Color(0.18f, 0.80f, 0.44f, 1f);
        int cid = pc.Data.DefaultOutfit.ColorId;
        if (cid >= 0 && cid < Palette.PlayerColors.Length)
            return Palette.PlayerColors[cid];
        return Color.white;
    }

    // Cached 64x64 circle sprite used as fallback icon
    private static Sprite? _circleSprite;
    private static Sprite CreateCircleSprite()
    {
        if (_circleSprite != null) return _circleSprite;
        int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float r = size * 0.5f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - r + 0.5f, dy = y - r + 0.5f;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            float alpha = Mathf.Clamp01((r - dist) * 2f);
            tex.SetPixel(x, y, new Color(1, 1, 1, alpha));
        }
        tex.Apply();
        _circleSprite = Sprite.Create(
            tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        return _circleSprite;
    }

    // ── Inner type ───────────────────────────────────────────────────────────
    private class SpeakerSlot
    {
        public GameObject?   IconGO;
        public TextMeshPro?  LabelTMP;
    }
}
