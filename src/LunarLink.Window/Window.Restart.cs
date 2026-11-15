using System;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace LunarLink
{
    // Leaving the world on the player's say-so: Save & reload (a wrapped plugin's reload that waits for the main menu)
    // and Restart & Apply (a normal plugin turned on or off, or a setting changed, applies at the next start).
    static partial class Window
    {
        // ------------------------------------------------ Save & reload / Save & menu
        // The game's own save (the pause menu's), and once it has finished, the game's own way back to the menu
        // (MoonSession.ReturnToMenu); the core reloads the plugin there. Save & reload (solo) then loads the same save
        // again once that reload has started cleanly, the way a click on it does (the main menu's Play).
        static MoonCleaners.MoonPauseMenu saving; static float savingSince;
        static string reloadThenLoad; static float atMenuSince = -1f;

        static string SaveThenMenu(string thenLoad)
        {
            reloadThenLoad = thenLoad; atMenuSince = -1f;
            saving = PauseMenu();
            if (saving == null) return "Couldn't find the game's pause menu: save and go to the main menu yourself";
            savingSince = Time.realtimeSinceStartup;
            saving.SaveGame();
            return "Saving, then going to the main menu...";
        }

        static MoonCleaners.MoonPauseMenu PauseMenu()
        {
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<MoonCleaners.MoonPauseMenu>());
            return found.Length == 0 ? null : found[0].Cast<MoonCleaners.MoonPauseMenu>();
        }

        // each frame: once the save has finished, the main menu
        static void SavedThenMenu()
        {
            if (saving == null) return;
            float waited = Time.realtimeSinceStartup - savingSince;
            bool busy; try { busy = saving.IsSaving; } catch { busy = false; }
            if ((busy || waited < 0.5f) && waited < 30f) return;              // the save starts within a frame or two
            saving = null;
            if (root != null) Close();
            MoonCleaners.MoonSession.ReturnToMenu();
        }

        // each frame at the main menu after Save & reload: once the plugin's new copy runs, its save again
        static void LoadAgain()
        {
            if (reloadThenLoad == null || saving != null) return;
            bool atMenu = false; try { atMenu = MoonCleaners.MoonSession.InMenu && !Hot.InWorld; } catch (System.Exception caught) { Quiet.Note(caught); }
            if (!atMenu) { atMenuSince = -1f; return; }
            if (atMenuSince < 0f) { atMenuSince = Time.realtimeSinceStartup; return; }
            if (Time.realtimeSinceStartup - atMenuSince < 1.5f) return;          // the menu settles
            var p = Hot.Find(reloadThenLoad); var name = reloadThenLoad;
            // the core reloads it once the menu's own loading screen is gone, which can take a few seconds: wait for that
            // (up to 30 s), and give up only on a copy that actually failed
            bool waiting = p != null && p.Error == null && (p.ReloadAtMenu || !p.Running);
            if (waiting && Time.realtimeSinceStartup - atMenuSince < 30f) return;
            reloadThenLoad = null;
            if (p == null || p.ReloadAtMenu || !p.Running || p.Error != null)
            {
                Plugin.L.LogWarning("window: " + name + " didn't start cleanly at the main menu, so its save wasn't loaded again"
                    + (p?.Error != null ? ": " + p.Error : ""));
                return;
            }
            // the main menu's own Play, as a click on the save does: plugins hook it (Mars Sweepers sets up its planet
            // session there), which the start step under it (StartShift) would skip
            var menus = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<MoonCleaners.MoonMainMenu>());
            if (menus.Length == 0) { Plugin.L.LogWarning("window: no main menu found, so " + name + "'s save wasn't loaded again: pick it yourself"); return; }
            Plugin.L.LogInfo("window: " + name + " reloaded at the main menu; loading the save again");
            menus[0].Cast<MoonCleaners.MoonMainMenu>().Play();
        }

        // ------------------------------------------------ Restart & Apply
        // The game's save (none needed at the main menu), waited for until it has finished; then a helper that waits for
        // this process to end and starts the game again; then the game's own quit (MoonSession.QuitGame: a plain
        // Application.Quit is refused in a world, where the game asks first). The game can hang after Unity has shut down,
        // so a helper that has waited 30 s ends it. On Windows the helper is a hidden PowerShell that asks Steam to start
        // the game. Under Proton (Linux) Steam would still count the game as running while the helper waits, so the helper
        // is a bash script (Wine starts Linux programs too) that starts the game the way Proton does: its wine running
        // Steam's stub with the game, in the same prefix, writing what it did to BepInEx/lunarlink-restart.log. If the
        // helper can't start, the game stays open and the line at the foot says to restart by hand.
        const string SteamAppId = "5335510";                      // Moon Sweepers
        const int HangSeconds = 30;                                // after this the helper ends a game that didn't close
        static float restartAt;                                    // when Restart was pressed (0: no restart under way)
        static MoonCleaners.MoonPauseMenu restartSave;
        static bool helperStarted;                                 // once per run: a second helper would start a second game

        static void Restart()
        {
            if (restartAt > 0f || helperStarted) return;
            restartSave = null;
            if (Api.World != null)
            {
                restartSave = PauseMenu();
                if (restartSave == null) { message = "Restart cancelled: the game's save isn't available. Restart the game yourself"; Draw(true); return; }
                try { restartSave.SaveGame(); }
                catch (Exception e) { message = "Restart cancelled: the save failed (" + e.Message + "). Restart the game yourself"; Draw(true); return; }
            }
            restartAt = Time.unscaledTime;
            Plugin.L.LogInfo("window: saving, then restarting the game to apply plugin changes");
            Draw(true);
        }

        // each frame while a restart is under way: true holds everything else off
        static bool Restarting()
        {
            if (restartAt <= 0f) return false;
            float waited = Time.unscaledTime - restartAt;
            bool busy = false; try { busy = restartSave != null && restartSave.IsSaving; } catch (Exception caught) { Quiet.Note(caught); }
            if ((busy || waited < 0.5f) && waited < 30f) return true;     // the save starts within a frame or two
            restartAt = 0f; restartSave = null;
            try
            {
                Process.Start(Support.Wine() != null ? ProtonHelper() : WindowsHelper());
                helperStarted = true;
                Plugin.L.LogInfo("window: saved; restarting Moon Sweepers");
                MoonCleaners.MoonSession.QuitGame();
            }
            catch (Exception e)
            {
                message = (helperStarted ? "Couldn't quit the game (" : "Saved, but couldn't restart the game (") + e.Message + "): "
                    + (helperStarted ? "quit it yourself and it starts again" : "restart it yourself");
                Plugin.L.LogWarning("window: restart failed: " + e);
                if (root != null) Draw(true);
            }
            return true;
        }

        static ProcessStartInfo WindowsHelper()
        {
            int pid = Process.GetCurrentProcess().Id;
            var start = new ProcessStartInfo { FileName = "powershell.exe", UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command",
                "Wait-Process -Id " + pid + " -Timeout " + HangSeconds + " -ErrorAction SilentlyContinue; "
                + "Stop-Process -Id " + pid + " -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1; "
                + "Start-Process 'steam://rungameid/" + SteamAppId + "'" })
                start.ArgumentList.Add(a);
            return start;
        }

        // Wine's own answer to where a Windows path is on Linux (a Steam library on another drive letter included)
        [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr wine_get_unix_file_name(string dos);
        static string LinuxPath(string windows)
        {
            var p = wine_get_unix_file_name(windows);
            if (p == IntPtr.Zero) throw new InvalidOperationException("Wine has no Linux path for " + windows);
            return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(p);       // (Wine's heap: a few bytes, once)
        }

        // Under Wine every Windows process is a Linux one: this one's Linux id is in /proc/self (Z: is the Linux root). Proton's wine is the one
        // its library path names (<proton>/files/lib/wine -> <proton>/files/bin/wine).
        static ProcessStartInfo ProtonHelper()
        {
            static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";              // a shell word
            string linuxPid = System.IO.File.ReadAllText(@"Z:\proc\self\stat").Split(' ')[0];    // Z: is the Linux root
            var dllPath = (Environment.GetEnvironmentVariable("WINEDLLPATH") ?? "").Split(':').FirstOrDefault(p => p.EndsWith("/lib/wine"));
            if (dllPath == null) throw new InvalidOperationException("Proton's wine wasn't found (no WINEDLLPATH)");
            string wine = dllPath.Substring(0, dllPath.Length - "/lib/wine".Length) + "/bin/wine";
            string exe = Process.GetCurrentProcess().MainModule.FileName;                      // S:\...\Moon Sweepers.exe
            string dir = LinuxPath(System.IO.Path.GetDirectoryName(exe));
            string log = LinuxPath(BepInEx.Paths.BepInExRootPath) + "/lunarlink-restart.log";
            string script = "echo \"$(date) waiting for the game (" + linuxPid + ") to close\"; n=0; "
                + "while [ -e /proc/" + linuxPid + " ]; do sleep 1; n=$((n+1)); "
                + "if [ $n -eq " + HangSeconds + " ]; then echo \"$(date) it hasn't closed: ending it\"; kill -9 " + linuxPid + "; fi; done; sleep 1; "
                + "echo \"$(date) starting the game\"; cd " + Q(dir) + " && exec " + Q(wine) + " 'c:\\Program Files (x86)\\Steam\\steam.exe' " + Q(exe);
            var start = new ProcessStartInfo { FileName = @"Z:\bin\bash", UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("( " + script + " ) >> " + Q(log) + " 2>&1 &");
            return start;
        }
    }
}
