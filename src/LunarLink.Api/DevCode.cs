using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Iced.Intel;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace LunarLink
{
    // Reading the game's compiled code from inside the running game (2026-11-09: why a crew-mate's game says "Save
    // unavailable. Building is paused."). The game's metadata is too new for Cpp2IL, but the code is loaded here:
    //   /dev/xref?find=text[&type=MoonCleaners.X]   game methods whose code uses a string containing text (all types,
    //                                               or one); slow the first time (scans every method) - read-only
    //   /dev/dis?type=MoonCleaners.X&method=Name[&n=0]   disassemble a method: strings, called methods named
    //   /dev/fields?type=MoonCleaners.X             a class's fields with their offsets (to read [reg+offset])
    // Read-only: nothing is patched or called.
    // SAFE (rewritten after a crash, 2026-11-09): every read goes through ReadProcessMemory on our own process, which
    // fails instead of crashing on bad memory; nothing in the game is initialised or called (Il2CppInterop's XrefScan
    // runs the game's metadata init and is not used).
    static class DevCode
    {
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr proc, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr read);
        static readonly IntPtr self = GetCurrentProcess();
        static byte[] Read(IntPtr a, int n)
        {
            if (a == IntPtr.Zero || n <= 0) return null;
            var b = new byte[n];
            return ReadProcessMemory(self, a, b, (UIntPtr)n, out var r) && (int)r == n ? b : null;
        }
        static bool Ptr(IntPtr a, out IntPtr v) { var b = Read(a, 8); v = b == null ? IntPtr.Zero : (IntPtr)BitConverter.ToInt64(b, 0); return b != null; }
        // a method's code, as much as can be read up to max (shorter near the end of the code)
        static byte[] Code(IntPtr a, int max)
        {
            // the most that can be read, halving past the end of readable memory (?bytes= below 256 used to read nothing)
            for (int n = Math.Max(16, max); n >= 16; n /= 2) { var b = Read(a, n); if (b != null) return b; }
            return null;
        }
        static IntPtr stringClass;
        static Dictionary<long, string> codeNames;          // code pointer -> Type::Method

        static IEnumerable<Type> GameTypes()
        {
            Type[] ts; try { ts = typeof(MoonCleaners.MoonWorld).Assembly.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types; }
            foreach (var t in ts) if (t != null) yield return t;
        }
        static IntPtr CodeOf(MethodBase m)
        {
            try
            {
                var f = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(m); if (f == null) return IntPtr.Zero;
                var mi = (IntPtr)f.GetValue(null); return mi != IntPtr.Zero && Ptr(mi, out var c) ? c : IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }
        static string Name(MethodBase m) => (m.DeclaringType?.FullName ?? "?") + "::" + m.Name + "/" + m.GetParameters().Length;
        // the game's code plus the system libraries' (Task, List...) - so calls into them are named too
        static IEnumerable<Type> AllTypes()
        {
            foreach (var t in GameTypes()) yield return t;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = a.GetName().Name;
                if (n != "Il2Cppmscorlib" && n != "Il2CppSystem" && n != "Il2CppSystem.Core") continue;
                Type[] ts; try { ts = a.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types; } catch { continue; }
                foreach (var t in ts) if (t != null) yield return t;
            }
        }
        static void Index()
        {
            if (codeNames != null) return;
            codeNames = new();
            stringClass = Il2CppClassPointerStore<string>.NativeClassPtr;
            foreach (var t in AllTypes())
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var c = CodeOf(m); if (c != IntPtr.Zero) codeNames.TryAdd(c.ToInt64(), Name(m));
                }
        }
        // a global slot the code reads: an initialised string literal, or a MethodInfo whose code we know
        static string Slot(IntPtr slot)
        {
            if (!Ptr(slot, out var v) || v == IntPtr.Zero) return null;
            long lv = v.ToInt64();
            if ((lv & 1) != 0)                                                   // not initialised: an encoded metadata token
            {
                long u = lv >> 1; long kind = (u >> 29) & 7, idx = u & 0x1FFFFFFF;
                return kind == 5 ? "literal#" + idx : null;
            }
            if (lv < 0x10000 || !Ptr(v, out var k)) return null;
            if (k == stringClass)
            {
                var lb = Read(v + 0x10, 4); if (lb == null) return null;
                int len = BitConverter.ToInt32(lb, 0); if (len < 0 || len > 4000) return null;
                var cb = Read(v + 0x14, len * 2); return cb == null ? null : "\"" + Encoding.Unicode.GetString(cb) + "\"";
            }
            if (codeNames.TryGetValue(k.ToInt64(), out var mn)) return "methodinfo " + mn;
            return null;
        }
        static IEnumerable<Instruction> Decode(IntPtr code, int max)
        {
            var bytes = Code(code, max); if (bytes == null) yield break;
            var dec = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(bytes)); dec.IP = (ulong)code.ToInt64();
            ulong end = dec.IP + (ulong)bytes.Length; int ccs = 0;
            while (dec.IP < end)
            {
                var ins = dec.Decode(); yield return ins;
                if (ins.Mnemonic == Mnemonic.Int3) { if (++ccs >= 2) yield break; } else ccs = 0;
            }
        }

        public static string Xref(string find, string type)
        {
            Index();
            var sb = new StringBuilder("{\"ok\":true,\"hits\":["); int n = 0, scanned = 0;
            foreach (var t in GameTypes())
            {
                if (!string.IsNullOrEmpty(type) && t.FullName != type) continue;
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var code = CodeOf(m);
                    if (m.IsGenericMethodDefinition || code == IntPtr.Zero) continue;
                    scanned++;
                    foreach (var ins in Decode(code, 8192))
                    {
                        if (!ins.IsIPRelativeMemoryOperand) continue;
                        var s = Slot((IntPtr)(long)ins.IPRelativeMemoryAddress); if (s == null || !s.Contains(find)) continue;
                        if (n++ > 0) sb.Append(',');
                        sb.Append("{\"method\":").Append(J.S(Name(m))).Append(",\"text\":").Append(J.S(s)).Append('}');
                    }
                }
            }
            return sb.Append("],\"scanned\":").Append(scanned).Append('}').ToString();
        }

        public static string Dis(string type, string method, int which, int maxBytes)
        {
            Index();
            Type t = null; foreach (var x in AllTypes()) if (x.FullName == type) { t = x; break; }
            if (t == null) return "{\"ok\":false,\"reason\":\"no type\"}";
            var ms = new List<MethodBase>();
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (m.Name == method) ms.Add(m);
            if (which >= ms.Count) return "{\"ok\":false,\"reason\":\"no such method (" + ms.Count + " overloads)\"}";
            var mb = ms[which]; var code = CodeOf(mb);
            if (code == IntPtr.Zero) return "{\"ok\":false,\"reason\":\"no code pointer\"}";
            var fmt = new NasmFormatter(); var outp = new StringOutput();
            var sb = new StringBuilder(); sb.Append("== ").Append(Name(mb)).Append(" @ 0x").Append(code.ToString("X")).Append('\n');
            foreach (var ins in Decode(code, maxBytes))
            {
                fmt.Format(ins, outp); string text = outp.ToStringAndReset(), note = "";
                if (ins.IsIPRelativeMemoryOperand) note = Slot((IntPtr)(long)ins.IPRelativeMemoryAddress) ?? "";
                if ((ins.FlowControl == FlowControl.Call || ins.FlowControl == FlowControl.UnconditionalBranch) && ins.Op0Kind == OpKind.NearBranch64)
                {
                    if (codeNames.TryGetValue((long)ins.NearBranchTarget, out var cn)) note = "-> " + cn;
                }
                sb.Append((ins.IP - (ulong)code.ToInt64()).ToString("X4")).Append("  ").Append(text.PadRight(48)).Append(note).Append('\n');
            }
            return sb.ToString();
        }

        // /dev/name?addr=hex - which known method starts at (or contains, nearest below) this code address
        public static string NameAt(string hex)
        {
            Index();
            if (!long.TryParse(hex.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var a)) return "{\"ok\":false}";
            if (codeNames.TryGetValue(a, out var n)) return "{\"ok\":true,\"exact\":true,\"name\":" + J.S(n) + "}";
            long best = 0; string bn = null;
            foreach (var kv in codeNames) if (kv.Key < a && kv.Key > best) { best = kv.Key; bn = kv.Value; }
            return "{\"ok\":true,\"exact\":false,\"nearestBelow\":" + J.S(bn) + ",\"offset\":" + (a - best) + "}";
        }

        public static string Fields(string type)
        {
            Type t = null; foreach (var x in AllTypes()) if (x.FullName == type) { t = x; break; }
            if (t == null) return "{\"ok\":false,\"reason\":\"no type\"}";
            var rows = new List<(int off, string name)>();
            for (var tt = t; tt != null && tt != typeof(Il2CppObjectBase); tt = tt.BaseType)
                foreach (var f in tt.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!f.Name.StartsWith("NativeFieldInfoPtr_")) continue;
                    try { var p = (IntPtr)f.GetValue(null); if (p != IntPtr.Zero) rows.Add(((int)IL2CPP.il2cpp_field_get_offset(p), tt.Name + "." + f.Name.Substring(19))); } catch { }
                }
            rows.Sort((a, b) => a.off.CompareTo(b.off));
            var sb = new StringBuilder(); foreach (var r in rows) sb.Append("0x").Append(r.off.ToString("X")).Append("  ").Append(r.name).Append('\n');
            return sb.ToString();
        }
    }
}
