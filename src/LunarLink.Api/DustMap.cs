using System;
using System.Reflection;
using System.Text;
using MoonCleaners;
using UnityEngine;

namespace LunarLink
{
    // Where the dust is (2026-11-08). Read-only, and only when asked - nothing here runs at load or per frame.
    //   /dust/regions   every region not complete: index, centre direction, dust left, dust it started with
    //   /dust/geometry  region and chunk-file centres, written to dust_geometry.json
    static class DustMap
    {
        public static string Regions(MoonWorld w)
        {
            var regions = w.Regions; if (regions == null) return "{\"ok\":false,\"reason\":\"no regions yet\"}";
            var centres = regions.Centers; int n = centres.Length;
            var sb = new StringBuilder("{\"ok\":true,\"count\":").Append(n).Append(",\"radius\":").Append(J.F(MoonRadius())).Append(",\"dusty\":[");
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                if (w.IsRegionComplete(i)) continue;
                var c = centres[i];
                if (k++ > 0) sb.Append(',');
                sb.Append('[').Append(i).Append(',').Append(J.F(c.x)).Append(',').Append(J.F(c.y)).Append(',').Append(J.F(c.z))
                  .Append(',').Append(J.F(w.RegionRemainingDust(i))).Append(',').Append(J.F(w.RegionInitialDust(i))).Append(']');
            }
            return sb.Append("],\"left\":").Append(k).Append('}').ToString();
        }

        // every region centre and the centre of each chunk file named in chunk_names.txt (next to the plugin), written to
        // dust_geometry.json there - for editing the save's dust offline. Read-only.
        public static string Geometry(MoonWorld w)
        {
            var regions = w.Regions; if (regions == null) return "{\"ok\":false,\"reason\":\"no regions yet\"}";
            var dir = Api.DataDir;
            var centres = regions.Centers; var sb = new StringBuilder("{\"centres\":[");
            for (int i = 0; i < centres.Length; i++) { var c = centres[i]; if (i > 0) sb.Append(','); sb.Append(J.F(c.x)).Append(',').Append(J.F(c.y)).Append(',').Append(J.F(c.z)); }
            sb.Append("],\"chunks\":{"); int ok = 0, bad = 0;
            var namesFile = System.IO.Path.Combine(dir, "chunk_names.txt");
            if (System.IO.File.Exists(namesFile))
                foreach (var raw in System.IO.File.ReadAllLines(namesFile))
                {
                    var name = raw.Trim(); if (name.Length == 0) continue;
                    var pt = Vector3.zero;
                    if (w.ChunkFileCentre(name, out pt)) { if (ok + bad > 0) sb.Append(','); sb.Append(J.S(name)).Append(':').Append(J.V(pt)); ok++; } else bad++;
                }
            sb.Append("},\"chunkCount\":").Append(w.ChunkCount).Append(",\"radius\":").Append(J.F(MoonRadius())).Append('}');
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "dust_geometry.json"), sb.ToString());
            return "{\"ok\":true,\"regions\":" + centres.Length + ",\"chunks\":" + ok + ",\"chunkMisses\":" + bad + ",\"chunkCount\":" + w.ChunkCount + "}";
        }

        static float MoonRadius()
        {
            try { foreach (var s in UnityEngine.Object.FindObjectsOfType<MoonCollector>()) return s.transform.position.magnitude; } catch { }
            return 2000f;
        }
    }
}
