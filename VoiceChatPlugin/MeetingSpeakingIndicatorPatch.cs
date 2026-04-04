using HarmonyLib;
using VoiceChatPlugin.VoiceChat;
using UnityEngine;
using Object = UnityEngine.Object;
using System.Collections.Generic;

namespace VoiceChatPlugin;

/// <summary>
/// BCL-style meeting speaking indicator.
///
/// When a player is talking, we light up their vote card's HighlightedFX
/// sprite using that player's actual Palette colour — exactly how BCL draws
/// a coloured border around talking avatars in the meeting screen.
///
/// No TMP text labels are used; the glow IS the indicator.
/// </summary>
[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
public static class MeetingSpeakingIndicatorPatch
{
    private const float SpeakingThreshold = 0.01f;

    // Cache original HighlightedFX colours so we can restore them
    private static readonly Dictionary<byte, Color> OriginalGlowColors = new();

    public static void Postfix(MeetingHud __instance)
    {
        if (__instance.playerStates == null) return;

        var room = VoiceChatRoom.Current;

        // Build speaking set
        var speaking = new HashSet<byte>();
        if (room != null)
        {
            foreach (var c in room.AllClients)
                if (c.PlayerId != byte.MaxValue && c.Level > SpeakingThreshold)
                    speaking.Add(c.PlayerId);

            byte localId = PlayerControl.LocalPlayer
                ? PlayerControl.LocalPlayer.PlayerId : byte.MaxValue;
            if (PlayerControl.LocalPlayer && room.LocalMicLevel > SpeakingThreshold
                && localId != byte.MaxValue)
                speaking.Add(localId);
        }

        foreach (var state in __instance.playerStates)
        {
            if (state == null || !state.HighlightedFX) continue;

            bool isSpeaking = speaking.Contains(state.TargetPlayerId);

            if (isSpeaking)
            {
                // Resolve this player's body colour from Palette
                Color glowColor = GetPlayerColor(state.TargetPlayerId);

                // Cache original colour on first encounter
                if (!OriginalGlowColors.ContainsKey(state.TargetPlayerId))
                    OriginalGlowColors[state.TargetPlayerId] = state.HighlightedFX.color;

                // Recolour and enable the existing HighlightedFX sprite
                state.HighlightedFX.color   = glowColor;
                state.HighlightedFX.enabled = true;
            }
            else
            {
                // Restore original colour and hide
                if (OriginalGlowColors.TryGetValue(state.TargetPlayerId, out var orig))
                    state.HighlightedFX.color = orig;
                state.HighlightedFX.enabled = false;
            }
        }
    }

    // Destroy patches: clean up colour cache when meeting ends
    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.OnDestroy))]
    private static class DestroyPatch
    {
        private static void Postfix() => OriginalGlowColors.Clear();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the Palette.PlayerColors colour for the given playerId.
    /// Falls back to green if the player or colour-id cannot be resolved.
    /// </summary>
    private static Color GetPlayerColor(byte playerId)
    {
        foreach (var pc in PlayerControl.AllPlayerControls)
        {
            if (pc == null || pc.Data == null) continue;
            if (pc.PlayerId != playerId) continue;

            int colorId = pc.Data.DefaultOutfit.ColorId;
            if (colorId >= 0 && colorId < Palette.PlayerColors.Length)
                return Palette.PlayerColors[colorId]; // Color32 implicitly casts to Color
        }
        // Fallback: BCL uses #2ecc71 for unknown players
        return new Color(0.18f, 0.80f, 0.44f, 1f);
    }
}
