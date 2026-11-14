// LunarLink's own hot modules (src/LunarLink.<Module>) use the core's internals: the route table (Api), the JSON
// helpers (J), the journal (Pump), the log (Plugin.L), the hot loader (Hot) and the update slot (Updates).
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("LunarLink.Api")]
[assembly: InternalsVisibleTo("LunarLink.Perf")]
[assembly: InternalsVisibleTo("LunarLink.Window")]
[assembly: InternalsVisibleTo("LunarLink.Update")]
