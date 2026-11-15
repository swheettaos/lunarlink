using System;
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
            var o = new JsonOut().Obj().Bool("inLobby", MoonSteamLobby.InLobby).Bool("isHost", MoonSteamLobby.IsHost)
                .Str("crewName", MoonSteamLobby.CrewName).Str("privacy", MoonSteamLobby.CurrentPrivacy.ToString())
                .Raw("lobbyId", MoonSteamLobby.LobbyId.ToString()).Obj("data");
            if (MoonSteamLobby.InLobby)
            {
                int n = SteamMatchmaking.GetLobbyDataCount(Id());
                for (int i = 0; i < n; i++)
                {
                    string k, v;
                    if (!SteamMatchmaking.GetLobbyDataByIndex(Id(), i, out k, 256, out v, 8192)) continue;
                    o.Str(k, v);
                }
            }
            return o;
        }

        public static string Name(string text)
        {
            if (!MoonSteamLobby.InLobby || !MoonSteamLobby.IsHost) return Api.Result(false, "host a game first");
            text = (text ?? "").Trim(); if (text.Length == 0 || text.Length > 60) return Api.Result(false, "name must be 1-60 characters");
            string old = MoonSteamLobby.CrewName ?? "";
            MoonSteamLobby.CrewName = text;
            var changed = new System.Collections.Generic.List<string>();
            int n = SteamMatchmaking.GetLobbyDataCount(Id());
            for (int i = 0; i < n; i++)
            {
                string k, v;
                if (!SteamMatchmaking.GetLobbyDataByIndex(Id(), i, out k, 256, out v, 8192)) continue;
                if (old.Length > 0 && v == old)
                {
                    SteamMatchmaking.SetLobbyData(Id(), k, text);
                    changed.Add(k);
                }
            }
            try { MoonSteamLobby.RefreshLobby(); } catch (Exception e) { Plugin.L.LogWarning("lobby refresh: " + e.Message); }
            var o = new JsonOut().Obj().Bool("ok", true).Str("old", old).Str("name", MoonSteamLobby.CrewName).Arr("keysChanged");
            foreach (var k in changed) o.Str(k);
            return o;
        }

        public static string Set(string key, string value)
        {
            if (!MoonSteamLobby.InLobby || !MoonSteamLobby.IsHost) return Api.Result(false, "host a game first");
            if (string.IsNullOrEmpty(key)) return Api.Result(false, "key");
            return new JsonOut().Obj().Bool("ok", SteamMatchmaking.SetLobbyData(Id(), key, value ?? ""));
        }
    }
}
