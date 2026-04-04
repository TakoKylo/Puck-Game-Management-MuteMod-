// MutePlugin.cs - Main plugin entry point for MuteMod
// Handles: Player muting (text/voice), admin/mod system, timed mutes

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Newtonsoft.Json;
using Unity.Netcode;
using UnityEngine;

public class PlayerMutePlugin : IPuckMod
{
    public static PlayerMutePlugin Instance { get; private set; }
    private Harmony harmony;

    public const int PlayerState = 1;
    public const int MutedState = 2;
    public static readonly Dictionary<ulong, int> PlayerStates = new Dictionary<ulong, int>();

    // Mute expirations (UTC). Missing key == permanent
    public static readonly Dictionary<ulong, DateTime> MutedUntilUtc = new Dictionary<ulong, DateTime>();

    // Vote tracking
    public class VoteData
    {
        public string Command;
        public HashSet<ulong> Voters = new HashSet<ulong>();
        public int VotesNeeded;
        public float Timeout = 60f;
        public object Data;
        public Action OnSuccess;
    }
    
    public static Dictionary<string, VoteData> ActiveVotes = new Dictionary<string, VoteData>();

    public static MuteConfig Config { get; internal set; } = new MuteConfig();

    // Runtime-only: Server admins from server_configuration.json
    public static List<ulong> ServerAdminIds = new List<ulong>();

    // Frozen state tracking - stores velocity/position/rotation for players and puck
    public class FrozenState
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;
        public PlayerTeam Team;
        public bool WasHoldingPosition; // true if they had a position claimed
    }
    
    // Key is steamId for players
    public static Dictionary<ulong, FrozenState> FrozenPlayers = new Dictionary<ulong, FrozenState>();
    // Puck frozen state (only one puck)
    public static FrozenState FrozenPuck = null;

    public PlayerMutePlugin()
    {
        Instance = this;
        harmony = new Harmony("GAFURIX.PlayerMutePlugin");
        Debug.Log("[MuteMod] PlayerMutePlugin constructed");
    }

    public bool OnEnable()
    {
        try
        {
            MuteConfigManager.LoadConfig();

            if (Config.MutedIds != null)
                foreach (var id in Config.MutedIds)
                    PlayerStates[id] = MutedState;

            harmony.PatchAll();

            var go = new GameObject("MuteExpiryChecker");
            go.AddComponent<MuteExpiryChecker>();
            UnityEngine.Object.DontDestroyOnLoad(go);
            Debug.Log("[MuteMod] Added MuteExpiryChecker component");

            // Patch VOIP handler
            var voiceMethod = AccessTools.DeclaredMethod(typeof(PlayerVoiceRecorder), "Client_VoiceDataRpc");
            if (voiceMethod != null)
            {
                harmony.Patch(voiceMethod, prefix: new HarmonyMethod(typeof(VoiceMutePatch), "Prefix"));
                Debug.Log("[MuteMod] Patched PlayerVoiceRecorder.Client_VoiceDataRpc (VOIP)");
            }
            else Debug.LogError("[MuteMod] Could not find PlayerVoiceRecorder.Client_VoiceDataRpc!");

            // Patch PlayerBody.OnNetworkPostSpawn to detect frozen player respawns
            // (Player reference is set in OnNetworkPostSpawn, not OnNetworkSpawn)
            var bodySpawnMethod = AccessTools.DeclaredMethod(typeof(PlayerBody), "OnNetworkPostSpawn");
            if (bodySpawnMethod != null)
            {
                harmony.Patch(bodySpawnMethod, postfix: new HarmonyMethod(typeof(PlayerBodySpawnPatch), "Postfix"));
                Debug.Log("[MuteMod] Patched PlayerBody.OnNetworkPostSpawn (freeze respawn detection)");
            }
            else Debug.LogError("[MuteMod] Could not find PlayerBody.OnNetworkPostSpawn!");

            Debug.Log("[MuteMod] Enabled and patched (server-only).");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("[MuteMod] Harmony patch failed: " + e);
            return false;
        }
    }

    public bool OnDisable()
    {
        try
        {
            var checker = GameObject.Find("MuteExpiryChecker");
            if (checker != null) UnityEngine.Object.Destroy(checker);

            harmony.UnpatchSelf();
            Debug.Log("[MuteMod] Disabled and unpatched.");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("[MuteMod] Harmony unpatch failed: " + e);
            return false;
        }
    }

    public static bool IsAdmin(ulong steamId)
    {
        if (steamId == 0) return false;

        // B310: Use the game's built-in AdminManager
        var serverMgr = NetworkBehaviourSingleton<ServerManager>.Instance;
        if (serverMgr?.AdminManager != null)
        {
            if (serverMgr.AdminManager.IsSteamIdAdmin(steamId.ToString()))
                return true;
        }

        // Fallback to locally loaded IDs
        return ServerAdminIds.Contains(steamId);
    }

    public static bool IsMod(ulong steamId)
    {
        return steamId != 0 && Config.ModIds.Contains(steamId);
    }

    public static bool IsLeagueStaff(ulong steamId)
    {
        return steamId != 0 && Config.LeagueStaffIds.Contains(steamId);
    }

    public static string GetRoleTitle(ulong steamId)
    {
        if (IsMod(steamId)) return "MOD";
        if (IsAdmin(steamId)) return "ADMIN";
        if (IsLeagueStaff(steamId)) return "LEAGUE STAFF";
        return "";
    }

    public static bool IsMuted(ulong steamId)
    {
        if (steamId == 0) return false;

        if (PlayerStates.TryGetValue(steamId, out int state) && state == MutedState)
        {
            if (MutedUntilUtc.TryGetValue(steamId, out var untilUtc))
            {
                if (DateTime.UtcNow >= untilUtc)
                {
                    PlayerStates.Remove(steamId);
                    MutedUntilUtc.Remove(steamId);
                    MuteConfigManager.SaveConfig();
                    return false;
                }
            }
            return true;
        }

        return false;
    }

    public static void SetMuted(ulong steamId, bool muted)
    {
        if (steamId == 0) return;

        if (muted)
        {
            PlayerStates[steamId] = MutedState;
            MutedUntilUtc.Remove(steamId); // permanent
        }
        else
        {
            PlayerStates.Remove(steamId);
            MutedUntilUtc.Remove(steamId);
        }

        MuteConfigManager.SaveConfig();
        MuteConfigManager.LoadConfig();
    }

    public static void SetMutedTimed(ulong steamId, DateTime? untilUtc)
    {
        if (steamId == 0) return;

        PlayerStates[steamId] = MutedState;
        if (untilUtc.HasValue) MutedUntilUtc[steamId] = untilUtc.Value;
        else MutedUntilUtc.Remove(steamId);

        MuteConfigManager.SaveConfig();
        MuteConfigManager.LoadConfig();
    }

    public static string GetMutedListHuman()
    {
        var list = new List<string>();
        foreach (var kv in PlayerStates)
        {
            if (kv.Value != MutedState) continue;
            var id = kv.Key;

            string name;
            string display;
            if (PlayerHelpers.TryGetNameBySteamId(id, out name) && !string.IsNullOrEmpty(name))
                display = name + " (" + id + ")";
            else
                display = id.ToString();

            string suffix;
            if (MutedUntilUtc.TryGetValue(id, out var until))
            {
                var left = until - DateTime.UtcNow;
                suffix = left <= TimeSpan.Zero ? "0s left" : TimeHelpers.FormatDuration(left) + " left";
            }
            else
            {
                suffix = "permanent";
            }

            list.Add(display + " — " + suffix);
        }

        if (list.Count == 0) return "(none)";
        return string.Join(", ", list);
    }

    public static string GetMuteLeftHuman(ulong steamId)
    {
        if (!IsMuted(steamId)) return "";
        if (MutedUntilUtc.TryGetValue(steamId, out var until))
        {
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return "0s left";
            return TimeHelpers.FormatDuration(left) + " left";
        }
        return "permanent";
    }
}
