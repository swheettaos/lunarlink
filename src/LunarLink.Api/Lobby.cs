using System;
using System.Text;
using MoonCleaners;
using Steamworks;

namespace LunarLink
{
    // The name your hosted game shows in the server list (user, 2026-11-09). The game builds it as "<Steam name>'s crew"
    // (MoonSteamLobby.CrewName) and has no box to type one. Host only; only when asked.
    //   GET  /lobby                       what the lobby publishes to Steam (every key and value), and the game's own fields
    //   POST /lobby/modify?name=...       set the crew name: the game's field, every lobby key that held the old name, then
    //                                     the game's own lobby refresh
    //   POST /lobby/modify?key=&value=    set one lobby key directly (for testing what the list reads)
    static class Lobby
    {
        static CSteamID Id() => new CSteamID(MoonSteamLobby.LobbyId);

        public static string Data()
        {
            var sb = new StringBuilder("{\"inLobby\":").Append(MoonSteamLobby.InLobby ? "true" : "false")
                .Append(",\"isHost\":").Append(MoonSteamLobby.IsHost ? "true" : "false")
                .Append(",\"crewName\":").Append(J.S(MoonSteamLobby.CrewName))
                .Append(",\"privacy\":").Append(J.S(MoonSteamLobby.CurrentPrivacy.ToString()))
                .Append(",\"lobbyId\":").Append(MoonSteamLobby.LobbyId).Append(",\"data\":{");
            if (MoonSteamLobby.InLobby)
            {
                int n = SteamMatchmaking.GetLobbyDataCount(Id());
                for (int i = 0; i < n; i++)
                {
                    string k, v;
                    if (!SteamMatchmaking.GetLobbyDataByIndex(Id(), i, out k, 256, out v, 8192)) continue;
                    if (i > 0) sb.Append(',');
                    sb.Append(J.S(k)).Append(':').Append(J.S(v));
                }
            }
            return sb.Append("}}").ToString();
        }

        public static string Name(string text)
        {
            if (!MoonSteamLobby.InLobby || !MoonSteamLobby.IsHost) return "{\"ok\":false,\"reason\":\"host a game first\"}";
            text = (text ?? "").Trim(); if (text.Length == 0 || text.Length > 60) return "{\"ok\":false,\"reason\":\"name must be 1-60 characters\"}";
            string old = MoonSteamLobby.CrewName ?? "";
            MoonSteamLobby.CrewName = text;
            var changed = new StringBuilder("[");
            int n = SteamMatchmaking.GetLobbyDataCount(Id()), c = 0;
            for (int i = 0; i < n; i++)
            {
                string k, v;
                if (!SteamMatchmaking.GetLobbyDataByIndex(Id(), i, out k, 256, out v, 8192)) continue;
                if (old.Length > 0 && v == old)
                {
                    SteamMatchmaking.SetLobbyData(Id(), k, text);
                    if (c++ > 0) changed.Append(','); changed.Append(J.S(k));
                }
            }
            try { MoonSteamLobby.RefreshLobby(); } catch (Exception e) { Plugin.L.LogWarning("lobby refresh: " + e.Message); }
            return "{\"ok\":true,\"old\":" + J.S(old) + ",\"name\":" + J.S(MoonSteamLobby.CrewName) + ",\"keysChanged\":" + changed.Append(']') + "}";
        }

        public static string Set(string key, string value)
        {
            if (!MoonSteamLobby.InLobby || !MoonSteamLobby.IsHost) return "{\"ok\":false,\"reason\":\"host a game first\"}";
            if (string.IsNullOrEmpty(key)) return "{\"ok\":false,\"reason\":\"key\"}";
            bool ok = SteamMatchmaking.SetLobbyData(Id(), key, value ?? "");
            return "{\"ok\":" + (ok ? "true" : "false") + "}";
        }
    }
}
