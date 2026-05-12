// AdminCommands.cs - Admin and mod command handlers

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

[HarmonyPatch(typeof(ChatManager), "Client_SendChatMessageRpc")]
public static class ChatServerPatch
{
    private struct PendingChatSend
    {
        public ulong[] ClientIds;
        public ChatMessage Message;
    }

    internal const string WHITE = ChatColors.WHITE;
    internal const string ORANGE = ChatColors.ORANGE;
    internal const string RED = ChatColors.RED;
    internal const string BLUE = ChatColors.BLUE;
    internal const string GREY = ChatColors.GREY;
    internal const string PINK = ChatColors.PINK;
    internal const string GREEN = ChatColors.GREEN;
    internal const string B = ChatColors.B, EB = ChatColors.EB, EC = ChatColors.EC;
    private static readonly Queue<PendingChatSend> PendingChatSends = new Queue<PendingChatSend>();

    internal static void SendPrivate(ChatManager ui, ulong clientId, string msg)
    {
        EnqueueSystemChat(new ulong[] { clientId }, msg);
    }

    internal static void BroadcastSystem(ChatManager ui, string msg)
    {
        var ids = GetConnectedClientIds();
        EnqueueSystemChat(ids, msg);
    }

    private static void Broadcast(ChatManager ui, string msg)
    {
        BroadcastSystem(ui, msg);
    }

    internal static ulong[] GetConnectedClientIds()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return Array.Empty<ulong>();

        var ids = new ulong[nm.ConnectedClientsList.Count];
        for (int index = 0; index < nm.ConnectedClientsList.Count; index++)
            ids[index] = nm.ConnectedClientsList[index].ClientId;
        return ids;
    }

    internal static ChatMessage CreateSystemChatMessage(string msg)
    {
        return new ChatMessage
        {
            Username = null,
            Content = new FixedString512Bytes(msg ?? string.Empty),
            Timestamp = Time.realtimeSinceStartupAsDouble,
            IsQuickChat = false,
            IsTeamChat = false,
            IsSystem = true,
            SteamID = null,
            Team = null,
        };
    }

    internal static void EnqueueSystemChat(ulong[] clientIds, string msg)
    {
        if (clientIds == null || clientIds.Length == 0) return;

        lock (PendingChatSends)
        {
            PendingChatSends.Enqueue(new PendingChatSend
            {
                ClientIds = clientIds,
                Message = CreateSystemChatMessage(msg),
            });
        }
    }

    internal static void FlushPendingChats()
    {
        ChatManager ui = NetworkBehaviourSingleton<ChatManager>.Instance;
        if (ui == null)
            ui = UnityEngine.Object.FindFirstObjectByType<ChatManager>();
        if (ui == null) return;

        while (true)
        {
            PendingChatSend pending;
            lock (PendingChatSends)
            {
                if (PendingChatSends.Count == 0) break;
                pending = PendingChatSends.Dequeue();
            }

            if (pending.ClientIds != null && pending.ClientIds.Length > 0)
                ui.Server_SendChatMessageToClients(pending.Message, pending.ClientIds);
        }
    }

    internal static int GetPhaseDurationTick(GamePhase phase, int fallbackTick)
    {
        try
        {
            var gameModeManager = GameModeManager.Instance;
            if (gameModeManager == null) return fallbackTick;

            var selectedGameModeField = typeof(GameModeManager).GetField("selectedGameMode", BindingFlags.Instance | BindingFlags.NonPublic);
            var selectedGameMode = selectedGameModeField?.GetValue(gameModeManager);
            if (selectedGameMode == null) return fallbackTick;

            var configProperty = selectedGameMode.GetType().GetProperty("Config", BindingFlags.Instance | BindingFlags.Public);
            var config = configProperty?.GetValue(selectedGameMode);
            if (config == null) return fallbackTick;

            var mapProperty = config.GetType().GetProperty("phaseDurationMap", BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            var phaseDurationMap = mapProperty?.GetValue(config) as System.Collections.IDictionary;
            if (phaseDurationMap == null || !phaseDurationMap.Contains(phase)) return fallbackTick;

            return Convert.ToInt32(phaseDurationMap[phase]);
        }
        catch
        {
            return fallbackTick;
        }
    }

    public static ulong GetSteamIdFromPlayer(Player p) => PlayerHelpers.GetSteamIdFromPlayer(p);
    public static bool TryGetNameBySteamId(ulong steamId, out string name) => PlayerHelpers.TryGetNameBySteamId(steamId, out name);

    private static void FreezeAllPlayers(bool freeze)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        foreach (var cc in nm.ConnectedClientsList)
        {
            var p = cc.PlayerObject?.GetComponent<Player>();
            if (p == null) continue;
            TryFreezePlayer(p, freeze);
        }
        
        // If unfreezing all, clear any remaining frozen states for players who may have left
        if (!freeze)
        {
            PlayerMutePlugin.FrozenPlayers.Clear();
        }
    }

    private static object FindPlayerBodyComponent(Player p)
    {
        if (p == null) return null;

        var prop = p.GetType().GetProperty("PlayerBody", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop != null)
        {
            var body = prop.GetValue(p, null);
            if (body != null) return body;
        }

        foreach (var comp in p.GetComponentsInChildren<Component>(true))
        {
            var tn = comp.GetType().Name;
            if (tn == "PlayerBody" || tn.Contains("PlayerBody"))
                return comp;
        }

        return null;
    }

    private static bool TryCallFreezeOnBody(object body, bool freeze)
    {
        if (body == null) return false;
        var t = body.GetType();
        var mi = t.GetMethod(freeze ? "Server_Freeze" : "Server_Unfreeze",
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (mi == null) return false;
        mi.Invoke(body, null);
        return true;
    }

    private static bool TryFreezePlayer(Player p, bool freeze)
    {
        if (!p) return false;
        
        ulong steamId = GetSteamIdFromPlayer(p);
        var body = FindPlayerBodyComponent(p);
        
        if (freeze)
        {
            // Store frozen state before freezing
            var rb = p.GetComponentInChildren<Rigidbody>();
            if (rb != null)
            {
                var frozenState = new PlayerMutePlugin.FrozenState
                {
                    Position = rb.position,
                    Rotation = rb.rotation,
                    LinearVelocity = rb.linearVelocity,
                    AngularVelocity = rb.angularVelocity,
                    Team = p.Team,
                    WasHoldingPosition = p.PlayerPosition != null
                };
                PlayerMutePlugin.FrozenPlayers[steamId] = frozenState;
            }
            
            if (TryCallFreezeOnBody(body, true))
                return true;
            
            if (rb != null)
            {
                rb.constraints = RigidbodyConstraints.FreezeAll;
                return true;
            }
        }
        else
        {
            // Unfreeze and restore velocity
            if (TryCallFreezeOnBody(body, false))
            {
                // Restore velocity after unfreezing
                if (PlayerMutePlugin.FrozenPlayers.TryGetValue(steamId, out var state))
                {
                    var rb = p.GetComponentInChildren<Rigidbody>();
                    if (rb != null)
                    {
                        rb.linearVelocity = state.LinearVelocity;
                        rb.angularVelocity = state.AngularVelocity;
                    }
                    PlayerMutePlugin.FrozenPlayers.Remove(steamId);
                }
                return true;
            }
            
            var rbFallback = p.GetComponentInChildren<Rigidbody>();
            if (rbFallback != null)
            {
                rbFallback.constraints = RigidbodyConstraints.None;
                
                // Restore velocity
                if (PlayerMutePlugin.FrozenPlayers.TryGetValue(steamId, out var state))
                {
                    rbFallback.linearVelocity = state.LinearVelocity;
                    rbFallback.angularVelocity = state.AngularVelocity;
                    PlayerMutePlugin.FrozenPlayers.Remove(steamId);
                }
                return true;
            }
        }

        return false;
    }

    [HarmonyPrefix]
    public static bool Prefix(ChatManager __instance, string content, bool isQuickChat, bool isTeamChat, RpcParams rpcParams)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;
        try
        {
            if (!__instance.NetworkManager.IsServer) return true;
            if (string.IsNullOrEmpty(content)) return true;

            // B310: Resolve Player from RPC sender's client ID
            Player player = null;
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client))
            {
                player = client.PlayerObject?.GetComponent<Player>();
            }
            if (player == null) return true;

            string message = content;
            ulong senderSteam = GetSteamIdFromPlayer(player);

            // Muted chat block
            if (message[0] != '/')
            {
                if (PlayerMutePlugin.IsMuted(senderSteam))
                {
                    SendPrivate(__instance, clientId, RED + B + "You are muted and cannot send messages." + EB + EC);
                    return false;
                }
                return true;
            }

            string[] parts = message.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts[0].ToLowerInvariant();
            string arg1 = parts.Length > 1 ? parts[1] : null;
            string arg2 = parts.Length > 2 ? parts[2] : null;

            // Check for vote commands
            if (VoteCommands.IsVoteCommand(cmd))
            {
                return VoteCommands.HandleVoteCommand(__instance, player, clientId, cmd, arg1, arg2);
            }

            // Check for whisper
            if (cmd == "/whisper" || cmd == "/w")
            {
                return HandleWhisperCommand(__instance, player, clientId, senderSteam, arg1, arg2);
            }

            // Check for admin commands
            bool adminCmd = cmd == "/mute" || cmd == "/m" || cmd == "/unmute" || cmd == "/um" ||
                cmd == "/muteid" || cmd == "/unmuteid" ||
                cmd == "/muted" || cmd == "/admin" || cmd == "/whoami" ||
                cmd == "/changeteam" || cmd == "/ct" || cmd == "/swap" ||
                cmd == "/freeze" || cmd == "/fp" || cmd == "/f" || cmd == "/unfreeze" || cmd == "/ufp" || cmd == "/uf" ||
                cmd == "/freezeall" || cmd == "/fa" || cmd == "/unfreezeall" || cmd == "/ufa" ||
                cmd == "/kick" || cmd == "/k" || cmd == "/kicksteamid" ||
                cmd == "/slap" || cmd == "/jump" || cmd == "/j" ||
                cmd == "/pauseall" || cmd == "/resumeall" ||
                cmd == "/faceoff" || cmd == "/fo" ||
                cmd == "/settime" || cmd == "/st" ||
                cmd == "/setgoals" || cmd == "/sg" ||
                cmd == "/setstate" || cmd == "/ss" ||
                cmd == "/warmup" || cmd == "/start" ||
                cmd == "/pause" || cmd == "/resume";

            if (!adminCmd) return true;

            return HandleAdminCommand(__instance, player, clientId, senderSteam, cmd, arg1, arg2);
        }
        catch (Exception e)
        {
            Debug.LogError($"[MuteMod] Error in chat command handler: {e}");
            try { SendPrivate(__instance, clientId, RED + "An error occurred processing your command." + EC); } catch { }
            return false;
        }
    }

    private static bool HandleWhisperCommand(ChatManager ui, Player player, ulong clientId, ulong senderSteam, string arg1, string arg2)
    {
        if (PlayerMutePlugin.IsMuted(senderSteam))
        {
            SendPrivate(ui, clientId, RED + B + "You are muted and cannot whisper." + EB + EC);
            return false;
        }
        if (string.IsNullOrEmpty(arg1) || string.IsNullOrEmpty(arg2))
        {
            SendPrivate(ui, clientId, WHITE + "Usage: /whisper <name|#num|steamId> <message>");
            return false;
        }

        if (!PlayerHelpers.ResolvePlayer(ui.NetworkManager, arg1, out var target, out ulong targetId, out var targetSteam) || target == null)
        {
            SendPrivate(ui, clientId, WHITE + "Player not found.");
            return false;
        }

        string senderName = player?.Username?.Value.ToString() ?? "(unknown)";
        string targetName = target?.Username?.Value.ToString() ?? targetSteam.ToString();
        string senderColor = PlayerHelpers.TeamColor(player);
        string targetColor = PlayerHelpers.TeamColor(target);

        SendPrivate(ui, targetId,
            "Whisper from " + senderColor + B + senderName + EB + EC +
            WHITE + ": " + PINK + arg2 + EC);

        SendPrivate(ui, clientId,
            "You whispered to " + targetColor + B + targetName + EB + EC +
            WHITE + ": " + PINK + arg2 + EC);

        return false;
    }

    private static bool HandleAdminCommand(ChatManager ui, Player player, ulong clientId, ulong senderSteam, string cmd, string arg1, string arg2)
    {
        bool isAdmin = PlayerMutePlugin.IsAdmin(senderSteam);
        bool isMod = PlayerMutePlugin.IsMod(senderSteam);
        bool isLeagueStaff = PlayerMutePlugin.IsLeagueStaff(senderSteam);

        // /whoami is available to everyone
        if (cmd == "/whoami")
        {
            if (!string.IsNullOrEmpty(arg1) && isAdmin)
            {
                if (PlayerHelpers.ResolvePlayer(ui.NetworkManager, arg1, out var t, out var tId, out var tSteam) && tSteam != 0)
                {
                    bool tMuted = PlayerMutePlugin.IsMuted(tSteam);
                    string tl = tMuted ? PlayerMutePlugin.GetMuteLeftHuman(tSteam) : "";
                    string tRole = PlayerMutePlugin.GetRoleTitle(tSteam);
                    if (string.IsNullOrEmpty(tRole)) tRole = "PLAYER";
                    SendPrivate(ui, clientId, WHITE + B + (t?.Username?.Value.ToString() ?? tSteam.ToString()) + ": SteamId=" + tSteam +
                        "  Role=" + tRole +
                        "  Muted=" + tMuted + (tMuted ? " (" + tl + ")" : "") +
                        "  Team=" + (t != null ? t.Team.ToString() : "(offline)") + EB + EC);
                }
                else
                {
                    SendPrivate(ui, clientId, WHITE + "Player not found." + EC);
                }
                return false;
            }

            bool sMuted = PlayerMutePlugin.IsMuted(senderSteam);
            string sTl = sMuted ? PlayerMutePlugin.GetMuteLeftHuman(senderSteam) : "";
            string sRole = PlayerMutePlugin.GetRoleTitle(senderSteam);
            if (string.IsNullOrEmpty(sRole)) sRole = "PLAYER";
            SendPrivate(ui, clientId, WHITE + B + player.Username.Value + EB + ": SteamId=" + senderSteam +
                "  Role=" + sRole +
                "  Muted=" + sMuted + (sMuted ? " (" + sTl + ")" : "") +
                "  Team=" + player.Team + EC);
            return false;
        }

        // /muted is available to everyone
        if (cmd == "/muted")
        {
            SendPrivate(ui, clientId, WHITE + B + "Muted:" + EB + " " + PlayerMutePlugin.GetMutedListHuman() + EC);
            return false;
        }

        // /admin is available to everyone
        if (cmd == "/admin")
        {
            string helpText;
            if (isMod && !isAdmin)
            {
                helpText = WHITE + B + "Mod Commands:" +
                    "\n/kick <name|#num>" +
                    "\n/kicksteamid <steamid>" +
                    "\n/mute <player> [time]" +
                    "\n/unmute <player>" +
                    "\n/muted" +
                    "\n/whoami [player]" +
                    "\n/changeteam <player> <team>" +
                    "\n/swap <player> <player>" +
                    "\n/freeze puck | <player>" +
                    "\n/unfreeze puck | <player>" +
                    "\n/freezeall" +
                    "\n/unfreezeall" +
                    "\n/pauseall" +
                    "\n/resumeall" +
                    "\n/slap <player>" +
                    "\n/settime <seconds>" +
                    "\n/setgoals <blue|red> <amount>" +
                    "\n/setstate <period>" + EB + EC;
            }
            else if (isLeagueStaff && !isAdmin && !isMod)
            {
                helpText = WHITE + B + "League Staff Commands:" +
                    "\n/freeze puck | <player>" +
                    "\n/unfreeze puck | <player>" +
                    "\n/freezeall" +
                    "\n/unfreezeall" +
                    "\n/pauseall" +
                    "\n/resumeall" +
                    "\n/settime <seconds>" +
                    "\n/setgoals <blue|red> <amount>" +
                    "\n/setstate <period>" + EB + EC;
            }
            else
            {
                helpText = WHITE + B + "Admin Commands:" +
                    "\n/mute <player> [time]" +
                    "\n/unmute <player>" +
                    "\n/muted" +
                    "\n/whoami [player]" +
                    "\n/changeteam <player> <team>" +
                    "\n/swap <player> <player>" +
                    "\n/freeze puck | <player>" +
                    "\n/unfreeze puck | <player>" +
                    "\n/freezeall" +
                    "\n/unfreezeall" +
                    "\n/pauseall" +
                    "\n/resumeall" +
                    "\n/warmup [seconds]" +
                    "\n/start" +
                    "\n/pause" +
                    "\n/resume" +
                    "\n/faceoff" +
                    "\n/kicksteamid <steamid>" +
                    "\n/slap <player>" +
                    "\n/jump <player>" +
                    "\n/settime <seconds>" +
                    "\n/setgoals <blue|red> <amount>" +
                    "\n/setstate <period>" + EB + EC;
            }
            
            SendPrivate(ui, clientId, helpText);
            return false;
        }

        // League staff freeze commands
        bool isLeagueCmd = cmd == "/freeze" || cmd == "/fp" || cmd == "/f" ||
                           cmd == "/unfreeze" || cmd == "/ufp" || cmd == "/uf" ||
                           cmd == "/freezeall" || cmd == "/fa" ||
                           cmd == "/unfreezeall" || cmd == "/ufa" ||
                           cmd == "/pauseall" || cmd == "/resumeall" ||
                           cmd == "/faceoff" || cmd == "/fo" ||
                           cmd == "/settime" || cmd == "/st" ||
                           cmd == "/setgoals" || cmd == "/sg" ||
                           cmd == "/setstate" || cmd == "/ss";

        // Permission checks
        if (!isAdmin && !isMod && !isLeagueStaff)
        {
            SendPrivate(ui, clientId, WHITE + B + "You don't have permission." + EB + EC);
            return false;
        }
        
        if (!isAdmin && !isMod && isLeagueStaff && !isLeagueCmd)
        {
            SendPrivate(ui, clientId, WHITE + B + "League staff can only use freeze and league management commands." + EB + EC);
            return false;
        }

        // Mod-only kick by name
        if (cmd == "/kick" || cmd == "/k")
        {
            if (isMod && !isAdmin)
            {
                return HandleModKick(ui, clientId, senderSteam, arg1);
            }
            if (isAdmin)
            {
                return true; // Let base game handle admin kick
            }
            return true;
        }

        // Handle freeze commands
        if (cmd == "/freeze" || cmd == "/fp" || cmd == "/f" || cmd == "/unfreeze" || cmd == "/ufp" || cmd == "/uf")
        {
            return HandleFreezeCommand(ui, clientId, senderSteam, cmd, arg1);
        }

        if (cmd == "/freezeall" || cmd == "/fa" || cmd == "/unfreezeall" || cmd == "/ufa")
        {
            bool un = (cmd == "/unfreezeall" || cmd == "/ufa");
            FreezeAllPlayers(!un);
            Broadcast(ui, ORANGE + B + PlayerMutePlugin.GetRoleTitle(senderSteam) + EB + EC + WHITE + " has " + (un ? "unfrozen" : "frozen") + " all players");
            return false;
        }

        // Mute commands
        if (cmd == "/mute" || cmd == "/m" || cmd == "/unmute" || cmd == "/um" || cmd == "/muteid" || cmd == "/unmuteid")
        {
            return HandleMuteCommand(ui, clientId, senderSteam, cmd, arg1, arg2);
        }

        // Change team
        if (cmd == "/changeteam" || cmd == "/ct")
        {
            return HandleChangeTeam(ui, clientId, senderSteam, arg1, arg2);
        }

        // Swap
        if (cmd == "/swap")
        {
            return HandleSwap(ui, clientId, senderSteam, arg1, arg2);
        }

        // Kick by steamid
        if (cmd == "/kicksteamid")
        {
            return HandleKickSteamId(ui, clientId, senderSteam, arg1);
        }

        // Slap
        if (cmd == "/slap")
        {
            return HandleSlap(ui, clientId, senderSteam, arg1);
        }

        // Jump
        if (cmd == "/jump" || cmd == "/j")
        {
            return HandleJump(ui, clientId, senderSteam, arg1);
        }

        // Pause/Resume
        if (cmd == "/pauseall")
        {
            return HandlePauseAll(ui, clientId, senderSteam, isAdmin, isMod);
        }

        if (cmd == "/resumeall")
        {
            return HandleResumeAll(ui, clientId, senderSteam, isAdmin, isMod);
        }

        // Set time/goals/state
        if (cmd == "/settime" || cmd == "/st")
        {
            return HandleSetTime(ui, clientId, senderSteam, arg1, isAdmin, isMod);
        }

        if (cmd == "/setgoals" || cmd == "/sg")
        {
            return HandleSetGoals(ui, clientId, senderSteam, arg1, arg2, isAdmin, isMod);
        }

        if (cmd == "/setstate" || cmd == "/ss")
        {
            return HandleSetState(ui, clientId, senderSteam, arg1, isAdmin, isMod);
        }

        // Faceoff
        if (cmd == "/faceoff" || cmd == "/fo")
        {
            return HandleFaceoff(ui, clientId, senderSteam, isAdmin, isMod);
        }

        // Admin-only game flow commands
        if (cmd == "/warmup" || cmd == "/start" || cmd == "/pause" || cmd == "/resume")
        {
            if (!isAdmin)
            {
                SendPrivate(ui, clientId, WHITE + B + "Admin only." + EB + EC);
                return false;
            }

            if (cmd == "/warmup") return HandleWarmup(ui, clientId, senderSteam, arg1);
            if (cmd == "/start") return HandleStart(ui, clientId, senderSteam);
            if (cmd == "/pause") return HandlePauseTimer(ui, clientId, senderSteam);
            if (cmd == "/resume") return HandleResumeTimer(ui, clientId, senderSteam);
        }

        return true;
    }

    private static bool HandleWarmup(ChatManager ui, ulong clientId, ulong senderSteam, string arg1)
    {
        var gm = GameManager.Instance;
        if (gm == null)
        {
            SendPrivate(ui, clientId, WHITE + "GameManager not found." + EC);
            return false;
        }

        int warmupTick = GetPhaseDurationTick(GamePhase.Warmup, 30);
        if (!string.IsNullOrEmpty(arg1) && int.TryParse(arg1, out int customSeconds) && customSeconds > 0)
            warmupTick = customSeconds;

        gm.Server_SetGameState(GamePhase.Warmup, warmupTick, new int?(1), new int?(0), new int?(0), new bool?(false));
        gm.Server_StartTicking();

        Broadcast(ui, ORANGE + B + "ADMIN" + EB + EC + WHITE + " has started warmup (" + warmupTick + "s)");
        return false;
    }

    private static bool HandleStart(ChatManager ui, ulong clientId, ulong senderSteam)
    {
        var gm = GameManager.Instance;
        if (gm == null)
        {
            SendPrivate(ui, clientId, WHITE + "GameManager not found." + EC);
            return false;
        }

        int faceoffTick = GetPhaseDurationTick(GamePhase.FaceOff, 4);
        int playTick = GetPhaseDurationTick(GamePhase.Play, 300);

        // Reset period/scores/overtime so this is a true game start, then go to FaceOff.
        gm.Server_SetGameState(GamePhase.FaceOff, faceoffTick, new int?(1), new int?(0), new int?(0), new bool?(false));
        gm.Server_StartTicking();

        // StandardGameMode.OnFaceOffEnded uses its protected `tickRemainder` field to seed the
        // Play phase, but that field is only populated by OnPreGameStarted (which we skipped).
        // Without this, Play would start with a stale tick (usually 0) — overwrite after FaceOff ends.
        gm.StartCoroutine(SetPlayTickAfterFaceoff(gm, playTick));

        Broadcast(ui, ORANGE + B + "ADMIN" + EB + EC + WHITE + " has started the game");
        return false;
    }

    private static System.Collections.IEnumerator SetPlayTickAfterFaceoff(GameManager gm, int playTick)
    {
        while (gm != null && gm.GameState.Value.Phase == GamePhase.FaceOff)
        {
            yield return new WaitForSeconds(0.25f);
        }
        yield return new WaitForSeconds(0.1f);
        if (gm == null) yield break;
        if (gm.GameState.Value.Phase == GamePhase.Play)
        {
            gm.Server_SetGameState(null, new int?(playTick), null, null, null, null);
        }
    }

    private static bool HandlePauseTimer(ChatManager ui, ulong clientId, ulong senderSteam)
    {
        var gm = GameManager.Instance;
        if (gm == null)
        {
            SendPrivate(ui, clientId, WHITE + "GameManager not found." + EC);
            return false;
        }

        gm.Server_StopTicking();
        Broadcast(ui, ORANGE + B + "ADMIN" + EB + EC + WHITE + " has paused the timer");
        return false;
    }

    private static bool HandleResumeTimer(ChatManager ui, ulong clientId, ulong senderSteam)
    {
        var gm = GameManager.Instance;
        if (gm == null)
        {
            SendPrivate(ui, clientId, WHITE + "GameManager not found." + EC);
            return false;
        }

        gm.Server_StartTicking();
        Broadcast(ui, ORANGE + B + "ADMIN" + EB + EC + WHITE + " has resumed the timer");
        return false;
    }

    private static bool HandleModKick(ChatManager ui, ulong clientId, ulong senderSteam, string arg1)
    {
        if (string.IsNullOrEmpty(arg1))
        {
            SendPrivate(ui, clientId, WHITE + B + "Usage:" + EB + " /kick <name|#num>" + EC);
            return false;
        }

        if (!PlayerHelpers.ResolvePlayer(ui.NetworkManager, arg1, out var target, out var targetClientId, out var targetSteam) || target == null)
        {
            SendPrivate(ui, clientId, WHITE + "Player not found or not online." + EC);
            return false;
        }

        string targetName = target.Username != null ? target.Username.Value.ToString() : targetSteam.ToString();

        if (ui.NetworkManager.IsHost && targetClientId == 0)
        {
            SendPrivate(ui, clientId, RED + "Cannot kick the server host." + EC);
            return false;
        }

        try
        {
            ui.NetworkManager.DisconnectClient(targetClientId);
            Broadcast(ui, ORANGE + B + "MOD" + EB + EC + WHITE + " has kicked " + targetName);
        }
        catch (Exception e)
        {
            Debug.LogError($"[MuteMod] Failed to kick {targetName}: {e}");
            SendPrivate(ui, clientId, RED + "Failed to kick player." + EC);
        }

        return false;
    }

    private static bool HandleFreezeCommand(ChatManager ui, ulong clientId, ulong senderSteam, string cmd, string arg1)
    {
        bool un = (cmd == "/unfreeze" || cmd == "/ufp" || cmd == "/uf");

        if (!string.IsNullOrEmpty(arg1) && arg1.Equals("puck", StringComparison.OrdinalIgnoreCase))
        {
            var puck = UnityEngine.Object.FindFirstObjectByType<Puck>();
            if (puck == null)
            {
                SendPrivate(ui, clientId, WHITE + "Puck not found." + EC);
                return false;
            }

            if (un)
            {
                puck.Server_Unfreeze();
                // Restore puck velocity
                if (PlayerMutePlugin.FrozenPuck != null)
                {
                    var rb = puck.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.linearVelocity = PlayerMutePlugin.FrozenPuck.LinearVelocity;
                        rb.angularVelocity = PlayerMutePlugin.FrozenPuck.AngularVelocity;
                    }
                    PlayerMutePlugin.FrozenPuck = null;
                }
            }
            else
            {
                // Store puck velocity before freezing
                var rb = puck.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    PlayerMutePlugin.FrozenPuck = new PlayerMutePlugin.FrozenState
                    {
                        Position = rb.position,
                        Rotation = rb.rotation,
                        LinearVelocity = rb.linearVelocity,
                        AngularVelocity = rb.angularVelocity
                    };
                }
                puck.Server_Freeze();
            }

            Broadcast(ui, ORANGE + B + PlayerMutePlugin.GetRoleTitle(senderSteam) + EB + EC + WHITE + " has " + (un ? "unfrozen" : "frozen") + " the puck");
            return false;
        }

        if (string.IsNullOrEmpty(arg1))
        {
            SendPrivate(ui, clientId, WHITE + "Usage: /freeze <name|#num|steamId>  |  /freeze puck" + EC);
            return false;
        }

        if (!PlayerHelpers.ResolvePlayer(ui.NetworkManager, arg1, out var tgt, out var tgtClientId, out var tgtSteam) || tgtSteam == 0)
        {
            SendPrivate(ui, clientId, WHITE + "Player not found." + EC);
            return false;
        }

        string tName = (tgt != null && tgt.Username != null) ? tgt.Username.Value.ToString() : tgtSteam.ToString();

        if (!TryFreezePlayer(tgt, !un))
        {
            SendPrivate(ui, clientId, WHITE + "Cannot " + (un ? "unfreeze" : "freeze") + " this player." + EC);
            return false;
        }

        Broadcast(ui, ORANGE + B + PlayerMutePlugin.GetRoleTitle(senderSteam) + EB + EC + WHITE + " has " + (un ? "unfrozen" : "frozen") + " " + tName);
        return false;
    }

    private static bool HandleMuteCommand(ChatManager ui, ulong clientId, ulong senderSteam, string cmd, string arg1, string arg2)
    {
        if (string.IsNullOrEmpty(arg1))
        {
            SendPrivate(ui, clientId, WHITE + B + "Usage:" + EB + " /mute <name|#num|num|steamId> [time]\nExamples: /mute 62 1h, /mute name 15m" + EC);
            return false;
        }

        ulong targetSteam = 0; ulong targetClientId = 0; Player target = null;

        if (cmd == "/muteid" || cmd == "/unmuteid")
        {
            if (!ulong.TryParse(arg1, out targetSteam) || targetSteam == 0)
            {
                SendPrivate(ui, clientId, WHITE + "Invalid steamId." + EC);
                return false;
            }
        }
        else
        {
            if (!PlayerHelpers.ResolvePlayer(ui.NetworkManager, arg1, out target, out targetClientId, out targetSteam) || targetSteam == 0)
            {
                SendPrivate(ui, clientId, WHITE + B + "Player not found." + EB + EC);
                return false;
            }
        }

        bool doMute = cmd == "/mute" || cmd == "/m" || cmd == "/muteid";
        string targetName = target != null && target.Username != null ? target.Username.Value.ToString() : targetSteam.ToString();

        if (doMute)
        {
            TimeSpan dur;
            if (!string.IsNullOrEmpty(arg2) && TimeHelpers.TryParseDuration(arg2, out dur))
            {
                var until = DateTime.UtcNow.Add(dur);
                PlayerMutePlugin.SetMutedTimed(targetSteam, until);
                Broadcast(ui, ORANGE + B + PlayerMutePlugin.GetRoleTitle(senderSteam) + EB + EC + WHITE + " has muted " + targetName + " for " + TimeHelpers.FormatDuration(dur));
                if (targetClientId != 0) SendPrivate(ui, targetClientId, RED + B + "You have been muted for " + TimeHelpers.FormatDuration(dur) + "." + EB + EC);
            }
            else
            {
                PlayerMutePlugin.SetMuted(targetSteam, true);
                Broadcast(ui, ORANGE + B + PlayerMutePlugin.GetRoleTitle(senderSteam) + EB + EC + WHITE + " has muted " + targetName);
                if (targetClientId != 0) SendPrivate(ui, targetClientId, RED + B + "You have been muted." + EB + EC);
            }
        }
        else
        {
            PlayerMutePlugin.SetMuted(targetSteam, false);
            Broadcast(ui, ORANGE + B + PlayerMutePlugin.GetRoleTitle(senderSteam) + EB + EC + WHITE + " has unmuted " + targetName);
            if (targetClientId != 0) SendPrivate(ui, targetClientId, WHITE + B + "You have been unmuted." + EB + EC);
        }
        return false;
    }

    private static bool HandleChangeTeam(ChatManager ui, ulong clientId, ulong senderSteam, string arg1, string arg2)
    {
        if (string.IsNullOrEmpty(arg1) || string.IsNullOrEmpty(arg2))
        {
            SendPrivate(ui, clientId, WHITE + B + "Usage:" + EB + " /ct <name|#num|num> <team>" + EC);
            return false;
        }

        if (!PlayerHelpers.ResolvePlayer(ui.NetworkManager, arg1, out var target, out var tId, out _))
        {
            SendPrivate(ui, clientId, WHITE + "Player not found." + EC); 
            return false;
        }

        string team = arg2.ToLowerInvariant();
        PlayerTeam newTeam = PlayerTeam.Spectator;
        string teamColor = GREY, teamPretty = "Spectator";
        if (team.StartsWith("r")) { newTeam = PlayerTeam.Red; teamColor = RED; teamPretty = "RED"; }
        else if (team.StartsWith("b")) { newTeam = PlayerTeam.Blue; teamColor = BLUE; teamPretty = "BLUE"; }

        target.Server_SetGameState(team: newTeam);

        Broadcast(ui, ORANGE + B + PlayerMutePlugin.GetRoleTitle(senderSteam) + EB + EC + WHITE + " has changed " + target.Username.Value +
            " to " + teamColor + B + teamPretty + EB + EC);
        return false;
    }

    private static bool HandleSwap(ChatManager ui, ulong clientId, ulong senderSteam, string arg1, string arg2)
    {
        if (string.IsNullOrEmpty(arg1) || string.IsNullOrEmpty(arg2))
        {
            SendPrivate(ui, clientId, WHITE + "Usage: /swap <name|#num|num> <name|#num|num>");
            return false;
        }

        if (!PlayerHelpers.ResolvePlayer(ui.NetworkManager, arg1, out var t1, out _, out _) ||
            !PlayerHelpers.ResolvePlayer(ui.NetworkManager, arg2, out var t2, out _, out _))
        {
            SendPrivate(ui, clientId, WHITE + "Player not found.");
            return false;
        }

        var team1 = t1.Team;
        var team2 = t2.Team;
        t1.Server_SetGameState(team: team2);
        t2.Server_SetGameState(team: team1);

        string n1 = t1.Username.Value.ToString(), n2 = t2.Username.Value.ToString();
        string c1 = PlayerHelpers.TeamColor(t1), c2 = PlayerHelpers.TeamColor(t2);
        Broadcast(ui, ORANGE + B + PlayerMutePlugin.GetRoleTitle(senderSteam) + EB + EC +
            WHITE + " has swapped " + c1 + B + n1 + EB + EC +
            WHITE + " and " + c2 + B + n2 + EB + EC);
        return false;
    }

    private static bool HandleKickSteamId(ChatManager ui, ulong clientId, ulong senderSteam, string arg1)
    {
        if (string.IsNullOrEmpty(arg1))
        {
            SendPrivate(ui, clientId, WHITE + B + "Usage:" + EB + " /kicksteamid <steamId>" + EC);
            return false;
        }

        if (!ulong.TryParse(arg1, out var targetSteam) || targetSteam == 0)
        {
            SendPrivate(ui, clientId, WHITE + "Invalid Steam ID." + EC);
            return false;
        }

        Player target = null;
        ulong targetClientId = ulong.MaxValue;
        
        foreach (var cc in ui.NetworkManager.ConnectedClientsList)
        {
            var p = cc.PlayerObject?.GetComponent<Player>();
            if (GetSteamIdFromPlayer(p) == targetSteam)
            {
                target = p;
                targetClientId = cc.ClientId;
                break;
            }
        }

        if (target == null || targetClientId == ulong.MaxValue)
        {
            SendPrivate(ui, clientId, WHITE + "Player with that Steam ID is not online." + EC);
            return false;
        }

        string targetName = target.Username != null ? target.Username.Value.ToString() : targetSteam.ToString();

        if (ui.NetworkManager.IsHost && targetClientId == 0)
        {
            SendPrivate(ui, clientId, RED + "Cannot kick the server host." + EC);
            return false;
        }

        try
        {
            ui.NetworkManager.DisconnectClient(targetClientId);
            Broadcast(ui, ORANGE + B + PlayerMutePlugin.GetRoleTitle(senderSteam) + EB + EC + WHITE + " has kicked " + targetName);
        }
        catch (Exception e)
        {
            Debug.LogError($"[MuteMod] Failed to kick {targetName}: {e}");
            SendPrivate(ui, clientId, RED + "Failed to kick player." + EC);
        }

        return false;
    }

    private static bool HandleSlap(ChatManager ui, ulong clientId, ulong senderSteam, string arg1)
    {
        if (string.IsNullOrEmpty(arg1))
        {
            SendPrivate(ui, clientId, WHITE + B + "Usage:" + EB + " /slap <name|#num|steamId>" + EC);
            return false;
        }

        if (!PlayerHelpers.ResolvePlayer(ui.NetworkManager, arg1, out var target, out var targetClientId, out var targetSteam) || target == null)
        {
            SendPrivate(ui, clientId, WHITE + "Player not found or not online." + EC);
            return false;
        }

        string targetName = target.Username != null ? target.Username.Value.ToString() : targetSteam.ToString();

        var rigidbodies = target.GetComponentsInChildren<Rigidbody>();
        if (rigidbodies != null && rigidbodies.Length > 0)
        {
            foreach (var rb in rigidbodies)
            {
                if (rb != null)
                {
                    Vector3 randomTorque = new Vector3(
                        UnityEngine.Random.Range(-60f, 60f),
                        UnityEngine.Random.Range(-60f, 60f),
                        UnityEngine.Random.Range(-60f, 60f)
                    );
                    rb.AddTorque(randomTorque, ForceMode.Impulse);
                    rb.AddForce(Vector3.up * UnityEngine.Random.Range(30f, 60f), ForceMode.Impulse);
                }
            }

            Broadcast(ui, ORANGE + B + PlayerMutePlugin.GetRoleTitle(senderSteam) + EB + EC + WHITE + " has slapped " + targetName + "!");
        }
        else
        {
            SendPrivate(ui, clientId, WHITE + "Could not slap player (no rigidbodies found)." + EC);
        }

        return false;
    }

    private static bool HandleJump(ChatManager ui, ulong clientId, ulong senderSteam, string arg1)
    {
        if (string.IsNullOrEmpty(arg1))
        {
            SendPrivate(ui, clientId, WHITE + B + "Usage:" + EB + " /jump <name|#num|steamId>" + EC);
            return false;
        }

        if (!PlayerHelpers.ResolvePlayer(ui.NetworkManager, arg1, out var target, out var targetClientId, out var targetSteam))
        {
            SendPrivate(ui, clientId, WHITE + "Player not found." + EC);
            return false;
        }

        string targetName = target.Username != null ? target.Username.Value.ToString() : targetSteam.ToString();

        var rigidbodies = target.GetComponentsInChildren<Rigidbody>();
        bool jumped = false;
        
        if (rigidbodies != null && rigidbodies.Length > 0)
        {
            foreach (var rb in rigidbodies)
            {
                if (rb != null)
                {
                    rb.AddForce(Vector3.up * 1000f, ForceMode.Impulse);
                    jumped = true;
                }
            }

            if (jumped)
            {
                Broadcast(ui, ORANGE + B + PlayerMutePlugin.GetRoleTitle(senderSteam) + EB + EC + WHITE + " has launched " + targetName + " into the air!");
            }
            else
            {
                SendPrivate(ui, clientId, WHITE + "Could not launch player (no rigidbodies found)." + EC);
            }
        }
        else
        {
            SendPrivate(ui, clientId, WHITE + "Could not launch player (no rigidbodies found)." + EC);
        }

        return false;
    }

    private static bool HandlePauseAll(ChatManager ui, ulong clientId, ulong senderSteam, bool isAdmin, bool isMod)
    {
        var gm = GameManager.Instance;
        if (gm == null)
        {
            SendPrivate(ui, clientId, WHITE + "GameManager not found." + EC);
            return false;
        }

        // B310: Server_Pause removed - freeze players/puck manually
        FreezeAllPlayers(true);
        
        // Stop the game timer
        gm.Server_StopTicking();
        
        // Freeze puck with velocity storage
        var puck = UnityEngine.Object.FindFirstObjectByType<Puck>();
        if (puck != null)
        {
            var rb = puck.GetComponent<Rigidbody>();
            if (rb != null)
            {
                PlayerMutePlugin.FrozenPuck = new PlayerMutePlugin.FrozenState
                {
                    Position = rb.position,
                    Rotation = rb.rotation,
                    LinearVelocity = rb.linearVelocity,
                    AngularVelocity = rb.angularVelocity
                };
            }
            puck.Server_Freeze();
        }
        
        string staffType = isAdmin ? "ADMIN" : (isMod ? "MOD" : "LEAGUE STAFF");
        Broadcast(ui, ORANGE + B + staffType + EB + EC + WHITE + " has paused the game (players, puck, and timer frozen)");
        return false;
    }

    private static bool HandleResumeAll(ChatManager ui, ulong clientId, ulong senderSteam, bool isAdmin, bool isMod)
    {
        var gm = GameManager.Instance;
        if (gm == null)
        {
            SendPrivate(ui, clientId, WHITE + "GameManager not found." + EC);
            return false;
        }

        // B310: Server_Resume removed - unfreeze players/puck manually
        FreezeAllPlayers(false);
        
        // Resume the game timer
        gm.Server_StartTicking();
        
        // Unfreeze puck with velocity restoration
        var puck = UnityEngine.Object.FindFirstObjectByType<Puck>();
        if (puck != null)
        {
            puck.Server_Unfreeze();
            if (PlayerMutePlugin.FrozenPuck != null)
            {
                var rb = puck.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.linearVelocity = PlayerMutePlugin.FrozenPuck.LinearVelocity;
                    rb.angularVelocity = PlayerMutePlugin.FrozenPuck.AngularVelocity;
                }
                PlayerMutePlugin.FrozenPuck = null;
            }
        }
        
        string staffType = isAdmin ? "ADMIN" : (isMod ? "MOD" : "LEAGUE STAFF");
        Broadcast(ui, ORANGE + B + staffType + EB + EC + WHITE + " has resumed the game (players, puck, and timer unfrozen)");
        return false;
    }

    private static bool HandleSetTime(ChatManager ui, ulong clientId, ulong senderSteam, string arg1, bool isAdmin, bool isMod)
    {
        if (string.IsNullOrEmpty(arg1))
        {
            SendPrivate(ui, clientId, WHITE + B + "Usage:" + EB + " /settime <seconds> (e.g., /st 300 for 5 minutes)" + EC);
            return false;
        }

        if (!int.TryParse(arg1, out int seconds) || seconds < 0)
        {
            SendPrivate(ui, clientId, WHITE + "Invalid time. Must be a positive number of seconds." + EC);
            return false;
        }

        var gm = GameManager.Instance;
        if (gm == null)
        {
            SendPrivate(ui, clientId, WHITE + "GameManager not found." + EC);
            return false;
        }

        gm.Server_SetGameState(null, new int?(seconds), null, null, null, null);
        
        int minutes = seconds / 60;
        int secs = seconds % 60;
        string timeStr = minutes > 0 ? $"{minutes}m {secs}s" : $"{secs}s";
        
        string staffType = isAdmin ? "ADMIN" : (isMod ? "MOD" : "LEAGUE STAFF");
        Broadcast(ui, ORANGE + B + staffType + EB + EC + WHITE + " has set the game time to " + timeStr);
        return false;
    }

    private static bool HandleSetGoals(ChatManager ui, ulong clientId, ulong senderSteam, string arg1, string arg2, bool isAdmin, bool isMod)
    {
        if (string.IsNullOrEmpty(arg1) || string.IsNullOrEmpty(arg2))
        {
            SendPrivate(ui, clientId, WHITE + B + "Usage:" + EB + " /setgoals <blue|red> <amount> (e.g., /sg blue 3)" + EC);
            return false;
        }

        string team = arg1.ToLowerInvariant();
        if (!team.StartsWith("b") && !team.StartsWith("r"))
        {
            SendPrivate(ui, clientId, WHITE + "Team must be 'blue' or 'red'." + EC);
            return false;
        }

        if (!int.TryParse(arg2, out int goals) || goals < 0)
        {
            SendPrivate(ui, clientId, WHITE + "Invalid goal amount. Must be a positive number." + EC);
            return false;
        }

        var gm = GameManager.Instance;
        if (gm == null)
        {
            SendPrivate(ui, clientId, WHITE + "GameManager not found." + EC);
            return false;
        }

        bool isBlue = team.StartsWith("b");
        if (isBlue)
            gm.Server_SetGameState(null, null, null, new int?(goals), null, null);
        else
            gm.Server_SetGameState(null, null, null, null, new int?(goals), null);
        
        string teamColor = isBlue ? BLUE : RED;
        string teamName = isBlue ? "BLUE" : "RED";
        string staffType = isAdmin ? "ADMIN" : (isMod ? "MOD" : "LEAGUE STAFF");
        
        Broadcast(ui, ORANGE + B + staffType + EB + EC + WHITE + " has set " + teamColor + B + teamName + EB + EC + WHITE + " team goals to " + goals);
        return false;
    }

    private static bool HandleSetState(ChatManager ui, ulong clientId, ulong senderSteam, string arg1, bool isAdmin, bool isMod)
    {
        if (string.IsNullOrEmpty(arg1))
        {
            SendPrivate(ui, clientId, WHITE + B + "Usage:" + EB + " /setstate <period> (e.g., /ss 1 for 1st period, /ss 4 for OT)" + EC);
            return false;
        }

        if (!int.TryParse(arg1, out int period) || period < 0)
        {
            SendPrivate(ui, clientId, WHITE + "Invalid period. Must be a positive number (1=1st, 2=2nd, 3=3rd, 4+=OT)." + EC);
            return false;
        }

        var gm = GameManager.Instance;
        if (gm == null)
        {
            SendPrivate(ui, clientId, WHITE + "GameManager not found." + EC);
            return false;
        }

        gm.Server_SetGameState(null, null, new int?(period), null, null, null);
        
        string periodStr;
        if (period == 1) periodStr = "1st period";
        else if (period == 2) periodStr = "2nd period";
        else if (period == 3) periodStr = "3rd period";
        else periodStr = $"Overtime {period - 3}";
        
        string staffType = isAdmin ? "ADMIN" : (isMod ? "MOD" : "LEAGUE STAFF");
        Broadcast(ui, ORANGE + B + staffType + EB + EC + WHITE + " has set the game state to " + periodStr);
        return false;
    }

    private static bool HandleFaceoff(ChatManager ui, ulong clientId, ulong senderSteam, bool isAdmin, bool isMod)
    {
        var gm = GameManager.Instance;
        if (gm == null)
        {
            SendPrivate(ui, clientId, WHITE + "GameManager not found." + EC);
            return false;
        }

        // Don't allow faceoff during goal/replay phases
        var currentPhase = gm.GameState.Value.Phase;
        if (currentPhase == GamePhase.BlueScore || currentPhase == GamePhase.RedScore || currentPhase == GamePhase.Replay || currentPhase == GamePhase.FaceOff || currentPhase == GamePhase.Intermission || currentPhase == GamePhase.GameOver)
        {
            SendPrivate(ui, clientId, WHITE + "Cannot call a faceoff during this time." + EC);
            return false;
        }

        // Store the current tick so we can restore it after the faceoff timer completes
        GamePhase originalPhase = gm.GameState.Value.Phase;
        int originalTick = gm.GameState.Value.Tick;

        // Unfreeze everything if currently paused
        FreezeAllPlayers(false);
        var puck = UnityEngine.Object.FindFirstObjectByType<Puck>();
        if (puck != null)
        {
            puck.Server_Unfreeze();
            PlayerMutePlugin.FrozenPuck = null;
        }

        int faceoffTick = GetPhaseDurationTick(GamePhase.FaceOff, 4);

        // Set phase to FaceOff using the configured faceoff timer instead of the current period clock.
        gm.Server_SetGameState(GamePhase.FaceOff, faceoffTick, null, null, null, null);

        // After faceoff ends, restore the original period's remaining time
        gm.StartCoroutine(RestoreTickAfterFaceoff(gm, originalPhase, originalTick));
        
        string staffType = isAdmin ? "ADMIN" : (isMod ? "MOD" : "LEAGUE STAFF");
        Broadcast(ui, ORANGE + B + staffType + EB + EC + WHITE + " has called a faceoff");
        return false;
    }

    private static System.Collections.IEnumerator RestoreTickAfterFaceoff(GameManager gm, GamePhase originalPhase, int originalTick)
    {
        // Wait for the faceoff phase to complete naturally (game uses its own faceoff timer)
        while (gm != null && gm.GameState.Value.Phase == GamePhase.FaceOff)
        {
            yield return new WaitForSeconds(0.25f);
        }
        
        // Small delay to let the phase transition settle
        yield return new WaitForSeconds(0.1f);
        
        if (gm == null) yield break;

        // Restore the tick (remaining period time) that was active before the faceoff
        // If we were in Play phase, the game auto-transitions to Play after faceoff, just fix the tick
        // If we were in a non-Play phase (like Warmup), restore that phase too
        if (originalPhase != GamePhase.Play && originalPhase != GamePhase.FaceOff)
        {
            gm.Server_SetGameState(originalPhase, originalTick, null, null, null, null);
        }
        else
        {
            gm.Server_SetGameState(null, originalTick, null, null, null, null);
        }
    }

    // Helper for getting player count
    public static int GetActivePlayersCount(NetworkManager nm)
    {
        if (nm == null) return 0;
        int count = 0;
        foreach (var cc in nm.ConnectedClientsList)
        {
            var p = cc.PlayerObject?.GetComponent<Player>();
            // Only count players on Red/Blue who are holding a position (not N/A)
            if (p != null && (p.Team == PlayerTeam.Red || p.Team == PlayerTeam.Blue) && p.PlayerPosition != null)
                count++;
        }
        return count;
    }
}
