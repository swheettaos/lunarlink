// LunarLink: HTTP API into a running Moon Sweepers session. This file is the BepInEx plugin and its HTTP server.
// Listens on http://127.0.0.1:8765/ (config [Server]: another address lets other machines in, with an access key);
// every request is answered on Unity's main thread (Pump.cs).
// Uses a small TcpListener server (one request per connection) because HttpListener stalls under Proton.
using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using MoonCleaners;
using UnityEngine;

namespace LunarLink
{
    [BepInPlugin("lunarlink", "LunarLink", "1.3.0")]
    // after MoonFramework (when it's there): a hot plugin's Boot() registers buildings from LunarLink's Load
    [BepInDependency("moonsweepers.framework", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource L;
        internal static readonly ConcurrentQueue<Job> Pending = new();

        public override void Load()
        {
            L = Log;
            Support.Init();                                            // the machine as Unity sees it, and the BepInEx log keeper
            AddComponent<Pump>();
            var address = Config.Bind("Server", "Address", "127.0.0.1",
                "Where the API listens. 127.0.0.1 = this machine only (the default). This machine's address on your network (for example 192.168.1.20), or 0.0.0.0 for every network, lets other machines in: they must then send the Access key.").Value;
            int port = Config.Bind("Server", "Port", 8765, "The API's port.").Value;
            AccessKey = Config.Bind("Server", "Access key", "",
                "What another machine must send to use the API (header X-LunarLink-Key, or ?key=). Empty: other machines are refused. Requests from this machine never need it.").Value ?? "";
            var listener = Listen(address, port, out var listening);
            new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        var client = listener.AcceptTcpClient();
                        Pump.Journal("net: connection accepted; handing to reader");
                        System.Threading.Tasks.Task.Run(() => Receive(client));
                    }
                    catch (Exception e) { L.LogWarning("accept: " + e.Message); Thread.Sleep(200); }
                }
            }) { IsBackground = true, Name = "LunarLink" }.Start();
            Pump.StartWatcher();
            // the hot plugin window's settings live here so they outlast the window module's reloads
            WindowShowModules = Config.Bind("Hot plugins", "Show LunarLink modules", false, "Whether the hot plugin window lists LunarLink's own modules too.");
            WindowKey = Config.Bind("Hot plugins", "Window key", "F9", "Key that opens and closes the hot plugin window (an Input System key name, for example F9).");
            Pump.Routes(); Hot.Routes();                                // the core's own endpoints, in the route table
            Updates.Finish();                                          // a core update installed last run: its modules into BepInEx/hot/
            try { Hot.LoadAll(); } catch (Exception e) { L.LogError("hot: " + e); }   // Boot() here: before MoonFramework's build menu
            L.LogInfo("LunarLink listening on " + listening + (IPAddress.IsLoopback(((IPEndPoint)listener.LocalEndpoint).Address) ? " (this machine only)"
                : AccessKey.Length > 0 ? " (other machines with the access key)" : " (other machines refused: no access key set)"));
        }

        internal static string AccessKey = "";
        internal static BepInEx.Configuration.ConfigEntry<string> WindowKey;
        internal static BepInEx.Configuration.ConfigEntry<bool> WindowShowModules;

        // The configured address, or this machine only when it can't be used (not an address, or not one of this machine's).
        static TcpListener Listen(string address, int port, out string url)
        {
            if (port < 1 || port > 65535) { L.LogError("Server port " + port + " isn't a port; using 8765"); port = 8765; }
            if (!IPAddress.TryParse((address ?? "").Trim(), out var ip)) { L.LogError("Server address \"" + address + "\" isn't an IP address; listening on 127.0.0.1"); ip = IPAddress.Loopback; }
            try
            {
                var l = new TcpListener(ip, port); l.Start(64);
                url = "http://" + (ip.Equals(IPAddress.Any) ? "0.0.0.0" : ip.ToString()) + ":" + port + "/"; return l;
            }
            catch (Exception e) when (!ip.Equals(IPAddress.Loopback))
            {
                L.LogError("Couldn't listen on " + ip + ":" + port + " (" + e.Message + "); listening on 127.0.0.1 instead");
                var l = new TcpListener(IPAddress.Loopback, port); l.Start(64);
                url = "http://127.0.0.1:" + port + "/"; return l;
            }
        }

        // Read one HTTP request off the socket (request line, headers, Content-Length body) and queue it.
        static void Receive(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
                var s = client.GetStream();
                var head = new System.Collections.Generic.List<byte>();
                while (head.Count < 65536)
                {
                    int b = s.ReadByte();
                    if (b < 0) break;
                    head.Add((byte)b);
                    int n = head.Count;
                    if (n >= 4 && head[n - 4] == 13 && head[n - 3] == 10 && head[n - 2] == 13 && head[n - 1] == 10) break;
                }
                var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
                Pump.Journal("net: header read: " + lines[0]);
                var target = lines[0].Split(' ').Length > 1 ? lines[0].Split(' ')[1] : "/";
                int len = 0; string key = null; bool html = false;
                foreach (var h in lines)
                {
                    if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(h.Substring(15).Trim(), out len);
                    else if (h.StartsWith("X-LunarLink-Key:", StringComparison.OrdinalIgnoreCase)) key = h.Substring(18).Trim();
                    else if (h.StartsWith("Accept:", StringComparison.OrdinalIgnoreCase)) html = h.Contains("text/html");
                }
                var body = new byte[Math.Min(len, 4 << 20)];
                int got = 0;
                while (got < body.Length) { int r = s.Read(body, got, body.Length - got); if (r <= 0) break; got += r; }
                var q = target.IndexOf('?');
                var req = new Req
                {
                    Method = lines[0].Split(' ')[0],
                    Path = Uri.UnescapeDataString(q < 0 ? target : target.Substring(0, q)),
                    Query = System.Web.HttpUtility.ParseQueryString(q < 0 ? "" : target.Substring(q + 1)),
                    Body = Encoding.UTF8.GetString(body, 0, got)
                };
                key ??= req.Query["key"]; req.Query.Remove("key");        // never journalled
                // another machine: only with the access key (none set: refused). This machine: always.
                var from = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                if (!IPAddress.IsLoopback(from) && (AccessKey.Length == 0 || !SameKey(key, AccessKey)))
                {
                    Pump.Journal("net: refused " + from + " " + req.Path + (AccessKey.Length == 0 ? " (no access key set)" : " (wrong or no key)"));
                    Send(client, 403, AccessKey.Length == 0 ? "{\"error\":\"this API takes requests from other machines only with an access key, and none is set\"}"
                                                            : "{\"error\":\"wrong or missing access key (X-LunarLink-Key header, or key=)\"}");
                    return;
                }
                // the support file: written here, off the main thread, so it works at the main menu, with a module
                // broken, and while the game is frozen
                if (req.Method == "GET" && req.Path.TrimEnd('/') == "/support") { Pump.Journal("net: support file"); Send(client, 200, Support.Http(html)); return; }
                Pump.Journal("net: queueing " + req.Path + " (queue had " + Pending.Count + ")");
                Pending.Enqueue(new Job { Req = req, Client = client });
                Pump.Journal("net: queued " + req.Path);
            }
            catch (Exception e) { Pump.Journal("net: receive failed: " + e.Message); L.LogWarning("receive: " + e.Message); try { client.Close(); } catch { } }
        }

        // compares every character, so the time taken doesn't tell how much of a guess was right
        static bool SameKey(string a, string b)
        {
            if (a == null) return false;
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
        }

        internal static void Send(TcpClient client, int code, string body)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                // a page, JSON, or plain text (the /dev disassembly and field lists)
                var lead = body.TrimStart();
                var type = lead.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ? "text/html; charset=utf-8"
                         : lead.StartsWith("{") || lead.StartsWith("[") || lead.Length == 0 ? "application/json" : "text/plain; charset=utf-8";
                var header = $"HTTP/1.1 {code} {(code == 200 ? "OK" : code == 403 ? "Forbidden" : "Error")}\r\nContent-Type: {type}\r\n" +
                             $"Content-Length: {bytes.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
                Pump.Journal("net: sending " + bytes.Length + " bytes, code " + code);
                var s = client.GetStream();
                var hb = Encoding.ASCII.GetBytes(header);
                s.Write(hb, 0, hb.Length); s.Write(bytes, 0, bytes.Length); s.Flush();
                Pump.Journal("net: sent");
            }
            catch (Exception e) { Pump.Journal("net: send failed: " + e.Message); L.LogWarning("send: " + e.Message); }
            finally { try { client.Close(); } catch { } }
        }

        internal static int Served, Frames;
    }

    public class Req { public string Method, Path; public NameValueCollection Query; public string Body; }
    public class Job { public Req Req; public TcpClient Client; }
}
