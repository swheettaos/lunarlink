using System;
using System.Text;

namespace LunarLink
{
    // DLL filenames are often compact identifiers. Keep the friendly names for our
    // mods here, then use a conservative PascalCase fallback for other plugins.
    internal static class DisplayNames
    {
        internal static string For(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            var name = value;
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            switch (name)
            {
                case "WhoLeftTheLightOn": return "Who Left The Light On?";
                case "JoonasJoyride": return "J.O.O.N.A.S. Joyride";
                case "QuantumConveyors": return "Quantum Conveyors";
                case "LunarLink": return "Lunar Link";
                case "LunarLink.Api": return "Lunar Link API";
                case "LunarLink.Perf": return "Lunar Link Performance";
                case "LunarLink.Update": return "Lunar Link Updates";
                case "LunarLink.Window": return "Lunar Link Window";
            }
            if (name.IndexOf('.') >= 0 || name.IndexOf(' ') >= 0) return name;
            var b = new StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])))) b.Append(' ');
                b.Append(name[i]);
            }
            return b.ToString();
        }
    }
}
