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
            var regions = w.Regions; if (regions == null) return Api.Result(false, "no regions yet");
            var centres = regions.Centers; int n = centres.Length, k = 0;
            var o = new JsonOut().Obj().Bool("ok", true).Num("count", n).Num("radius", MoonRadius()).Arr("dusty");
            for (int i = 0; i < n; i++)
            {
                if (w.IsRegionComplete(i)) continue;
                var c = centres[i]; k++;
                o.Nums(i, c.x, c.y, c.z, w.RegionRemainingDust(i), w.RegionInitialDust(i));      // [index, x, y, z, left, started with]
            }
            return o.End().Num("left", k);
        }

        // every region centre and the centre of each chunk file named in chunk_names.txt (next to the plugin), written to
        // dust_geometry.json there - for editing the save's dust offline. Read-only.
        public static string Geometry(MoonWorld w)
        {
            var regions = w.Regions; if (regions == null) return Api.Result(false, "no regions yet");
            var dir = Api.DataDir;
            var centres = regions.Centers;
            var o = new JsonOut().Obj().Arr("centres");                  // flat: x, y, z of each region in turn
            for (int i = 0; i < centres.Length; i++) o.Num(centres[i].x).Num(centres[i].y).Num(centres[i].z);
            o.End().Obj("chunks"); int ok = 0, bad = 0;
            var namesFile = System.IO.Path.Combine(dir, "chunk_names.txt");
            if (System.IO.File.Exists(namesFile))
                foreach (var raw in System.IO.File.ReadAllLines(namesFile))
                {
                    var name = raw.Trim(); if (name.Length == 0) continue;
                    var pt = Vector3.zero;
                    if (w.ChunkFileCentre(name, out pt)) { o.Vec(name, pt); ok++; } else bad++;
                }
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "dust_geometry.json"), o.End().Num("chunkCount", w.ChunkCount).Num("radius", MoonRadius()));
            return new JsonOut().Obj().Bool("ok", true).Num("regions", centres.Length).Num("chunks", ok).Num("chunkMisses",
                bad).Num("chunkCount", w.ChunkCount);
        }

        static float MoonRadius()
        {
            try { foreach (var s in UnityEngine.Object.FindObjectsOfType<MoonCollector>()) return s.transform.position.magnitude;
                } catch (System.Exception caught) { Quiet.Note(caught); }
            return 2000f;
        }
    }
}
