// MuteConfig.cs - Configuration management for MuteMod

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

[Serializable]
public class MuteConfig
{
    public List<ulong> ModIds = new List<ulong>();
    public List<ulong> LeagueStaffIds = new List<ulong>();
    public HashSet<ulong> MutedIds = new HashSet<ulong>();
    public Dictionary<ulong, long> MuteUntilUnix = new Dictionary<ulong, long>();
}

[Serializable]
public class MuteServerConfig
{
    public List<string> adminSteamIds = new List<string>();
}

public static class MuteConfigManager
{
    /// <summary>
    /// Gets the Puck game root folder (where Puck.exe lives)
    /// Works whether mod is in Mods folder or Steam Workshop folder
    /// </summary>
    private static string GetPuckGameRoot()
    {
        // Application.dataPath points to Puck_Data folder
        string gameRoot = Application.dataPath;
        
        if (gameRoot.EndsWith("Puck_Data"))
        {
            gameRoot = Directory.GetParent(gameRoot).FullName;
        }
        
        return gameRoot;
    }

    /// <summary>
    /// Gets the config directory inside the Puck game folder
    /// Creates it if it doesn't exist
    /// </summary>
    private static string ConfigDir
    {
        get
        {
            string configFolder = Path.Combine(GetPuckGameRoot(), "config");
            if (!Directory.Exists(configFolder))
            {
                Directory.CreateDirectory(configFolder);
            }
            return configFolder;
        }
    }

    private static string ConfigPath => Path.Combine(ConfigDir, "muteconfig.json");

    public static void LoadConfig()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            if (!File.Exists(ConfigPath))
            {
                PlayerMutePlugin.Config = new MuteConfig();
                SaveConfig();
            }
            else
            {
                var json = File.ReadAllText(ConfigPath);
                PlayerMutePlugin.Config = JsonConvert.DeserializeObject<MuteConfig>(json) ?? new MuteConfig();
            }

            var config = PlayerMutePlugin.Config;
            if (config.ModIds == null) config.ModIds = new List<ulong>();
            if (config.LeagueStaffIds == null) config.LeagueStaffIds = new List<ulong>();
            if (config.MutedIds == null) config.MutedIds = new HashSet<ulong>();
            if (config.MuteUntilUnix == null) config.MuteUntilUnix = new Dictionary<ulong, long>();

            LoadServerAdmins();

            // Rebuild runtime state
            PlayerMutePlugin.PlayerStates.Clear();
            PlayerMutePlugin.MutedUntilUtc.Clear();

            var now = DateTime.UtcNow;
            foreach (var id in config.MutedIds)
                PlayerMutePlugin.PlayerStates[id] = PlayerMutePlugin.MutedState;

            foreach (var kv in config.MuteUntilUnix)
            {
                var id = kv.Key;
                var unix = kv.Value;
                if (unix > 0)
                {
                    var until = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
                    if (until > now)
                    {
                        PlayerMutePlugin.MutedUntilUtc[id] = until;
                        PlayerMutePlugin.PlayerStates[id] = PlayerMutePlugin.MutedState;
                    }
                    else
                    {
                        PlayerMutePlugin.PlayerStates.Remove(id);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[MuteMod] Failed to load config: " + e);
            PlayerMutePlugin.Config = new MuteConfig();
            PlayerMutePlugin.PlayerStates.Clear();
            PlayerMutePlugin.MutedUntilUtc.Clear();
        }
    }

    public static void SaveConfig()
    {
        try
        {
            var config = PlayerMutePlugin.Config;
            if (config.MutedIds == null) config.MutedIds = new HashSet<ulong>();
            if (config.MuteUntilUnix == null) config.MuteUntilUnix = new Dictionary<ulong, long>();

            config.MutedIds.Clear();
            config.MuteUntilUnix.Clear();

            var now = DateTime.UtcNow;
            foreach (var kv in PlayerMutePlugin.PlayerStates)
            {
                if (kv.Value == PlayerMutePlugin.MutedState)
                {
                    var id = kv.Key;
                    config.MutedIds.Add(id);
                    if (PlayerMutePlugin.MutedUntilUtc.TryGetValue(id, out var until) && until > now)
                    {
                        config.MuteUntilUnix[id] = new DateTimeOffset(until).ToUnixTimeSeconds();
                    }
                }
            }

            if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(config, Formatting.Indented));
            Debug.Log($"[MuteMod] Config saved to: {ConfigPath}");
        }
        catch (Exception e)
        {
            Debug.LogError("[MuteMod] Failed to save config: " + e);
        }
    }

    private static void LoadServerAdmins()
    {
        try
        {
            PlayerMutePlugin.ServerAdminIds.Clear();
            
            // Get the Puck game root folder to find server config
            string puckRoot = GetPuckGameRoot();
            
            if (!Directory.Exists(puckRoot))
            {
                Debug.LogWarning($"[MuteMod] Puck game folder not found: {puckRoot}");
                return;
            }
            
            // Search all .json files in the Puck game root folder for adminSteamIds
            var jsonFiles = Directory.GetFiles(puckRoot, "*.json", SearchOption.TopDirectoryOnly);
            foreach (var jsonFile in jsonFiles)
            {
                try
                {
                    var json = File.ReadAllText(jsonFile);
                    var serverConfig = JsonConvert.DeserializeObject<MuteServerConfig>(json);
                    
                    if (serverConfig?.adminSteamIds != null && serverConfig.adminSteamIds.Count > 0)
                    {
                        foreach (var steamIdStr in serverConfig.adminSteamIds)
                        {
                            if (ulong.TryParse(steamIdStr, out ulong steamId) && steamId != 0)
                            {
                                if (!PlayerMutePlugin.ServerAdminIds.Contains(steamId))
                                {
                                    PlayerMutePlugin.ServerAdminIds.Add(steamId);
                                }
                            }
                        }
                        Debug.Log($"[MuteMod] Loaded {PlayerMutePlugin.ServerAdminIds.Count} admin ID(s) from {Path.GetFileName(jsonFile)}");
                        return; // Found and loaded, we're done
                    }
                }
                catch { } // Skip files that aren't valid JSON or don't have the field
            }

            Debug.Log($"[MuteMod] No .json file found with adminSteamIds field in {puckRoot}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[MuteMod] Failed to load admin IDs: {e}");
        }
    }
}
