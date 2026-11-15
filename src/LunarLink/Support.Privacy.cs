using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace LunarLink
{
    // Blanking out what identifies a player: names, Steam ids, the user name in paths, LAN addresses, the access key.
    static partial class Support
    {
        // players' names from every "<Steam id> | <name> | ..." list in BepInEx/config (Crew Tools', Crew Permissions'),
        // the local player's Steam name, the PC's name
        static List<string> Names()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            Try(() =>
            {
                foreach (var f in Directory.GetFiles(Path.Combine(BepRoot, "config"), "*players*.txt"))
                    foreach (var l in File.ReadAllLines(f))
                    {
                        var parts = l.Split('|');
                        if (parts.Length >= 2 && Regex.IsMatch(parts[0].Trim(), @"^\d{15,20}$")) names.Add(parts[1].Trim());
                    }
                return "";
            });
            if (!string.IsNullOrWhiteSpace(PlayerName)) names.Add(PlayerName.Trim());
            Try(() => { names.Add(Environment.MachineName); return ""; });
            return names.Where(n => n.Length >= 3).OrderByDescending(n => n.Length).ToList();
        }

        static readonly Regex SteamId = new Regex(@"\b7656119\d{10}\b", RegexOptions.Compiled),
            UserPath = new Regex(@"(?i)([\\/])(home|users)([\\/])(?!public\b|steamuser\b|default\b)([^\\/\s""':|]+)", RegexOptions.Compiled),
            Lan = new Regex(@"\b(10\.\d{1,3}|192\.168|172\.(1[6-9]|2\d|3[01])|100\.(6[4-9]|[7-9]\d|1[01]\d|12[0-7])|169\.254)\.\d{1,3}\.\d{1,3}\b",
                RegexOptions.Compiled),
            AnyIp = new Regex(@"(?<=(://|from |refused |address |Address = |on ))(?!127\.0\.0\.1\b|0\.0\.0\.0\b)\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b",
                RegexOptions.Compiled),
            KeyLine = new Regex(@"(?im)^(Access key\s*=\s*).+$", RegexOptions.Compiled),
            KeyQuery = new Regex(@"(?i)([?&]key=)[^&\s""]+", RegexOptions.Compiled);

        internal static string Blank(string text, List<string> names)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (!string.IsNullOrEmpty(Plugin.AccessKey)) text = text.Replace(Plugin.AccessKey, "[key]");
            text = KeyLine.Replace(text, "$1[key]");
            text = KeyQuery.Replace(text, "$1[key]");
            text = SteamId.Replace(text, "[steamid]");
            text = UserPath.Replace(text, "$1$2$3[user]");
            text = Lan.Replace(text, "[lan-ip]");
            text = AnyIp.Replace(text, "[ip]");
            for (int i = 0; i < names.Count; i++)
            {
                var n = names[i]; var pat = Regex.Escape(n);
                if (char.IsLetterOrDigit(n[0])) pat = @"(?<![\w])" + pat;
                if (char.IsLetterOrDigit(n[^1])) pat += @"(?![\w])";
                text = Regex.Replace(text, pat, "[player " + (i + 1) + "]");
            }
            return text;
        }

        // ---- small things ----
    }
}
