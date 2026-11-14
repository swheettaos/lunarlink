namespace LunarLink
{
    // The session itself: the team's money (read, or changed by the host) and the process's threads.
    static class GameArea
    {
        internal static void Start(string state)
        {
            Api.Get("/game/money", "The team's money", r => Money(Api.World));
            Api.Post("/game/money/modify", "?add=<dollars> (may be negative) or ?set=<dollars> : change the team's money (the host only, in a crew game); logged", r => SetMoney(r));
            Api.Get("/debug/threads", "Every thread of the game's process: id, state, CPU seconds, when it started (for a hang)", r => Threads());
        }


        // Through the wallet's own setter, as the game's payments go: it sets the balance and raises the wallet's Changed
        // with the difference (what the game and other mods, such as Crew Tools' money log, follow). In a crew game only
        // the host's money is the team's, so a crew-mate is refused.
        static string SetMoney(Req r)
        {
            var w = Api.World; if (w == null) return "{\"error\":\"no world loaded yet\"}";
            bool add = r.Query["add"] != null, set = r.Query["set"] != null;
            if (add == set) return Api.Result(false, "give add=<dollars> or set=<dollars>");
            if (!double.TryParse(r.Query[add ? "add" : "set"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amount) || !double.IsFinite(amount))
                return Api.Result(false, "not a number of dollars");
            try { if (MoonCleaners.MoonSteamLobby.InLobby && !MoonCleaners.MoonSteamLobby.IsHost) return Api.Result(false, "in a crew game only the host can change the team's money"); } catch { }
            var wallet = w.wallets;
            double before = Il2CppSystem.Decimal.ToDouble(wallet.Balance), after = add ? before + amount : amount;
            if (after < 0 || after > 1e12) return Api.Result(false, "the money would be " + after.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " (it must stay between 0 and 1,000,000,000,000)");
            string me = "host"; try { me = Steamworks.SteamUser.GetSteamID().m_SteamID.ToString(); } catch { }
            wallet.set_Item(me, new Il2CppSystem.Decimal(after));
            double now = Il2CppSystem.Decimal.ToDouble(wallet.Balance);
            var line = "money changed by the API: " + before.ToString("N2") + " -> " + now.ToString("N2");
            Plugin.L.LogInfo(line); Pump.Journal(line);
            return "{\"ok\":true,\"before\":" + J.F(before) + ",\"after\":" + J.F(now) + ",\"change\":" + J.F(now - before) + "}";
        }

        static string Money(MoonCleaners.MoonWorld w)
        {
            double m = Il2CppSystem.Decimal.ToDouble(w.wallets.Balance);
            return "{\"money\":" + J.F(m) + "}";
        }

        // the process's threads as the system sees them (.NET can't list its managed threads); a hung game shows which
        // ones still use CPU and which started late (at quitting, say)
        static string Threads()
        {
            var sb = new System.Text.StringBuilder("{\"threads\":[");
            int n = 0;
            using (var proc = System.Diagnostics.Process.GetCurrentProcess())
                foreach (System.Diagnostics.ProcessThread t in proc.Threads)
                {
                    string started = "null", cpu = "null", state = "?";
                    try { state = t.ThreadState.ToString(); } catch { }
                    try { cpu = J.F(t.TotalProcessorTime.TotalSeconds); } catch { }
                    try { started = J.S(t.StartTime.ToString("HH:mm:ss")); } catch { }
                    if (n++ > 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(t.Id).Append(",\"state\":").Append(J.S(state)).Append(",\"cpu\":").Append(cpu).Append(",\"started\":").Append(started).Append('}');
                }
            return sb.Append("],\"count\":").Append(n).Append('}').ToString();
        }
    }
}
