// VoteCommands.cs - Vote command handlers

using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public static class VoteCommands
{
    private const string WHITE = ChatColors.WHITE;
    private const string ORANGE = ChatColors.ORANGE;
    private const string RED = ChatColors.RED;
    private const string BLUE = ChatColors.BLUE;
    private const string B = ChatColors.B, EB = ChatColors.EB, EC = ChatColors.EC;

    public static bool IsVoteCommand(string cmd)
    {
        return cmd == "/votemute" || cmd == "/vm" ||
               cmd == "/votepause" || cmd == "/vp" ||
               cmd == "/voteresume" || cmd == "/vr" ||
               cmd == "/votefaceoff" || cmd == "/vf" ||
               cmd == "/votesetgoals" || cmd == "/vsg" ||
               cmd == "/votesettime" || cmd == "/vst" ||
               cmd == "/votesetstate" || cmd == "/vss" ||
               cmd == "/y";
    }

    private static void SendPrivate(ChatManager ui, ulong clientId, string msg)
    {
        ChatServerPatch.SendPrivate(ui, clientId, msg);
    }

    private static void Broadcast(ChatManager ui, string msg)
    {
        ChatServerPatch.BroadcastSystem(ui, msg);
    }

    private static IEnumerator RestoreTickAfterFaceoff(GameManager gm, GamePhase originalPhase, int originalTick)
    {
        // Wait for the faceoff phase to complete naturally (game uses its own faceoff timer)
        while (gm != null && gm.GameState.Value.Phase == GamePhase.FaceOff)
        {
            yield return new WaitForSeconds(0.25f);
        }
        
        // Small delay to let the phase transition settle
        yield return new WaitForSeconds(0.1f);
        
        if (gm == null) yield break;

        // Restore the original game clock after the faceoff countdown completes.
        if (originalPhase != GamePhase.FaceOff)
            gm.Server_SetGameState(originalPhase, originalTick, null, null, null, null);
    }

    private static void FreezeAllPlayers(bool freeze)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        foreach (var cc in nm.ConnectedClientsList)
        {
            var p = cc.PlayerObject?.GetComponent<Player>();
            if (p == null) continue;
            
            ulong steamId = PlayerHelpers.GetSteamIdFromPlayer(p);
            var body = p.PlayerBody;
            if (body != null)
            {
                if (freeze)
                {
                    // Store frozen state before freezing
                    var rb = body.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        var frozenState = new PlayerMutePlugin.FrozenState
                        {
                            Position = body.transform.position,
                            Rotation = body.transform.rotation,
                            LinearVelocity = rb.linearVelocity,
                            AngularVelocity = rb.angularVelocity,
                            Team = p.Team,
                            WasHoldingPosition = p.PlayerPosition != null
                        };
                        PlayerMutePlugin.FrozenPlayers[steamId] = frozenState;
                    }
                    body.Server_Freeze();
                }
                else
                {
                    // Restore velocity when unfreezing
                    if (PlayerMutePlugin.FrozenPlayers.TryGetValue(steamId, out var state))
                    {
                        body.Server_Unfreeze();
                        var rb = body.GetComponent<Rigidbody>();
                        if (rb != null)
                        {
                            rb.linearVelocity = state.LinearVelocity;
                            rb.angularVelocity = state.AngularVelocity;
                        }
                        PlayerMutePlugin.FrozenPlayers.Remove(steamId);
                    }
                    else
                    {
                        body.Server_Unfreeze();
                    }
                }
            }
        }
        
        // If unfreezing all, clear any remaining frozen states for players who may have left
        if (!freeze)
        {
            PlayerMutePlugin.FrozenPlayers.Clear();
        }
    }

    public static bool HandleVoteCommand(ChatManager ui, Player player, ulong clientId, string cmd, string arg1, string arg2)
    {
        try
        {
            var nm = ui.NetworkManager;
            ulong senderSteam = PlayerHelpers.GetSteamIdFromPlayer(player);
            string senderName = player?.Username?.Value.ToString() ?? "(unknown)";

            // Handle /y
            if (cmd == "/y")
            {
                foreach (var kvp in PlayerMutePlugin.ActiveVotes)
                {
                    var voteData = kvp.Value;
                    if (!voteData.Voters.Contains(senderSteam))
                    {
                        return ProcessVoteYes(ui, nm, player, clientId, senderSteam, senderName, kvp.Key, voteData);
                    }
                }
                SendPrivate(ui, clientId, WHITE + "No active votes to vote on, or you've already voted." + EC);
                return false;
            }

            int activeCount = ChatServerPatch.GetActivePlayersCount(nm);
            if (activeCount < 1)
            {
                SendPrivate(ui, clientId, WHITE + "No players on Red/Blue teams to vote." + EC);
                return false;
            }

            foreach (var kvp in PlayerMutePlugin.ActiveVotes)
            {
                if (kvp.Value.Voters.Contains(senderSteam))
                {
                    SendPrivate(ui, clientId, WHITE + "You cannot start a new vote while you have an active vote. Use /y to vote yes on existing votes." + EC);
                    return false;
                }
            }

            int votesNeeded = Mathf.CeilToInt(activeCount * 0.75f);

            // /votemute
            if (cmd == "/votemute" || cmd == "/vm")
            {
                if (string.IsNullOrEmpty(arg1))
                {
                    SendPrivate(ui, clientId, WHITE + B + "Usage:" + EB + " /votemute <name|#num|steamId>" + EC);
                    return false;
                }

                if (!PlayerHelpers.ResolvePlayer(nm, arg1, out var target, out var targetClientId, out var targetSteam) || targetSteam == 0)
                {
                    SendPrivate(ui, clientId, WHITE + "Player not found." + EC);
                    return false;
                }

                if (PlayerMutePlugin.IsAdmin(targetSteam) || PlayerMutePlugin.IsMod(targetSteam))
                {
                    SendPrivate(ui, clientId, RED + "Cannot vote mute staff members." + EC);
                    return false;
                }

                string targetName = target != null && target.Username != null ? target.Username.Value.ToString() : targetSteam.ToString();
                string voteKey = "votemute " + targetSteam;

                return ProcessVote(ui, nm, player, clientId, senderSteam, senderName, voteKey, votesNeeded, 
                    "mute " + targetName, 
                    () => {
                        PlayerMutePlugin.SetMutedTimed(targetSteam, DateTime.UtcNow.AddMinutes(10));
                        Broadcast(ui, ORANGE + B + "Vote Passed" + EB + EC + WHITE + " - " + targetName + " has been muted for 10 minutes" + EC);
                    },
                    new object[] { targetSteam, targetName });
            }

            // /votepause
            if (cmd == "/votepause" || cmd == "/vp")
            {
                string voteKey = "votepause";

                return ProcessVote(ui, nm, player, clientId, senderSteam, senderName, voteKey, votesNeeded,
                    "pause the game",
                    () => {
                        var gm = GameManager.Instance;
                        if (gm != null)
                        {
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
                            Broadcast(ui, ORANGE + B + "Vote Passed" + EB + EC + WHITE + " - Game paused" + EC);
                        }
                    },
                    null);
            }

            // /voteresume
            if (cmd == "/voteresume" || cmd == "/vr")
            {
                string voteKey = "voteresume";

                return ProcessVote(ui, nm, player, clientId, senderSteam, senderName, voteKey, votesNeeded,
                    "resume the game",
                    () => {
                        var gm = GameManager.Instance;
                        if (gm != null)
                        {
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
                            Broadcast(ui, ORANGE + B + "Vote Passed" + EB + EC + WHITE + " - Game resumed" + EC);
                        }
                    },
                    null);
            }
            // /votefaceoff
            if (cmd == "/votefaceoff" || cmd == "/vf")
            {
                // Don't allow faceoff during goal/replay phases
                var gmCheck = GameManager.Instance;
                if (gmCheck != null)
                {
                    var currentPhase = gmCheck.GameState.Value.Phase;
                    if (currentPhase == GamePhase.BlueScore || currentPhase == GamePhase.RedScore || currentPhase == GamePhase.Replay)
                    {
                        SendPrivate(ui, clientId, WHITE + "Cannot vote for a faceoff during this time." + EC);
                        return false;
                    }
                }

                string voteKey = "votefaceoff";

                return ProcessVote(ui, nm, player, clientId, senderSteam, senderName, voteKey, votesNeeded,
                    "call a faceoff",
                    () => {
                        var gm = GameManager.Instance;
                        if (gm != null)
                        {
                            // Store current tick to restore after the faceoff timer completes
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

                            int faceoffTick = ChatServerPatch.GetPhaseDurationTick(GamePhase.FaceOff, 4);

                            // Set phase to FaceOff using the configured faceoff timer instead of the current period clock.
                            gm.Server_SetGameState(GamePhase.FaceOff, faceoffTick, null, null, null, null);

                            // After faceoff ends, restore the original period's remaining time
                            gm.StartCoroutine(RestoreTickAfterFaceoff(gm, originalPhase, originalTick));

                            Broadcast(ui, ORANGE + B + "Vote Passed" + EB + EC + WHITE + " - Faceoff called" + EC);
                        }
                    },
                    null);
            }
            // /votesetgoals
            if (cmd == "/votesetgoals" || cmd == "/vsg")
            {
                if (string.IsNullOrEmpty(arg1) || string.IsNullOrEmpty(arg2))
                {
                    SendPrivate(ui, clientId, WHITE + B + "Usage:" + EB + " /votesetgoals <blue|red> <amount>" + EC);
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
                    SendPrivate(ui, clientId, WHITE + "Invalid goal amount." + EC);
                    return false;
                }

                bool isBlue = team.StartsWith("b");
                string teamName = isBlue ? "BLUE" : "RED";
                string teamColor = isBlue ? BLUE : RED;
                string voteKey = "votesetgoals " + teamName + " " + goals;

                return ProcessVote(ui, nm, player, clientId, senderSteam, senderName, voteKey, votesNeeded,
                    "set " + teamName + " goals to " + goals,
                    () => {
                        var gm = GameManager.Instance;
                        if (gm != null)
                        {
                            if (isBlue)
                                gm.Server_SetGameState(null, null, null, new int?(goals), null, null);
                            else
                                gm.Server_SetGameState(null, null, null, null, new int?(goals), null);
                            
                            Broadcast(ui, ORANGE + B + "Vote Passed" + EB + EC + WHITE + " - " + teamColor + B + teamName + EB + EC + WHITE + " goals set to " + goals + EC);
                        }
                    },
                    new object[] { isBlue, goals });
            }

            // /votesettime
            if (cmd == "/votesettime" || cmd == "/vst")
            {
                if (string.IsNullOrEmpty(arg1))
                {
                    SendPrivate(ui, clientId, WHITE + B + "Usage:" + EB + " /votesettime <seconds>" + EC);
                    return false;
                }

                if (!int.TryParse(arg1, out int seconds) || seconds < 0)
                {
                    SendPrivate(ui, clientId, WHITE + "Invalid time." + EC);
                    return false;
                }

                int minutes = seconds / 60;
                int secs = seconds % 60;
                string timeStr = minutes > 0 ? $"{minutes}m {secs}s" : $"{secs}s";
                string voteKey = "votesettime " + seconds;

                return ProcessVote(ui, nm, player, clientId, senderSteam, senderName, voteKey, votesNeeded,
                    "set time to " + timeStr,
                    () => {
                        var gm = GameManager.Instance;
                        if (gm != null)
                        {
                            gm.Server_SetGameState(null, new int?(seconds), null, null, null, null);
                            Broadcast(ui, ORANGE + B + "Vote Passed" + EB + EC + WHITE + " - Time set to " + timeStr + EC);
                        }
                    },
                    new object[] { seconds });
            }

            // /votesetstate
            if (cmd == "/votesetstate" || cmd == "/vss")
            {
                if (string.IsNullOrEmpty(arg1))
                {
                    SendPrivate(ui, clientId, WHITE + B + "Usage:" + EB + " /votesetstate <period>" + EC);
                    return false;
                }

                if (!int.TryParse(arg1, out int period) || period < 0)
                {
                    SendPrivate(ui, clientId, WHITE + "Invalid period." + EC);
                    return false;
                }

                string periodStr;
                if (period == 1) periodStr = "1st period";
                else if (period == 2) periodStr = "2nd period";
                else if (period == 3) periodStr = "3rd period";
                else periodStr = $"Overtime {period - 3}";
                
                string voteKey = "votesetstate " + period;

                return ProcessVote(ui, nm, player, clientId, senderSteam, senderName, voteKey, votesNeeded,
                    "set state to " + periodStr,
                    () => {
                        var gm = GameManager.Instance;
                        if (gm != null)
                        {
                            gm.Server_SetGameState(null, null, new int?(period), null, null, null);
                            Broadcast(ui, ORANGE + B + "Vote Passed" + EB + EC + WHITE + " - Game state set to " + periodStr + EC);
                        }
                    },
                    new object[] { period });
            }

            return false;
        }
        catch (Exception e)
        {
            Debug.LogError($"[MuteMod] Error in HandleVoteCommand: {e}");
            SendPrivate(ui, clientId, RED + "An error occurred processing the vote command." + EC);
            return false;
        }
    }

    private static bool ProcessVote(ChatManager ui, NetworkManager nm, Player voter, ulong clientId, 
                                     ulong voterSteam, string voterName, string voteKey, int votesNeeded,
                                     string description, Action onSuccess, object data)
    {
        try
        {
            if (voter.Team != PlayerTeam.Red && voter.Team != PlayerTeam.Blue)
            {
                SendPrivate(ui, clientId, WHITE + "Only players on Red or Blue teams can vote." + EC);
                return false;
            }

            if (!PlayerMutePlugin.ActiveVotes.ContainsKey(voteKey))
            {
                var voteData = new PlayerMutePlugin.VoteData
                {
                    Command = description,
                    VotesNeeded = votesNeeded,
                    Timeout = 60f,
                    Data = data,
                    OnSuccess = onSuccess
                };
                voteData.Voters.Add(voterSteam);
                PlayerMutePlugin.ActiveVotes[voteKey] = voteData;

                string voterColor = voter.Team == PlayerTeam.Red ? RED : (voter.Team == PlayerTeam.Blue ? BLUE : WHITE);
                Broadcast(ui, voterColor + B + voterName + EB + EC + WHITE + " started a vote to " + description + " (1/" + votesNeeded + ")");

                if (votesNeeded <= 1)
                {
                    PlayerMutePlugin.ActiveVotes.Remove(voteKey);
                    onSuccess?.Invoke();
                }
            }
            else
            {
                var voteData = PlayerMutePlugin.ActiveVotes[voteKey];
                
                if (voteData.Voters.Contains(voterSteam))
                {
                    SendPrivate(ui, clientId, WHITE + "You have already voted for this." + EC);
                    return false;
                }

                voteData.Voters.Add(voterSteam);
                int currentVotes = voteData.Voters.Count;

                if (currentVotes >= votesNeeded)
                {
                    PlayerMutePlugin.ActiveVotes.Remove(voteKey);
                    voteData.OnSuccess?.Invoke();
                }
                else
                {
                    string voterColor = voter.Team == PlayerTeam.Red ? RED : (voter.Team == PlayerTeam.Blue ? BLUE : WHITE);
                    Broadcast(ui, voterColor + B + voterName + EB + EC + WHITE + " voted to " + description + " (" + currentVotes + "/" + votesNeeded + ")");
                }
            }

            return false;
        }
        catch (Exception e)
        {
            Debug.LogError($"[MuteMod] Error in ProcessVote: {e}");
            try { SendPrivate(ui, clientId, RED + "An error occurred processing your vote." + EC); } catch { }
            return false;
        }
    }

    private static bool ProcessVoteYes(ChatManager ui, NetworkManager nm, Player voter, ulong clientId,
                                       ulong voterSteam, string voterName, string voteKey, PlayerMutePlugin.VoteData voteData)
    {
        try
        {
            if (voter.Team != PlayerTeam.Red && voter.Team != PlayerTeam.Blue)
            {
                SendPrivate(ui, clientId, WHITE + "Only players on Red or Blue teams can vote." + EC);
                return false;
            }

            if (voteData.Voters.Contains(voterSteam))
            {
                SendPrivate(ui, clientId, WHITE + "You have already voted for this." + EC);
                return false;
            }

            voteData.Voters.Add(voterSteam);
            int currentVotes = voteData.Voters.Count;

            if (currentVotes >= voteData.VotesNeeded)
            {
                PlayerMutePlugin.ActiveVotes.Remove(voteKey);
                voteData.OnSuccess?.Invoke();
            }
            else
            {
                string voterColor = voter.Team == PlayerTeam.Red ? RED : (voter.Team == PlayerTeam.Blue ? BLUE : WHITE);
                Broadcast(ui, voterColor + B + voterName + EB + EC + WHITE + " voted to " + voteData.Command + " (" + currentVotes + "/" + voteData.VotesNeeded + ")");
            }

            return false;
        }
        catch (Exception e)
        {
            Debug.LogError($"[MuteMod] Error in ProcessVoteYes: {e}");
            try { SendPrivate(ui, clientId, RED + "An error occurred processing your vote." + EC); } catch { }
            return false;
        }
    }
}
