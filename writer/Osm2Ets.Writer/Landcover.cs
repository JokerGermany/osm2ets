// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using TruckLib;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Geländematerial: überall Gras („grass ger main“, darauf wächst die deutsche Detailvegetation – Grasbüschel
/// und Blumen), in Flächen der Bodenbedeckung aus OSM (Zwischenformat: landcover) eine passende Grasart bzw.
/// Erde. Gemalt wird je Quad des Straßen- und Füllgeländes nach seiner Mitte; in Überschneidungen gilt die
/// kleinere Fläche. Prefab-Gelände bekommt nur das Grundmaterial. Reihenfolge der Quads wie in
/// <see cref="Underpasses.Pave"/>: Spalte für Spalte, je Spalte die Reihen nach außen.
/// Aus: OSM2ETS_NO_LANDCOVER=1 (dann bleibt das bisherige Material „dirt“).
/// </summary>
public static class Landcover
{
    public static bool Enabled = Environment.GetEnvironmentVariable("OSM2ETS_NO_LANDCOVER") != "1";
    /// <summary>Grundmaterial überall (terrain_mat.34 „grass ger main“).</summary>
    public const string Default = "34";
    /// <summary>Material je Art (terrain_material.sii des Grundspiels; Auswahl nach Namen, Aussehen ungeprüft).</summary>
    public static readonly Dictionary<string, string> Materials = new()
    {
        ["lawn"] = "82",      // grass green main – gepflegtes Gras
        ["meadow"] = "77",    // grass flowers main – Wiese mit Blumen
        ["farmland"] = "86",  // grass yellowish main – Acker (Feldtexturen gibt es nur in DLCs)
        ["heath"] = "80",     // grass droughty main
        ["scrub"] = "24",     // dirty grass
        ["bare"] = "0",       // dirt
    };
    /// <summary>Bisheriges Material der Straßen-/Füll-/Prefab-Seiten (TruckLib-Vorgabe „dirt“).</summary>
    const string Dirt = "0";

    private sealed record Poly(Vector2[] Outer, List<Vector2[]> Inner, string Kind, float Area, Vector2 Min, Vector2 Max);

    public sealed record Result(int Sides, int Quads, Dictionary<string, int> ByKind, int PrefabSides);

    public sealed class Index
    {
        const float Cell = 100f;
        readonly Dictionary<(int, int), List<Poly>> cells = [];
        public int Count { get; }

        public Index(IEnumerable<LandcoverEntry> entries)
        {
            foreach (var e in entries)
            {
                if (e.Outer.Count < 3 || !Materials.ContainsKey(e.Kind)) continue;
                var o = e.Outer.Select(p => new Vector2((float)p[0], (float)p[1])).ToArray();
                var inner = e.Inner.Where(r => r.Count >= 3).Select(r => r.Select(p => new Vector2((float)p[0], (float)p[1])).ToArray()).ToList();
                float area = MathF.Abs(Signed(o)) - inner.Sum(r => MathF.Abs(Signed(r)));
                var min = new Vector2(o.Min(p => p.X), o.Min(p => p.Y)); var max = new Vector2(o.Max(p => p.X), o.Max(p => p.Y));
                var poly = new Poly(o, inner, e.Kind, area, min, max);
                for (int i = (int)MathF.Floor(min.X / Cell); i <= (int)MathF.Floor(max.X / Cell); i++)
                    for (int j = (int)MathF.Floor(min.Y / Cell); j <= (int)MathF.Floor(max.Y / Cell); j++)
                    {
                        if (!cells.TryGetValue((i, j), out var l)) cells[(i, j)] = l = [];
                        l.Add(poly);
                    }
                Count++;
            }
            foreach (var l in cells.Values) l.Sort((a, b) => a.Area.CompareTo(b.Area));   // kleinste zuerst
        }

        static float Signed(Vector2[] r)
        {
            float a = 0;
            for (int i = 0, j = r.Length - 1; i < r.Length; j = i++) a += (r[j].X - r[i].X) * (r[j].Y + r[i].Y);
            return a / 2;
        }

        static bool InRing(Vector2[] r, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = r.Length - 1; i < r.Length; j = i++)
                if ((r[i].Y > p.Y) != (r[j].Y > p.Y) && p.X < (r[j].X - r[i].X) * (p.Y - r[i].Y) / (r[j].Y - r[i].Y) + r[i].X)
                    inside = !inside;
            return inside;
        }

        /// <summary>Art der kleinsten Fläche, die p enthält, oder null.</summary>
        public string? KindAt(Vector2 p)
        {
            if (!cells.TryGetValue(((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell)), out var l)) return null;
            foreach (var f in l)
            {
                if (p.X < f.Min.X || p.Y < f.Min.Y || p.X > f.Max.X || p.Y > f.Max.Y || !InRing(f.Outer, p)) continue;
                if (f.Inner.Any(r => InRing(r, p))) continue;
                return f.Kind;
            }
            return null;
        }
    }

    /// <summary>Grundmaterial setzen und die Quads aller Straßen- und Füllgelände-Seiten nach der Bodenbedeckung
    /// bemalen. Nach dem Formen des Geländes aufrufen (die Quad-Ecken stehen dann in den Offsets).</summary>
    public static Result Apply(Map map, IntermediateFile input)
    {
        var ix = new Index(input.Landcover);
        int sides = 0, quads = 0, prefabSides = 0;
        var byKind = new Dictionary<string, int>();
        foreach (var it in map.MapItems.Values)
        {
            switch (it)
            {
                case Road r:
                    foreach (var side in new[] { r.Left.Terrain, r.Right.Terrain })
                        if (Paint(side, r.Node.Position, ix, byKind, ref quads)) sides++;
                    break;
                case Terrain t:
                    foreach (var side in new[] { t.Left.Terrain, t.Right.Terrain })
                        if (Paint(side, t.Node.Position, ix, byKind, ref quads)) sides++;
                    break;
                case Prefab p:
                    foreach (var n in p.PrefabNodes)
                        if (n.Terrain is { } pt && pt.QuadData.BrushMaterials.Count > 0 && pt.QuadData.BrushMaterials[0].Name == new Token(Dirt))
                        {
                            pt.QuadData.BrushMaterials[0] = new Material(Default);
                            prefabSides++;
                        }
                    break;
            }
        }
        return new Result(sides, quads, byKind, prefabSides);
    }

    static bool Paint(RoadTerrain side, Vector3 origin, Index ix, Dictionary<string, int> byKind, ref int painted)
    {
        var q = side.QuadData;
        if (side.Size <= 0 || q.BrushMaterials.Count == 0) return false;
        if (q.BrushMaterials[0].Name == new Token(Dirt)) q.BrushMaterials[0] = new Material(Default);
        if (ix.Count == 0 || q.Offsets.Count == 0 || q.Quads.Count != q.Cols * q.Rows) return true;
        var at = new Dictionary<(int, int), Vector3>(q.Offsets.Count);
        foreach (var o in q.Offsets) at[(o.X, o.Y)] = origin + o.Data;       // Weltachsen ab Anfangsknoten
        for (int i = 0; i < q.Cols; i++)
            for (int j = 0; j < q.Rows; j++)
            {
                if (!at.TryGetValue((i, j), out var p00) || !at.TryGetValue((i + 1, j), out var p10) ||
                    !at.TryGetValue((i, j + 1), out var p01) || !at.TryGetValue((i + 1, j + 1), out var p11)) continue;
                var c = (p00 + p10 + p01 + p11) / 4;
                var kind = ix.KindAt(new Vector2(c.X, c.Z));
                if (kind is null) continue;
                int k = i * q.Rows + j;
                var quad = q.Quads[k];
                if (quad.MainMaterial != 0) continue;       // schon bemalt (z. B. Stein unter Brücken)
                var tok = new Token(Materials[kind]);
                int idx = q.BrushMaterials.FindIndex(m => m.Name == tok);
                if (idx < 0)
                {
                    if (q.BrushMaterials.Count >= 16) continue;
                    q.BrushMaterials.Add(new Material(tok));
                    idx = q.BrushMaterials.Count - 1;
                }
                if (idx == 0) continue;
                quad.MainMaterial = (Nibble)idx;
                q.Quads[k] = quad;
                painted++;
                byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
            }
        return true;
    }
}
