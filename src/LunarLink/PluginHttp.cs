// Normal BepInEx plugins (loaded at start from BepInEx/plugins/) can answer HTTP requests too: their plugin class
// (the BepInPlugin one) may have public  string HandleHttp(string method, string path, NameValueCollection query,
// string body)  (null = not mine) and  string[] HttpHelp()  (one line per endpoint, "GET /path?x= | what it does") -
// the same as a hot plugin's, so a plugin that ships both ways answers both ways.
// Looked up once, on the first request no module answers (plugins don't change after start).
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reflection;
using BepInEx.Unity.IL2CPP;

namespace LunarLink
{
    static class PluginHttp
    {
        sealed class Entry { public string Name; public object Obj; public MethodInfo Http, Help; }
        static List<Entry> found;

        static List<Entry> Found()
        {
            if (found != null) return found;
            found = new List<Entry>();
            foreach (var info in IL2CPPChainloader.Instance.Plugins.Values)
            {
                var o = info.Instance; if (o == null || o.GetType().Assembly == typeof(PluginHttp).Assembly) continue;
                var t = o.GetType();
                var http = t.GetMethod("HandleHttp", BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(string), typeof(string), typeof(NameValueCollection), typeof(string) }, null);
                if (http == null || http.ReturnType != typeof(string)) continue;
                var help = t.GetMethod("HttpHelp", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (help != null && help.ReturnType != typeof(string[])) help = null;
                found.Add(new Entry { Name = info.Metadata.Name, Obj = o, Http = http, Help = help });
                Plugin.L.LogInfo("plugin endpoints: " + info.Metadata.Name + " answers requests" + (help != null ? " and describes them" : ""));
            }
            return found;
        }

        internal static string Http(string method, string path, NameValueCollection query, string body)
        {
            foreach (var e in Found())
            {
                string r;
                try { r = (string)e.Http.Invoke(e.Obj, new object[] { method, path, query, body }); }
                catch (Exception x) { var i = x.InnerException ?? x; return "{\"error\":" + J.S(e.Name + ": " + i.GetType().Name + ": " + i.Message) + "}"; }
                if (r != null) return r;
            }
            return null;
        }

        internal static List<(string owner, string[] lines)> Help()
        {
            var list = new List<(string, string[])>();
            foreach (var e in Found())
            {
                if (e.Help == null) continue;
                try { list.Add((e.Name, (string[])e.Help.Invoke(e.Obj, null) ?? new string[0])); }
                catch (Exception x) { list.Add((e.Name, new[] { "? | HttpHelp failed: " + (x.InnerException ?? x).Message })); }
            }
            return list;
        }
    }
}
