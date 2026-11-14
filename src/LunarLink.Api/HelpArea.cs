// GET /help: every endpoint there is, described by whoever serves it - every route in the table, the core's and each
// module's by area (Api.Route.Help), and each plugin's HttpHelp() lines, hot or normal. ?path=/structures narrows it to the
// endpoints whose path starts with that. GET /help.html is the same as a searchable page. The F9 window's API tab
// reads /help through the core's route table.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LunarLink
{
    // Every endpoint and what it does: the core's, each module area's and each plugin's, as JSON and as a searchable page.
    static class HelpArea
    {
        internal static void Start(string state)
        {
            Api.Get("/help", "?path= : every endpoint and what it does (this list); &details=1 adds LunarLink's own endpoints' parameters, examples and answers", r => Json(r.Query["path"], r.Query["details"] == "1"));
            Api.Get("/help.html", "The same list as a searchable page, with the details: open it in a browser", r => Html());
        }

        // LunarLink's own endpoints' details (docs/endpoints.toml, built in as endpoints.json), by path: each one's raw JSON
        static Dictionary<string, string> details;
        static Dictionary<string, string> Details()
        {
            if (details != null) return details;
            details = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var s = typeof(HelpArea).Assembly.GetManifestResourceStream("LunarLink.endpoints.json"))
            {
                if (s == null) return details;
                using (var doc = System.Text.Json.JsonDocument.Parse(s))
                    foreach (var p in doc.RootElement.EnumerateObject()) details[p.Name] = p.Value.GetRawText();
            }
            return details;
        }

        internal sealed class Endpoint { public string Group, Kind, Method, Path, Text; }

        // a plugin's line: "GET /path?x= | what it does"
        static Endpoint Parse(string group, string kind, string line)
        {
            var bar = line.IndexOf('|'); string head = (bar < 0 ? line : line[..bar]).Trim(), text = bar < 0 ? "" : line[(bar + 1)..].Trim();
            var sp = head.IndexOf(' ');
            return new Endpoint { Group = group, Kind = kind, Method = sp < 0 ? "?" : head[..sp], Path = sp < 0 ? head : head[(sp + 1)..].Trim(), Text = text };
        }

        internal static List<Endpoint> All()
        {
            var list = new List<Endpoint>();
            foreach (var r in Api.All.OrderBy(r => r.Module == Api.Core ? 0 : 1))       // the core's own first
                list.Add(new Endpoint { Group = r.Area, Kind = r.Module == Api.Core ? "core" : r.Module, Method = r.Method, Path = r.Path,
                                        Text = r.Help + (r.Method == "POST" && !r.Write ? " (changes nothing)" : "") });
            foreach (var (owner, lines) in Hot.Help()) foreach (var l in lines) list.Add(Parse(owner, "hot plugin", l));
            foreach (var (owner, lines) in PluginHttp.Help()) foreach (var l in lines) list.Add(Parse(owner, "plugin", l));
            return list;
        }

        // GET /help.html: the searchable reference page (help.html, built into the DLL) with the live list in it
        internal static string Html()
        {
            string page;
            using (var s = typeof(HelpArea).Assembly.GetManifestResourceStream("LunarLink.help.html"))
            using (var r = new System.IO.StreamReader(s)) page = r.ReadToEnd();
            var data = Json(null, true);
            data = data.Substring(0, data.Length - 1) + ",\"note\":" + J.S("live from the running game, plugins included") + "}";
            return page.Replace("/*DATA*/null", data.Replace("</", "<\\/"));
        }

        internal static string Json(string prefix, bool withDetails = false)
        {
            var d = withDetails ? Details() : null;
            var sb = new StringBuilder("{\"groups\":[");
            bool firstGroup = true;
            foreach (var g in All().Where(e => string.IsNullOrEmpty(prefix) || e.Path.StartsWith(prefix, StringComparison.Ordinal)).GroupBy(e => (e.Group, e.Kind)))
            {
                if (!firstGroup) sb.Append(','); firstGroup = false;
                sb.Append("{\"group\":").Append(J.S(g.Key.Group)).Append(",\"from\":").Append(J.S(g.Key.Kind)).Append(",\"endpoints\":[");
                // details only for LunarLink's own routes (a plugin's endpoint may share no path with them)
                sb.Append(string.Join(",", g.Select(e => "{\"method\":" + J.S(e.Method) + ",\"path\":" + J.S(e.Path) + ",\"help\":" + J.S(e.Text)
                    + (d != null && (e.Kind == "core" || e.Kind.StartsWith("LunarLink.")) && d.TryGetValue(e.Path, out var x) ? ",\"d\":" + x : "") + "}")));
                sb.Append("]}");
            }
            return sb.Append("]}").ToString();
        }
    }
}
