// MuteExpiryChecker.cs - Handles mute expiration and vote timeout

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

public class MuteExpiryChecker : MonoBehaviour
{
    private float nextCheckTime = 0f;

    void Update()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

        ChatServerPatch.FlushPendingChats();

        if (Time.realtimeSinceStartup < nextCheckTime) return;
        nextCheckTime = Time.realtimeSinceStartup + 10f;

        // Check expired votes
        var expiredVotes = new List<string>();
        foreach (var kv in PlayerMutePlugin.ActiveVotes)
        {
            kv.Value.Timeout -= 10f;
            if (kv.Value.Timeout <= 0f)
                expiredVotes.Add(kv.Key);
        }
        
        foreach (var voteKey in expiredVotes)
        {
            PlayerMutePlugin.ActiveVotes.Remove(voteKey);
            var ui = UnityEngine.Object.FindFirstObjectByType<ChatManager>();
            if (ui != null)
            {
                ChatServerPatch.BroadcastSystem(ui, "<color=#FF9500FF><b>Vote</b></color> <color=#FFFFFF>" + voteKey + " has expired.</color>");
            }
        }

        // Check expired mutes
        var expired = new List<ulong>();
        foreach (var kv in PlayerMutePlugin.MutedUntilUtc)
        {
            if (DateTime.UtcNow >= kv.Value)
                expired.Add(kv.Key);
        }

        foreach (var steamId in expired)
        {
            try
            {
                PlayerMutePlugin.SetMuted(steamId, false);

                string name;
                if (!PlayerHelpers.TryGetNameBySteamId(steamId, out name))
                    name = steamId.ToString();

                var ui = UnityEngine.Object.FindFirstObjectByType<ChatManager>();
                if (ui != null)
                {
                    ChatServerPatch.BroadcastSystem(
                        ui,
                        "<color=#FF9500FF><b>SYSTEM</b></color> " +
                        "<color=#FFFFFF>has unmuted " + name + "</color>");

                    foreach (var cc in NetworkManager.Singleton.ConnectedClientsList)
                    {
                        var p = cc.PlayerObject?.GetComponent<Player>();
                        if (PlayerHelpers.GetSteamIdFromPlayer(p) == steamId)
                        {
                            try
                            {
                                ChatServerPatch.SendPrivate(
                                    ui,
                                    cc.ClientId,
                                    "<color=#FF0000>Your mute has expired. You are now unmuted.</color>");
                            }
                            catch (Exception e)
                            {
                                Debug.LogError("[MuteMod] Expiry private message failed: " + e);
                            }
                        }
                    }
                }

                Debug.Log($"[MuteMod] Automatically unmuted {steamId} (timer expired)");
            }
            catch (Exception e)
            {
                Debug.LogError($"[MuteMod] Auto-unmute error for {steamId}: {e}");
            }
        }
    }
}
