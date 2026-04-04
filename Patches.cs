// Patches.cs - Harmony patches for MuteMod

using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

public static class VoiceMutePatch
{
    private static DateTime _nextReloadUtc = DateTime.MinValue;

    public static bool Prefix(PlayerVoiceRecorder __instance, byte[] voiceData)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now >= _nextReloadUtc)
            {
                MuteConfigManager.LoadConfig();
                _nextReloadUtc = now.AddSeconds(1);
            }

            var pl = __instance.GetComponent<Player>();
            ulong steam = PlayerHelpers.GetSteamIdFromPlayer(pl);
            string uname = pl?.Username?.Value.ToString() ?? "(unknown)";

            bool muted =
                PlayerMutePlugin.IsMuted(steam) ||
                (PlayerMutePlugin.Config?.MutedIds != null &&
                 PlayerMutePlugin.Config.MutedIds.Contains(steam));

            if (muted)
            {
                Debug.Log($"[MuteMod] BLOCKED VOICE from muted {uname} ({steam})");
                return false;
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[MuteMod] Error in VOIP patch: " + e);
        }
        return true;
    }
}

// Patch to detect when a frozen player's body spawns and re-freeze them at their original position
public static class PlayerBodySpawnPatch
{
    public static void Postfix(PlayerBody __instance)
    {
        try
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            
            var player = __instance.Player;
            if (player == null)
            {
                Debug.Log($"[MuteMod] PlayerBodySpawnPatch: Player is null on body");
                return;
            }
            
            ulong steamId = PlayerHelpers.GetSteamIdFromPlayer(player);
            Debug.Log($"[MuteMod] PlayerBodySpawnPatch: Body spawned for steamId {steamId}, frozen count: {PlayerMutePlugin.FrozenPlayers.Count}");
            
            if (steamId == 0) return;
            
            // Check if this player was frozen
            if (PlayerMutePlugin.FrozenPlayers.TryGetValue(steamId, out var frozenState))
            {
                Debug.Log($"[MuteMod] Player {steamId} was frozen, starting re-freeze coroutine");
                // Start a coroutine to teleport and re-freeze after a short delay (let spawn complete)
                __instance.StartCoroutine(RefreezePlayerCoroutine(__instance, steamId, frozenState));
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[MuteMod] Error in PlayerBodySpawnPatch: {e}");
        }
    }
    
    private static IEnumerator RefreezePlayerCoroutine(PlayerBody body, ulong steamId, PlayerMutePlugin.FrozenState state)
    {
        // Wait a couple frames for the body to fully initialize
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();
        
        if (body == null) yield break;
        
        // Use the game's built-in teleport method which handles networking properly
        try { body.Server_Teleport(state.Position, state.Rotation); } catch { }
        
        Debug.Log($"[MuteMod] Teleported player {steamId} to position {state.Position}");
        
        // Wait one more frame after teleporting
        yield return new WaitForFixedUpdate();
        
        if (body == null) yield break;
        
        // Now freeze the player at the teleported position
        try { body.Server_Freeze(); } catch { }
        
        // Notify the player
        try
        {
            var ui = UnityEngine.Object.FindFirstObjectByType<ChatManager>();
            if (ui != null)
            {
                var player = body.Player;
                if (player != null)
                {
                    var clientId = player.OwnerClientId;
                    ui.Server_SendChatMessageToClients(
                        "<color=#FF9500FF><b>SYSTEM</b></color> <color=#FFFFFF>You are still frozen. Returning to frozen position.</color>",
                        new ulong[] { clientId }
                    );
                }
            }
        }
        catch { }
        
        Debug.Log($"[MuteMod] Re-froze player {steamId} at their original position after respawn");
    }
}
