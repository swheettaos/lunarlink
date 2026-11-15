namespace LunarLink
{
    // The session itself: the team's money (read, or changed by the host) and the process's threads.
    static class GameArea
    {
        internal static void Start()
        {
            Api.Get("/game/money", "The team's money", r => Money(Api.World));
            Api.Post("/game/money/modify",
                "?add=<dollars> (may be negative) or ?set=<dollars> : change the team's money (the host only, in a crew game); logged", r => SetMoney(r));
            Api.Get("/debug/threads", "Every thread of the game's process: id, state, CPU seconds, when it started (for a hang)", r => Threads());
        }


        // Through the wallet's own setter, as the game's payments go: it sets the balance and raises the wallet's Changed
        // with the difference (what the game and other mods, such as Crew Tools' money log, follow). In a crew game only
        // the host's money is the team's, so a crew-mate is refused.
        static string SetMoney(Req r)
        {
            var w = Api.World; if (w == null) return Api.Error("no world loaded yet");
            bool add = r.Query["add"] != null, set = r.Query["set"] != null;
            if (add == set) return Api.Result(false, "give add=<dollars> or set=<dollars>");
            if (!double.TryParse(r.Query[add ? "add" : "set"], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var amount) || !double.IsFinite(amount))
                return Api.Result(false, "not a number of dollars");
            try { if (MoonCleaners.MoonSteamLobby.InLobby
                && !MoonCleaners.MoonSteamLobby.IsHost) return Api.Result(false,
                "in a crew game only the host can change the team's money"); }
            catch (System.Exception caught) { Quiet.Note(caught); }
            var wallet = w.wallets;
            double before = Il2CppSystem.Decimal.ToDouble(wallet.Balance), after = add ? before + amount : amount;
            if (after < 0 || after > 1e12) return Api.Result(false, "the money would be "
                + after.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                + " (it must stay between 0 and 1,000,000,000,000)");
            string me = "host"; try { me = Steamworks.SteamUser.GetSteamID().m_SteamID.ToString(); } catch (System.Exception caught) { Quiet.Note(caught); }
            wallet.set_Item(me, new Il2CppSystem.Decimal(after));
            double now = Il2CppSystem.Decimal.ToDouble(wallet.Balance);
            var line = "money changed by the API: " + before.ToString("N2") + " -> " + now.ToString("N2");
            Plugin.L.LogInfo(line); Pump.Journal(line);
            return new JsonOut().Obj().Bool("ok", true).Num("before", before).Num("after", now).Num("change", now - before);
        }

        static string Money(MoonCleaners.MoonWorld w)
        {
            double m = Il2CppSystem.Decimal.ToDouble(w.wallets.Balance);
            return new JsonOut().Obj().Num("money", m);
        }

        // the process's threads as the system sees them (.NET can't list its managed threads); a hung game shows which
        // ones still use CPU and which started late (at quitting, say)
        static string Threads()
        {
            var o = new JsonOut().Obj().Arr("threads");
            int n = 0;
            using (var proc = System.Diagnostics.Process.GetCurrentProcess())
                foreach (System.Diagnostics.ProcessThread t in proc.Threads)
                {
                    string started = null, state = "?"; double cpu = double.NaN;      // NaN writes null
                    try { state = t.ThreadState.ToString(); } catch (System.Exception caught) { Quiet.Note(caught); }
                    try { cpu = t.TotalProcessorTime.TotalSeconds; } catch (System.Exception caught) { Quiet.Note(caught); }
                    try { started = t.StartTime.ToString("HH:mm:ss"); } catch (System.Exception caught) { Quiet.Note(caught); }
                    n++;
                    o.Obj().Num("id", t.Id).Str("state", state).Num("cpu", cpu).Str("started", started).End();
                }
            return o.End().Num("count", n);
        }
    }
}
