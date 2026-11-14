namespace LunarLink
{
    // The session itself: the team's money, the process's threads, and once the game has loaded, MoonFramework's per-save file is backed up (FrameworkBackup).
    static class GameArea
    {
        internal static void Start(string state)
        {
            if (state == null) FrameworkBackup.Run();       // once per game start, not on every reload
            Api.Get("/game/money", "The team's money", r => Money(Api.World));
            Api.Get("/debug/threads", "Every thread of the game's process: id, state, CPU seconds, when it started (for a hang)", r => Threads());
            Api.Get("/game/backup", "The last MoonFramework file backup: how many save slots were copied", r => "{\"last\":" + J.S(FrameworkBackup.Last) + "}");
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
