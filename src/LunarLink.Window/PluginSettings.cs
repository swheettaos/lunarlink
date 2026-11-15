using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LunarLink
{
    // A small, deliberately conservative editor for the BepInEx config files
    // that Mod Manager exposes.  The new UI writes only discovered setting
    // lines, keeps a one-level backup, and leaves the game to apply the value
    // on the next restart.  That is safer than pretending every plugin reacts
    // to a file change while it is already running.
    static class PluginSettings
    {
        internal enum Kind { Toggle, Whole, Number, Choice, KeyName, Text }

        internal sealed class Entry
        {
            internal string Id, Path, Section, Key, Description, Value;
            internal Kind Type;
            internal string[] Options = Array.Empty<string>();
            internal double? Min, Max;
            internal int Line;
        }

        static readonly Regex SettingType = new Regex(@"^#\s*Setting type:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Range = new Regex(@"From\s+([^ ]+)\s+to\s+([^ ]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // each changed setting's value before this session changed it: something here waits for a restart
        static readonly Dictionary<string, string> originalValues = new Dictionary<string, string>(StringComparer.Ordinal);
        internal static bool Pending => originalValues.Count > 0;
        internal static bool Changed(Entry entry) => originalValues.ContainsKey(entry.Id);
        // a hot plugin reloaded: it read its config again, so these settings no longer wait for a restart
        internal static void Applied(IEnumerable<Entry> entries) { foreach (var e in entries) originalValues.Remove(e.Id); }

        static string ConfigDir => BepInEx.Paths.ConfigPath;

        internal static List<Entry> For(string name, string guid = null, string file = null)
        {
            var result = new List<Entry>();
            if (!Directory.Exists(ConfigDir)) return result;
            var candidates = Identifiers(name, guid, file);
            foreach (var path in Directory.GetFiles(ConfigDir, "*.cfg", SearchOption.TopDirectoryOnly))
            {
                var score = Score(Path.GetFileNameWithoutExtension(path), candidates);
                if (score < 35) continue;
                try
                {
                    result.AddRange(Read(path));
                }
                catch (Exception e) { Plugin.L.LogWarning("settings: could not read " + Path.GetFileName(path) + ": " + e.Message); }
            }
            return result.OrderBy(x => x.Section, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Line).ToList();
        }

        static HashSet<string> Identifiers(string name, string guid, string file)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in new[] { name, guid, file, file == null ? null : System.IO.Path.GetFileNameWithoutExtension(file) })
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                var raw = value.Trim();
                if (raw.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(0, raw.Length - 4);
                if (raw.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(0, raw.Length - 9);
                set.Add(raw);
                set.Add(Normalize(raw));
            }
            return set;
        }

        static string Normalize(string value)
        {
            var chars = value.Where(char.IsLetterOrDigit).ToArray();
            return new string(chars).ToLowerInvariant();
        }

        static int Score(string fileStem, HashSet<string> candidates)
        {
            var normalizedFile = Normalize(fileStem);
            var best = 0;
            foreach (var candidate in candidates)
            {
                if (string.Equals(fileStem, candidate, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, 100);
                var normalized = Normalize(candidate);
                if (normalized.Length == 0) continue;
                if (normalizedFile == normalized) best = Math.Max(best, 100);
                else if (normalized.Length >= 5 && normalizedFile.Contains(normalized)) best = Math.Max(best, 60);
            }
            return best;
        }

        static List<Entry> Read(string path)
        {
            var lines = File.ReadAllLines(path);
            var result = new List<Entry>();
            string section = "General", description = null, typeText = null;
            string[] options = Array.Empty<string>();
            double? min = null, max = null;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("[", StringComparison.Ordinal) && line.TrimEnd().EndsWith("]", StringComparison.Ordinal))
                {
                    section = line.Trim().Trim('[', ']');
                    description = null; typeText = null; options = Array.Empty<string>(); min = max = null;
                    continue;
                }
                var trimmed = line.Trim();
                var type = SettingType.Match(trimmed);
                if (type.Success) { typeText = type.Groups[1].Value.Trim(); continue; }
                if (trimmed.StartsWith("##", StringComparison.Ordinal))
                {
                    var text = trimmed.Substring(2).Trim();
                    if (text.Length > 0) description = string.IsNullOrEmpty(description) ? text : description + " " + text;
                    continue;
                }
                if (trimmed.StartsWith("# Acceptable values:", StringComparison.OrdinalIgnoreCase))
                {
                    options = trimmed.Substring(trimmed.IndexOf(':') + 1).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
                    continue;
                }
                if (trimmed.StartsWith("# Acceptable value range:", StringComparison.OrdinalIgnoreCase))
                {
                    var match = Range.Match(trimmed);
                    if (match.Success)
                    {
                        if (double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo)) min = lo;
                        if (double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hi)) max = hi;
                    }
                    continue;
                }
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal)) continue;
                var equals = line.IndexOf('=');
                if (equals <= 0) continue;
                var key = line.Substring(0, equals).Trim();
                var value = line.Substring(equals + 1).Trim();
                if (key.Length == 0) continue;
                var kind = Classify(typeText, options);
                if (kind == null) continue;
                var entry = new Entry
                {
                    Id = path + "|" + i,
                    Path = path,
                    Line = i,
                    Section = section,
                    Key = key,
                    Value = value,
                    Description = description,
                    Type = kind.Value,
                    Options = options,
                    Min = min,
                    Max = max
                };
                result.Add(entry);
                description = null; typeText = null; options = Array.Empty<string>(); min = max = null;
            }
            return result;
        }

        static Kind? Classify(string type, string[] options)
        {
            var t = (type ?? "").Trim();
            if (t.Equals("Boolean", StringComparison.OrdinalIgnoreCase) || t.Equals("Bool", StringComparison.OrdinalIgnoreCase)) return Kind.Toggle;
            if (t.Equals("KeyCode", StringComparison.OrdinalIgnoreCase) || t.Equals("Keyboard", StringComparison.OrdinalIgnoreCase)) return Kind.KeyName;
            if (t.IndexOf("Int", StringComparison.OrdinalIgnoreCase) >= 0 || t.Equals("Byte", StringComparison.OrdinalIgnoreCase)) return Kind.Whole;
            if (t.Equals("Single", StringComparison.OrdinalIgnoreCase) || t.Equals("Double", StringComparison.OrdinalIgnoreCase)
                || t.Equals("Decimal", StringComparison.OrdinalIgnoreCase)) return Kind.Number;
            if (options.Length > 0 || t.Equals("Enum", StringComparison.OrdinalIgnoreCase)) return Kind.Choice;
            if (t.Equals("String", StringComparison.OrdinalIgnoreCase) || t.Length == 0) return Kind.Text;
            return null;
        }

        internal static string Toggle(Entry entry)
        {
            var next = entry.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "false" : "true";
            return Set(entry, next);
        }

        internal static string Next(Entry entry)
        {
            if (entry.Options == null || entry.Options.Length == 0) return "No choices are available";
            var at = Array.FindIndex(entry.Options, x => string.Equals(x, entry.Value, StringComparison.OrdinalIgnoreCase));
            return Set(entry, entry.Options[(at + 1 + entry.Options.Length) % entry.Options.Length]);
        }

        internal static string Set(Entry entry, string value)
        {
            if (entry == null) return "setting not found";
            value = (value ?? "").Trim();
            if (!Valid(entry, value, out var error)) return error;
            try
            {
                var lines = File.ReadAllLines(entry.Path);
                if (entry.Line < 0 || entry.Line >= lines.Length) return "setting changed on disk; close and reopen the menu";
                var equals = lines[entry.Line].IndexOf('=');
                if (equals <= 0) return "setting line is no longer valid";
                var backup = entry.Path + ".lunarlink.bak";
                if (!File.Exists(backup)) File.Copy(entry.Path, backup, false);
                lines[entry.Line] = lines[entry.Line].Substring(0, equals + 1) + " " + value;
                var temp = entry.Path + ".lunarlink.new";
                File.WriteAllLines(temp, lines);
                File.Move(temp, entry.Path, true);
                if (!originalValues.ContainsKey(entry.Id)) originalValues[entry.Id] = entry.Value ?? "";
                entry.Value = value;
                if (string.Equals(originalValues[entry.Id], value, StringComparison.Ordinal)) originalValues.Remove(entry.Id);
                return "Changed " + entry.Key + " to " + value + ". Restart to apply.";
            }
            catch (Exception e)
            {
                try { if (File.Exists(entry.Path + ".lunarlink.new")) File.Delete(entry.Path + ".lunarlink.new"); }
                catch (System.Exception caught) { Quiet.Note(caught); }
                return "Could not save " + entry.Key + ": " + e.Message;
            }
        }

        static bool Valid(Entry entry, string value, out string error)
        {
            error = null;
            if (entry.Type == Kind.Toggle && !bool.TryParse(value, out _)) { error = "Use true or false"; return false; }
            if (entry.Type == Kind.Whole)
            {
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)) { error = "Use a whole number"; return false; }
                if (entry.Min.HasValue && whole < entry.Min.Value || entry.Max.HasValue
                    && whole > entry.Max.Value) { error = "That number is outside the allowed range"; return false; }
            }
            if (entry.Type == Kind.Number)
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) { error = "Use a number"; return false; }
                if (entry.Min.HasValue && number < entry.Min.Value || entry.Max.HasValue
                    && number > entry.Max.Value) { error = "That number is outside the allowed range"; return false; }
            }
            if ((entry.Type == Kind.Choice || entry.Type == Kind.KeyName) && entry.Options.Length > 0
                && !entry.Options.Any(x => string.Equals(x, value,
                    StringComparison.OrdinalIgnoreCase))) { error = "That is not an allowed choice"; return false; }
            if (value.Contains('\n') || value.Contains('\r')) { error = "A setting cannot contain a line break"; return false; }
            return true;
        }
    }
}
