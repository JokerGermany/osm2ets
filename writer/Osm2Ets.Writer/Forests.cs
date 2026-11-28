// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using TruckLib;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Wälder als Waldwände aus 3D-Bäumen (Curve-Items mit Vegetationskurven des Grundspiels). Das Innere
/// eines Waldes bleibt leer; Wände stehen
/// <list type="bullet">
/// <item>an Waldrändern (Außen- und Innenringe), soweit höchstens <see cref="IntermediateFile.ForestNearM"/>
///   neben einer Straße der Karte (ohne Gleise),</item>
/// <item>beiderseits jeder Straße der Karte, die durch einen Wald führt (weiter als
///   <see cref="EdgeDepth"/> vom Waldrand entfernt – sonst übernimmt die Wand am Rand).</item>
/// </list>
/// Eine Wand besteht aus <see cref="RowOffsets"/>.Length parallelen Reihen: vorn Unterholz („BI N - roadside
/// forest undergrowth“), dahinter Bäume nach leaf_type. Jede Reihe ist eine Kette kurzer Kurven, deren Knoten
/// auf dem Gelände liegen (die Bäume einer Kurve stehen auf deren Linie). Frei bleiben Fahrbahnen samt Rand,
/// Prefab-Spuren und Gebäude. Baumreihen (natural=tree_row) werden als eine Reihe gesetzt.
/// Aus: OSM2ETS_NO_FORESTS=1.
/// </summary>
public static class Forests
{
    public static bool Enabled = Environment.GetEnvironmentVariable("OSM2ETS_NO_FORESTS") != "1";
    /// <summary>Abstand der ersten Reihe vom Fahrbahnrand (samt Modellversatz) – Straßen; Wege: <see cref="PathClear"/>.</summary>
    public const float RoadClear = 6f;
    public const float PathClear = 2f;
    public const float PrefabClear = 12f;
    public const float BuildingClear = 4f;
    /// <summary>Abstand der Stützpunkte längs einer Reihe.</summary>
    public const float Step = 5f;
    /// <summary>Reihen ab dem Waldrand (bzw. ab der Freihaltezone der Straße) nach innen; Reihe 0 = Unterholz.</summary>
    public static readonly float[] RowOffsets = [1.5f, 5f, 9.5f, 14f, 18.5f];
    public static float EdgeDepth => RowOffsets[^1] + 6f;
    /// <summary>Höchstens so lang wird eine Kurve, und höchstens so weit dürfen Bäume zwischen den Knoten über dem
    /// Gelände schweben bzw. darin versinken (Gerade zwischen den Knoten gegen das Gelände).</summary>
    public const float MaxPiece = 60f, MaxFloat = 0.3f, MaxSink = 1.0f;
    public const ushort ViewDistance = 500;

    /// <summary>Vegetationskurve (curve_model) und Variante.</summary>
    public readonly record struct Kind(string Model, string Variation);
    public static readonly Kind Undergrowth = new("3l02r", "1_line");    // BI N - roadside forest undergrowth crv
    static readonly Kind Spruce = new("3l02v", "1_line");                // BI spruce school tall (Fichte)
    static readonly Kind Fir = new("3l00j", "var1_line");                // silver firs 1 tall curve at (Tanne)
    static readonly Kind Beech = new("2d002", "alley1");                 // beech trees tall alley (Buche)
    static readonly Kind Medium = new("3l02s", "1_line");                // BI N - roadside forest medium (Birke/Ahorn)
    static readonly Kind Birch = new("3l00h", "var1_line");              // slim birches curve
    static readonly Kind Tall = new("3l02t", "1_line");                  // BI N - roadside forest tall (Birke/Lärche/Fichte)
    static readonly Kind Semidry = new("3l00e", "var1_line");            // sparse young trees semidry
    /// <summary>Baumreihen 1 … n je Blattwerk (Reihe 0 ist immer Unterholz).</summary>
    public static readonly Dictionary<string, Kind[]> Rows = new()
    {
        ["needleleaved"] = [Spruce, Fir, Spruce, Fir],
        ["broadleaved"] = [Beech, Medium, Birch, Beech],
        ["mixed"] = [Tall, Beech, Spruce, Tall],
        ["leafless"] = [Semidry, Semidry, Semidry, Semidry],
    };
    /// <summary>Baumreihe (natural=tree_row) je Blattwerk.</summary>
    public static readonly Dictionary<string, Kind> TreeRow = new()
    {
        ["needleleaved"] = Spruce, ["broadleaved"] = Beech, ["mixed"] = Tall, ["leafless"] = Semidry,
    };
    static Kind[] RowsFor(string leaf) => Rows.GetValueOrDefault(leaf) ?? Rows["mixed"];

    public sealed record Result(int Forests, double EdgeKm, double RoadKm, int Rows, int Items, int TreeRows, double TreeRowKm);

    private sealed record Poly(Vector2[] Outer, List<Vector2[]> Inner, string Leaf, Vector2 Min, Vector2 Max);

    /// <summary>Raumindex für Wälder, Ränder und freizuhaltende Flächen.</summary>
    public sealed class Index
    {
        const float Cell = 50f, NearCell = 20f;
        readonly Dictionary<(int, int), List<Poly>> polys = [];
        readonly Dictionary<(int, int), List<(Vector2 A, Vector2 B)>> edges = [];
        readonly Dictionary<(int, int), List<(Vector2 A, Vector2 B, float Half)>> segs = [];
        readonly Dictionary<(int, int), List<Vector2>> lanes = [];
        readonly List<(Vector2 C, Vector2 U, float HL, float HW)> houses = [];
        readonly Dictionary<(int, int), List<int>> houseCells = [];
        readonly HashSet<(int, int)> near = [];
        public int Count { get; private set; }

        static (int, int) Key(Vector2 p) => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell));
        static IEnumerable<(int, int)> Cells(Vector2 min, Vector2 max)
        {
            for (int i = (int)MathF.Floor(min.X / Cell); i <= (int)MathF.Floor(max.X / Cell); i++)
                for (int j = (int)MathF.Floor(min.Y / Cell); j <= (int)MathF.Floor(max.Y / Cell); j++)
                    yield return (i, j);
        }
        static void Put<T>(Dictionary<(int, int), List<T>> d, (int, int) k, T v)
        {
            if (!d.TryGetValue(k, out var l)) d[k] = l = [];
            l.Add(v);
        }

        public Index(IntermediateFile input, List<RoadEntry> roads, IReadOnlyDictionary<RoadEntry, ResolvedStyle[]> styles,
                     IReadOnlyList<Vector2> prefabLanes)
        {
            foreach (var f in input.Forests)
            {
                if (f.Outer.Count < 3) continue;
                var o = f.Outer.Select(V).ToArray();
                var inner = f.Inner.Where(r => r.Count >= 3).Select(r => r.Select(V).ToArray()).ToList();
                var min = new Vector2(o.Min(p => p.X), o.Min(p => p.Y));
                var max = new Vector2(o.Max(p => p.X), o.Max(p => p.Y));
                var poly = new Poly(o, inner, f.Leaf, min, max);
                foreach (var k in Cells(min, max)) Put(polys, k, poly);
                foreach (var ring in inner.Prepend(o))
                    for (int i = 0; i < ring.Length; i++)
                    {
                        var a = ring[i]; var b = ring[(i + 1) % ring.Length];
                        foreach (var k in Cells(Vector2.Min(a, b), Vector2.Max(a, b))) Put(edges, k, (a, b));
                    }
                Count++;
            }
            float nearM = (float)input.ForestNearM;
            int reach = (int)MathF.Ceiling(nearM / NearCell) + 1;
            foreach (var r in roads)
            {
                if (r.Role == "rail" || !styles.TryGetValue(r, out var st) || r.Points.Count < 2) continue;
                for (int i = 0; i + 1 < r.Points.Count; i++)
                {
                    var s = st[Math.Min(i, st.Length - 1)];
                    var a3 = MapWriter.ToVector(r.Points[i]); var b3 = MapWriter.ToVector(r.Points[i + 1]);
                    var a = new Vector2(a3.X, a3.Z); var b = new Vector2(b3.X, b3.Z);
                    float half = HalfClear(r, i, s);
                    foreach (var k in Cells(Vector2.Min(a, b) - new Vector2(half), Vector2.Max(a, b) + new Vector2(half)))
                        Put(segs, k, (a, b, half));
                    // Zellen (NearCell), deren Mitte höchstens nearM von der Achse entfernt ist
                    int n = Math.Max(1, (int)(Vector2.Distance(a, b) / 10));
                    for (int t = 0; t <= n; t++)
                    {
                        var q = Vector2.Lerp(a, b, t / (float)n);
                        int gx = (int)MathF.Floor(q.X / NearCell), gz = (int)MathF.Floor(q.Y / NearCell);
                        for (int di = -reach; di <= reach; di++)
                            for (int dj = -reach; dj <= reach; dj++)
                            {
                                var c = new Vector2((gx + di + 0.5f) * NearCell, (gz + dj + 0.5f) * NearCell);
                                if (Vector2.DistanceSquared(c, q) <= nearM * nearM) near.Add((gx + di, gz + dj));
                            }
                    }
                }
            }
            foreach (var p in prefabLanes)
                foreach (var k in Cells(p - new Vector2(PrefabClear), p + new Vector2(PrefabClear))) Put(lanes, k, p);
            foreach (var b in input.Buildings)
            {
                float ang = (float)(b.Angle * Math.PI / 180);
                var c = new Vector2((float)b.Center[0], (float)b.Center[1]);
                float hl = (float)b.Length / 2 + BuildingClear, hw = (float)b.Width / 2 + BuildingClear;
                houses.Add((c, new Vector2(MathF.Cos(ang), MathF.Sin(ang)), hl, hw));
                float rr = MathF.Sqrt(hl * hl + hw * hw);
                foreach (var k in Cells(c - new Vector2(rr), c + new Vector2(rr))) Put(houseCells, k, houses.Count - 1);
            }
        }

        static Vector2 V(double[] p) => new((float)p[0], (float)p[1]);

        /// <summary>Halbe Breite der Fahrbahn samt Modellversatz plus Freihaltezone.</summary>
        public static float HalfClear(RoadEntry r, int i, ResolvedStyle s)
        {
            float half = s.Width / 2 + MathF.Abs(s.ModelCenter) + MathF.Abs(s.Offset);
            var hw = r.Runs.FirstOrDefault(u => u.From <= i && i < u.To)?.Highway ?? "";
            return half + (IsPath(hw) ? PathClear : RoadClear);
        }
        public static bool IsPath(string hw) => hw is "footway" or "path" or "cycleway" or "steps" or "bridleway" or "pedestrian" or "track";

        static bool InRing(Vector2[] r, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = r.Length - 1; i < r.Length; j = i++)
                if ((r[i].Y > p.Y) != (r[j].Y > p.Y) && p.X < (r[j].X - r[i].X) * (p.Y - r[i].Y) / (r[j].Y - r[i].Y) + r[i].X)
                    inside = !inside;
            return inside;
        }

        /// <summary>Blattwerk des Waldes an p oder null.</summary>
        public string? Leaf(Vector2 p)
        {
            if (!polys.TryGetValue(Key(p), out var l)) return null;
            foreach (var f in l)
            {
                if (p.X < f.Min.X || p.Y < f.Min.Y || p.X > f.Max.X || p.Y > f.Max.Y || !InRing(f.Outer, p)) continue;
                if (f.Inner.Any(r => InRing(r, p))) continue;
                return f.Leaf;
            }
            return null;
        }

        /// <summary>Abstand zum nächsten Waldrand (höchstens <paramref name="max"/>).</summary>
        public float EdgeDistance(Vector2 p, float max)
        {
            float best = max;
            foreach (var k in Cells(p - new Vector2(max), p + new Vector2(max)))
                if (edges.TryGetValue(k, out var l))
                    foreach (var (a, b) in l)
                    {
                        var ab = b - a;
                        float t = Math.Clamp(Vector2.Dot(p - a, ab) / MathF.Max(ab.LengthSquared(), 1e-6f), 0, 1);
                        best = MathF.Min(best, Vector2.Distance(p, a + t * ab));
                    }
            return best;
        }

        public bool Near(Vector2 p) => near.Contains(((int)MathF.Floor(p.X / NearCell), (int)MathF.Floor(p.Y / NearCell)));

        /// <summary>Freizuhalten: Fahrbahn samt Rand, Prefab-Spuren, Gebäude.</summary>
        public bool Blocked(Vector2 p)
        {
            var k = Key(p);
            if (segs.TryGetValue(k, out var sl))
                foreach (var (a, b, half) in sl)
                {
                    var ab = b - a;
                    float t = Math.Clamp(Vector2.Dot(p - a, ab) / MathF.Max(ab.LengthSquared(), 1e-6f), 0, 1);
                    if (Vector2.DistanceSquared(p, a + t * ab) < half * half) return true;
                }
            if (lanes.TryGetValue(k, out var ll))
                foreach (var q in ll) if (Vector2.DistanceSquared(p, q) < PrefabClear * PrefabClear) return true;
            if (houseCells.TryGetValue(k, out var hl))
                foreach (var i in hl)
                {
                    var (c, u, a, w) = houses[i];
                    var d = p - c;
                    if (MathF.Abs(Vector2.Dot(d, u)) < a && MathF.Abs(u.X * d.Y - u.Y * d.X) < w) return true;
                }
            return false;
        }

        public IEnumerable<(Vector2[] Ring, string Leaf)> Rings()
        {
            foreach (var f in polys.Values.SelectMany(l => l).Distinct())
            {
                yield return (f.Outer, f.Leaf);
                foreach (var r in f.Inner) yield return (r, f.Leaf);
            }
        }
    }

    /// <summary>Punkte einer Linie alle <see cref="Step"/> m (geschlossen: zurück zum Anfang).</summary>
    public static List<(Vector2 P, Vector2 T)> Resample(IReadOnlyList<Vector2> pts, bool closed)
    {
        var line = closed ? pts.Append(pts[0]).ToList() : pts.ToList();
        var outp = new List<(Vector2, Vector2)>();
        float carry = 0;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var a = line[i]; var b = line[i + 1];
            float len = Vector2.Distance(a, b);
            if (len < 1e-3f) continue;
            var t = (b - a) / len;
            for (float s = carry; s < len; s += Step) outp.Add((a + t * s, t));
            carry = Step - (len - carry) % Step;
            if (carry >= Step) carry -= Step;
        }
        if (!closed && line.Count >= 2)
        {
            var a = line[^2]; var b = line[^1];
            if (Vector2.Distance(a, b) > 1e-3f) outp.Add((b, Vector2.Normalize(b - a)));
        }
        // Tangenten glätten (Mittel der Nachbarn), damit Ecken nicht springen
        var res = new List<(Vector2, Vector2)>(outp.Count);
        for (int i = 0; i < outp.Count; i++)
        {
            var tp = outp[Math.Max(0, i - 1)].Item2 + outp[i].Item2 + outp[Math.Min(outp.Count - 1, i + 1)].Item2;
            if (closed && outp.Count > 2) tp = outp[(i - 1 + outp.Count) % outp.Count].Item2 + outp[i].Item2 + outp[(i + 1) % outp.Count].Item2;
            res.Add((outp[i].Item1, tp.LengthSquared() > 1e-6f ? Vector2.Normalize(tp) : outp[i].Item2));
        }
        return res;
    }

    static Vector2 Left(Vector2 t) => new(-t.Y, t.X);

    public static Result Apply(Map map, IntermediateFile input, List<RoadEntry> roads,
                               IReadOnlyDictionary<RoadEntry, ResolvedStyle[]> styles, IReadOnlyList<Vector2> prefabLanes,
                               Func<float, float, float>? heightAt)
    {
        var ix = new Index(input, roads, styles, prefabLanes);
        int rowsN = 0, items = 0, treeRows = 0;
        double edgeM = 0, roadM = 0, treeRowM = 0;
        // Reihen: (Punkte, Art, Gewichtung für die Länge)
        void Emit(List<Vector2> all, Kind kind, string seed, ref double length)
        {
            // außerhalb des Geländerasters (keine Höhe) teilen
            var part = new List<Vector2>();
            int n = 0;
            foreach (var p in Clean(all).Append(new Vector2(float.NaN)))
            {
                if (!float.IsNaN(p.X) && (heightAt is null || float.IsFinite(heightAt(p.X, p.Y)))) { part.Add(p); continue; }
                if (part.Count >= 2)
                {
                    float len = 0;
                    for (int i = 0; i + 1 < part.Count; i++) len += Vector2.Distance(part[i], part[i + 1]);
                    if (len >= 8f)
                    {
                        items += Chain(map, part, kind, seed + "/" + n++, heightAt);
                        rowsN++;
                        length += len;
                    }
                }
                part = [];
            }
        }

        // 1) Waldränder
        int ringNo = 0;
        foreach (var (ring, leaf) in ix.Rings())
        {
            ringNo++;
            var samples = Resample(ring, closed: true);
            if (samples.Count < 3) continue;
            // Seite nach innen: Mehrheit der Proben, bei denen 2 m links im Wald liegen
            int leftIn = 0, rightIn = 0;
            for (int i = 0; i < samples.Count; i += Math.Max(1, samples.Count / 24))
            {
                var (p, t) = samples[i];
                if (ix.Leaf(p + Left(t) * 2f) is not null) leftIn++;
                if (ix.Leaf(p - Left(t) * 2f) is not null) rightIn++;
            }
            float side = leftIn >= rightIn ? 1 : -1;
            var kinds = RowsFor(leaf);
            for (int k = 0; k < RowOffsets.Length; k++)
            {
                float d = RowOffsets[k];
                var kind = k == 0 ? Undergrowth : kinds[(k - 1) % kinds.Length];
                var run = new List<Vector2>();
                double dummy = 0;
                void Flush(int i) { if (k == 1) Emit(run, kind, $"e{ringNo}/{k}/{i}", ref edgeM); else Emit(run, kind, $"e{ringNo}/{k}/{i}", ref dummy); run = []; }
                for (int i = 0; i < samples.Count; i++)
                {
                    var (p, t) = samples[i];
                    var q = p + Left(t) * (side * d);
                    bool ok = ix.Near(p) && ix.Leaf(q) is not null && ix.EdgeDistance(q, d) >= d - 1f && !ix.Blocked(q);
                    if (!ok) { Flush(i); continue; }
                    if (run.Count > 0 && Vector2.Distance(run[^1], q) > 3 * Step) Flush(i);
                    run.Add(q);
                }
                Flush(samples.Count);
            }
        }

        // 2) Straßen durch Wald: beiderseits, wo der Waldrand weiter als EdgeDepth entfernt ist
        foreach (var r in roads)
        {
            if (r.Role == "rail" || !styles.TryGetValue(r, out var st) || r.Points.Count < 2) continue;
            var pts = r.Points.Select(p => new Vector2((float)p[0], (float)p[2])).ToList();
            // Stützpunkte samt Index des Straßenstücks (für Breite und Art)
            var samples = new List<(Vector2 P, Vector2 T, int Seg)>();
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                float len = Vector2.Distance(pts[i], pts[i + 1]);
                if (len < 1e-3f) continue;
                var t = (pts[i + 1] - pts[i]) / len;
                for (float s = 0; s < len; s += Step) samples.Add((pts[i] + t * s, t, i));
            }
            if (samples.Count < 2) continue;
            foreach (float side in new[] { 1f, -1f })
                for (int k = 0; k < RowOffsets.Length; k++)
                {
                    var run = new List<Vector2>();
                    string? runLeaf = null;
                    double dummy = 0;
                    void Flush(int i)
                    {
                        var kinds = RowsFor(runLeaf ?? "mixed");
                        var kind = k == 0 ? Undergrowth : kinds[(k - 1) % kinds.Length];
                        if (k == 1) Emit(run, kind, $"r{r.Id}/{side}/{k}/{i}", ref roadM); else Emit(run, kind, $"r{r.Id}/{side}/{k}/{i}", ref dummy);
                        run = [];
                        runLeaf = null;
                    }
                    for (int i = 0; i < samples.Count; i++)
                    {
                        var (p, t, seg) = samples[i];
                        float half = Index.HalfClear(r, seg, st[Math.Min(seg, st.Length - 1)]);
                        var q = p + Left(t) * (side * (half + RowOffsets[k]));
                        var leaf = ix.Leaf(q);
                        bool ok = leaf is not null && ix.EdgeDistance(q, EdgeDepth) >= EdgeDepth && !ix.Blocked(q);
                        if (!ok) { Flush(i); continue; }
                        if (run.Count > 0 && (leaf != runLeaf || Vector2.Distance(run[^1], q) > 3 * Step)) Flush(i);
                        run.Add(q);
                        runLeaf = leaf;
                    }
                    Flush(samples.Count);
                }
        }

        // 3) Baumreihen
        foreach (var tr in input.TreeRows)
        {
            if (tr.Points.Count < 2) continue;
            var samples = Resample(tr.Points.Select(p => new Vector2((float)p[0], (float)p[1])).ToList(), closed: false);
            var kind = TreeRow.GetValueOrDefault(tr.Leaf, TreeRow["broadleaved"]);
            var run = new List<Vector2>();
            int before = rowsN;
            for (int i = 0; i < samples.Count; i++)
            {
                if (!ix.Blocked(samples[i].P)) { run.Add(samples[i].P); continue; }
                Emit(run, kind, $"t{tr.Osm}/{i}", ref treeRowM);
                run = [];
            }
            Emit(run, kind, $"t{tr.Osm}/end", ref treeRowM);
            if (rowsN > before) treeRows++;
        }
        return new Result(ix.Count, edgeM / 1000, roadM / 1000, rowsN, items, treeRows, treeRowM / 1000);
    }

    /// <summary>Punkte näher als 2 m am Vorgänger und Spitzen (Richtungswechsel über 100°, z. B. an spitzen Ecken nach
    /// dem Versatz) entfernen – sonst entstehen entartete Kurven.</summary>
    public static List<Vector2> Clean(List<Vector2> run)
    {
        var o = new List<Vector2>(run.Count);
        foreach (var p in run)
            if (o.Count == 0 || Vector2.Distance(o[^1], p) >= 2f) o.Add(p);
        bool changed = true;
        while (changed && o.Count >= 3)
        {
            changed = false;
            for (int i = 1; i + 1 < o.Count; i++)
            {
                var a = Vector2.Normalize(o[i] - o[i - 1]); var b = Vector2.Normalize(o[i + 1] - o[i]);
                if (Vector2.Dot(a, b) < -0.17f) { o.RemoveAt(i); changed = true; break; }
            }
            for (int i = 1; i < o.Count && !changed; i++)
                if (Vector2.Distance(o[i - 1], o[i]) < 2f) { o.RemoveAt(i); changed = true; }
        }
        return o;
    }

    static uint Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }

    /// <summary>Kurvenkette über die Punkte: Stücke so lang wie möglich (bis <see cref="MaxPiece"/>), solange die Gerade
    /// zwischen den Knoten höchstens <see cref="MaxFloat"/> über und <see cref="MaxSink"/> unter dem Gelände liegt.</summary>
    public static int Chain(Map map, List<Vector2> run, Kind kind, string seed, Func<float, float, float>? heightAt)
    {
        var h = run.Select(p => heightAt is null ? 0f : heightAt(p.X, p.Y) - 0.1f).ToArray();
        var idx = new List<int> { 0 };
        int start = 0;
        while (start < run.Count - 1)
        {
            int best = start + 1;
            float acc = Vector2.Distance(run[start], run[start + 1]);
            for (int end = start + 2; end < run.Count; end++)
            {
                acc += Vector2.Distance(run[end - 1], run[end]);
                if (acc > MaxPiece) break;
                bool ok = true;
                float tot = acc, s = 0;
                for (int i = start + 1; i < end && ok; i++)
                {
                    s += Vector2.Distance(run[i - 1], run[i]);
                    float lin = h[start] + (h[end] - h[start]) * s / tot;
                    if (lin - h[i] > MaxFloat || h[i] - lin > MaxSink) ok = false;
                }
                if (!ok) break;
                best = end;
            }
            idx.Add(best);
            start = best;
        }
        Vector3 P(int i) => new(run[i].X, h[i], run[i].Y);
        var first = Curve.Add(map, P(idx[0]), P(idx[1]), kind.Model);
        Setup(first, kind, seed, 0);
        var cur = first;
        for (int k = 2; k < idx.Count; k++)
        {
            cur = cur.Append(P(idx[k]), cloneSettings: true);
            cur.Subcurves[0].Seed = Hash(seed + "/" + k);
        }
        return idx.Count - 1;
    }

    static void Setup(Curve c, Kind kind, string seed, int k)
    {
        var sc = c.Subcurves[0];
        sc.CenterPartVariation = kind.Variation;
        sc.FirstPart = "";
        sc.LastPart = "";
        sc.Seed = Hash(seed + "/" + k);
        c.ViewDistance = ViewDistance;
        c.Collision = true;
        c.Shadows = true;
    }
}
