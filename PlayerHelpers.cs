// PlayerHelpers.cs - Utility functions for player operations

using System;
using Unity.Netcode;
using UnityEngine;

public static class PlayerHelpers
{
    public static ulong GetSteamIdFromPlayer(Player p)
    {
        try
        {
            if (p?.SteamId != null)
            {
                if (ulong.TryParse(p.SteamId.Value.ToString(), out ulong val))
                    return val;
            }
            return 0UL;
        }
        catch { return 0UL; }
    }

    public static bool TryGetNameBySteamId(ulong steamId, out string name)
    {
        name = null;
        if (NetworkManager.Singleton == null) return false;
        
        foreach (var cc in NetworkManager.Singleton.ConnectedClientsList)
        {
            var p = cc.PlayerObject?.GetComponent<Player>();
            if (GetSteamIdFromPlayer(p) == steamId)
            {
                name = p?.Username?.Value.ToString();
                return !string.IsNullOrEmpty(name);
            }
        }
        return false;
    }

    public static Player GetPlayerByNumber(NetworkManager nm, int number, out ulong clientId)
    {
        clientId = 0UL;
        if (nm == null) return null;

        foreach (var cc in nm.ConnectedClientsList)
        {
            var p = cc.PlayerObject?.GetComponent<Player>();
            if (p == null) continue;

            if (p.Number != null && p.Number.Value == number)
            {
                clientId = cc.ClientId;
                return p;
            }
        }
        return null;
    }

    public static Player FindByName(NetworkManager nm, string name, out ulong clientId)
    {
        clientId = 0UL;
        if (nm == null || string.IsNullOrEmpty(name)) return null;
        string want = Norm(name);

        foreach (var cc in nm.ConnectedClientsList)
        {
            var p = cc.PlayerObject?.GetComponent<Player>();
            if (p == null) continue;
            string rawName = p.Username?.Value.ToString();
            string n = Norm(rawName);
            if (n == want)
            {
                clientId = cc.ClientId;
                return p;
            }
        }
        return null;
    }

    public static Player FindByNameSmart(NetworkManager nm, string input, out ulong targetClientId)
    {
        targetClientId = 0UL;
        if (nm == null) return null;
        string want = Norm(input);

        foreach (var cc in nm.ConnectedClientsList)
        {
            var p = cc.PlayerObject?.GetComponent<Player>();
            if (p == null) continue;
            string rawName = p.Username?.Value.ToString();
            string n = Norm(rawName);
            if (string.IsNullOrEmpty(n)) continue;

            if (n == want || n.StartsWith(want) || n.Contains(want))
            {
                targetClientId = cc.ClientId;
                return p;
            }
        }
        return null;
    }

    public static bool ResolvePlayer(NetworkManager nm, string token,
                                      out Player target, out ulong targetClientId, out ulong targetSteam)
    {
        target = null;
        targetClientId = 0;
        targetSteam = 0;

        if (nm == null || string.IsNullOrEmpty(token)) return false;

        string t = token.Trim();
        if (t.StartsWith("#")) t = t.Substring(1);

        if (AllDigits(t))
        {
            int num;
            if (t.Length <= 3 && int.TryParse(t, out num))
            {
                target = GetPlayerByNumber(nm, num, out targetClientId);
                if (target != null)
                {
                    targetSteam = GetSteamIdFromPlayer(target);
                    return true;
                }
            }

            if (t.Length >= 8 && ulong.TryParse(t, out var steamAsNumber))
            {
                foreach (var cc in nm.ConnectedClientsList)
                {
                    var p = cc.PlayerObject?.GetComponent<Player>();
                    if (GetSteamIdFromPlayer(p) == steamAsNumber)
                    {
                        target = p;
                        targetClientId = cc.ClientId;
                        targetSteam = steamAsNumber;
                        return true;
                    }
                }
                targetSteam = steamAsNumber;
                return true;
            }
        }

        target = FindByNameSmart(nm, token, out targetClientId);
        if (target != null)
        {
            targetSteam = GetSteamIdFromPlayer(target);
            return true;
        }

        return false;
    }

    public static string Norm(string s) => string.IsNullOrEmpty(s) ? "" : s.Trim().ToLowerInvariant();

    public static bool AllDigits(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        for (int i = 0; i < s.Length; i++) if (!char.IsDigit(s[i])) return false;
        return true;
    }

    public static string TeamColor(Player p)
    {
        if (p == null) return ChatColors.GREY;
        switch (p.Team)
        {
            case PlayerTeam.Red: return ChatColors.RED;
            case PlayerTeam.Blue: return ChatColors.BLUE;
            default: return ChatColors.GREY;
        }
    }
}

public static class ChatColors
{
    public const string WHITE = "<color=#FFFFFFFF>";
    public const string ORANGE = "<color=#FF9500FF>";
    public const string RED = "<color=#FF0000FF>";
    public const string BLUE = "<color=#0066CCFF>";
    public const string GREY = "<color=#808080FF>";
    public const string PINK = "<color=#FF66CCFF>";
    public const string GREEN = "<color=#00FF00FF>";
    public const string B = "<b>", EB = "</b>", EC = "</color>";
}
