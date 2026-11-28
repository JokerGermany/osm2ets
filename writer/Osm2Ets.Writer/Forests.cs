// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using TruckLib;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Wälder aus OSM (Zwischenformat: forests) als Bewuchs des Füllgeländes. Die Geländestreifen werden an
/// Waldrändern geteilt; je Streifenseite bis zu drei Bänder (From/To quer zum Streifen) mit einem
/// SCS-Vegetationssatz: Nadelwald „Spruce mix“, Laubwald „leaved“, sonst „ger - mixed forest“.
/// Frei bleiben Fahrbahnen samt Rand (<see cref="RoadClear"/>), Prefabs und Gebäude.
/// Aus: OSM2ETS_NO_FORESTS=1.
/// </summary>
public sealed class Forests
{
    public static bool Enabled = Environment.GetEnvironmentVariable("OSM2ETS_NO_FORESTS") != "1";
    public const float RoadClear = 8f;       // Abstand der Bäume vom Fahrbahnrand
    public const float PrefabClear = 12f;    // Abstand von Prefab-Spurpunkten
    public const float BuildingClear = 4f;
    public const float Density = 150f;       // TruckLib-Dichte (Vorgabe 400) – ungeprüft, wie dicht das wirkt
    public const float Band = 5f;            // Auflösung quer zum Streifen
    public static readonly Dictionary<string, string> Sets = new()
    {
        ["needleleaved"] = "v2_37", ["broadleaved"] = "v2_14", ["mixed"] = "v2_1ger",
    };
    public static int Bands, Pieces;

    private sealed record Poly(Vector2[] Outer, List<Vector2[]> Inner, string Leaf, Vector2 Min, Vector2 Max);
    private const float Cell = 100f;
    private readonly Dictionary<(int, int), List<Poly>> polys = [];
    private readonly Dictionary<(int, int), List<(Vector2 A, Vector2 B, float Half)>> segs = [];
    private readonly Dictionary<(int, int), List<Vector2>> lanes = [];
    private readonly List<(Vector2 C, Vector2 U, float HL, float HW)> houses = [];
    private readonly Dictionary<(int, int), List<int>> houseCells = [];

    private static IEnumerable<(int, int)> Cells(Vector2 min, Vector2 max)
    {
        for (int i = (int)MathF.Floor(min.X / Cell); i <= (int)MathF.Floor(max.X / Cell); i++)
            for (int j = (int)MathF.Floor(min.Y / Cell); j <= (int)MathF.Floor(max.Y / Cell); j++)
                yield return (i, j);
    }
    private static (int, int) Key(float x, float z) => ((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell));

    public int Count { get; private set; }

    public Forests(IntermediateFile input, List<RoadEntry> roads, IReadOnlyDictionary<RoadEntry, ResolvedStyle[]> styles,
                   IReadOnlyList<Vector2> prefabLanes)
    {
        foreach (var f in input.Forests)
        {
            if (f.Outer.Count < 3) continue;
            var o = f.Outer.Select(p => new Vector2((float)p[0], (float)p[1])).ToArray();
            var inner = f.Inner.Where(r => r.Count >= 3).Select(r => r.Select(p => new Vector2((float)p[0], (float)p[1])).ToArray()).ToList();
            var min = new Vector2(o.Min(p => p.X), o.Min(p => p.Y)); var max = new Vector2(o.Max(p => p.X), o.Max(p => p.Y));
            var poly = new Poly(o, inner, f.Leaf, min, max);
            foreach (var k in Cells(min, max)) { if (!polys.TryGetValue(k, out var l)) polys[k] = l = []; l.Add(poly); }
            Count++;
        }
        foreach (var r in roads)
        {
            if (!styles.TryGetValue(r, out var st) || r.Points.Count < 2) continue;
            for (int i = 0; i + 1 < r.Points.Count; i++)
            {
                var s = st[Math.Min(i, st.Length - 1)];
                var a = MapWriter.ToVector(r.Points[i]); var b = MapWriter.ToVector(r.Points[i + 1]);
                float half = s.Width / 2 + MathF.Abs(s.ModelCenter) + MathF.Abs(s.Offset) + RoadClear;
                var A = new Vector2(a.X, a.Z); var B = new Vector2(b.X, b.Z);
                foreach (var k in Cells(Vector2.Min(A, B) - new Vector2(half), Vector2.Max(A, B) + new Vector2(half)))
                { if (!segs.TryGetValue(k, out var l)) segs[k] = l = []; l.Add((A, B, half)); }
            }
        }
        foreach (var p in prefabLanes)
            foreach (var k in Cells(p - new Vector2(PrefabClear), p + new Vector2(PrefabClear)))
            { if (!lanes.TryGetValue(k, out var l)) lanes[k] = l = []; l.Add(p); }
        foreach (var b in input.Buildings)
        {
            float a = (float)(b.Angle * Math.PI / 180);
            var c = new Vector2((float)b.Center[0], (float)b.Center[1]);
            float hl = (float)b.Length / 2 + BuildingClear, hw = (float)b.Width / 2 + BuildingClear;
            houses.Add((c, new Vector2(MathF.Cos(a), MathF.Sin(a)), hl, hw));
            float r = MathF.Sqrt(hl * hl + hw * hw);
            foreach (var k in Cells(c - new Vector2(r), c + new Vector2(r)))
            { if (!houseCells.TryGetValue(k, out var l)) houseCells[k] = l = []; l.Add(houses.Count - 1); }
        }
    }

    private static bool InRing(Vector2[] r, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = r.Length - 1; i < r.Length; j = i++)
            if ((r[i].Y > p.Y) != (r[j].Y > p.Y) && p.X < (r[j].X - r[i].X) * (p.Y - r[i].Y) / (r[j].Y - r[i].Y) + r[i].X)
                inside = !inside;
        return inside;
    }

    /// <summary>Waldart an (x, z) oder null (kein Wald oder freizuhalten).</summary>
    public string? At(float x, float z)
    {
        var k = Key(x, z);
        if (!polys.TryGetValue(k, out var l)) return null;
        var p = new Vector2(x, z);
        string? leaf = null;
        foreach (var f in l)
        {
            if (p.X < f.Min.X || p.Y < f.Min.Y || p.X > f.Max.X || p.Y > f.Max.Y || !InRing(f.Outer, p)) continue;
            if (f.Inner.Any(r => InRing(r, p))) continue;
            leaf = f.Leaf; break;
        }
        if (leaf is null) return null;
        if (segs.TryGetValue(k, out var sl))
            foreach (var (a, b, half) in sl)
            {
                var ab = b - a;
                float t = Math.Clamp(Vector2.Dot(p - a, ab) / MathF.Max(ab.LengthSquared(), 1e-6f), 0, 1);
                if (Vector2.DistanceSquared(p, a + t * ab) < half * half) return null;
            }
        if (lanes.TryGetValue(k, out var ll))
            foreach (var q in ll) if (Vector2.DistanceSquared(p, q) < PrefabClear * PrefabClear) return null;
        if (houseCells.TryGetValue(k, out var hl))
            foreach (var i in hl)
            {
                var (c, u, a, w) = houses[i];
                var d = p - c;
                if (MathF.Abs(Vector2.Dot(d, u)) < a && MathF.Abs(u.X * d.Y - u.Y * d.X) < w) return null;
            }
        return leaf;
    }

    /// <summary>Kennung eines Querschnitts bei x (Streifen in Zeile z, Breiten l nach Norden, r nach Süden):
    /// je Seite 4 Abschnitte Wald ja/nein; Streifen werden geteilt, wo sie sich ändert.</summary>
    public int Signature(float x, float z, float l, float r)
    {
        int sig = 0;
        for (int k = 0; k < 4; k++)
        {
            if (At(x, z - (k + 0.5f) / 4 * l) is not null) sig |= 1 << k;
            if (At(x, z + (k + 0.5f) / 4 * r) is not null) sig |= 1 << (k + 4);
        }
        return sig;
    }

    /// <summary>Bewuchs für einen Füllstreifen von xa bis xb in Zeile z.</summary>
    public void Plant(Terrain t, float xa, float xb, float z, float l, float r)
    {
        bool any = false;
        any |= PlantSide(t.Left, xa, xb, z, l, -1);
        any |= PlantSide(t.Right, xa, xb, z, r, +1);
        if (any) Pieces++;
    }

    private bool PlantSide(TerrainSide side, float xa, float xb, float z, float size, float sign)
    {
        if (size < Band) return false;
        int n = (int)MathF.Ceiling(size / Band);
        var leafAt = new string?[n];
        var xs = new[] { xa + (xb - xa) * 0.2f, (xa + xb) / 2, xa + (xb - xa) * 0.8f };
        for (int i = 0; i < n; i++)
        {
            float d = (i + 0.5f) * size / n;
            var hits = xs.Select(x => At(x, z + sign * d)).Where(v => v is not null).ToList();
            leafAt[i] = hits.Count >= 2 ? hits.GroupBy(v => v).MaxBy(g => g.Count())!.Key : null;
        }
        // zusammenhängende Bänder, höchstens so viele wie Plätze
        var bands = new List<(int A, int B, string Leaf)>();
        for (int i = 0; i < n; i++)
        {
            if (leafAt[i] is null) continue;
            if (bands.Count > 0 && bands[^1].B == i && bands[^1].Leaf == leafAt[i]) bands[^1] = (bands[^1].A, i + 1, bands[^1].Leaf);
            else bands.Add((i, i + 1, leafAt[i]!));
        }
        while (bands.Count > side.Vegetation.Length)
        {
            // kleinste Lücke schließen
            int best = 0;
            for (int i = 1; i + 1 < bands.Count; i++) if (bands[i + 1].A - bands[i].B < bands[best + 1].A - bands[best].B) best = i;
            bands[best] = (bands[best].A, bands[best + 1].B, bands[best].Leaf);
            bands.RemoveAt(best + 1);
        }
        for (int k = 0; k < bands.Count; k++)
        {
            var v = side.Vegetation[k];
            v.Name = Sets.GetValueOrDefault(bands[k].Leaf) ?? Sets["mixed"];
            v.Density = Density;
            v.Scale = VegetationScale.Percent80to120;
            v.From = bands[k].A * size / n;
            v.To = bands[k].B * size / n;
            Bands++;
        }
        if (bands.Count > 0) side.VegetationCollision = true;
        return bands.Count > 0;
    }
}
