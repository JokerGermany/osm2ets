// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;

namespace Osm2Ets.Writer;

/// <summary>
/// Obergrenze für das Füllgelände über Fahrbahnen. Das Geländeraster (10 m) und die groben
/// Füllquads (bis 16 m) bilden einen Einschnitt nicht ab: liegt eine Straße tiefer als ihre
/// Umgebung (z. B. die B 85 unter der A 70), läge das Füllgelände über ihr und deckte sie zu.
/// Innerhalb der Modellfläche (asymmetrisch, mit Modellmitte) plus <see cref="Margin"/> Meter
/// darf das Füllgelände höchstens auf Fahrbahnhöhe liegen, danach steigt die Grenze mit
/// <see cref="Slope"/> an. Unter Brücken: höchstens <see cref="DeckDrop"/> unter der Fahrbahn
/// (nur knapp, sonst Gruben unter kleinen Brücken und zwischen Zwillingsbrücken).
/// </summary>
public sealed class RoadCap
{
    public static float Margin = Env("OSM2ETS_CAP_MARGIN", 2f);
    public static float Slope = Env("OSM2ETS_CAP_SLOPE", 0.7f);
    public static float UnderDrop = Env("OSM2ETS_CAP_UNDER", 0.4f);
    /// <summary>
    /// Neben Straßen (nicht Prefabs) bleibt die Grenze so weit flach auf Fahrbahnhöhe. Die
    /// Füllquads sind bis 16 m lang; steigt die Grenze schon 2 m neben dem Rand an, liegt ein
    /// Eckpunkt einige Meter neben der Fahrbahn hoch und die Fläche zum nächsten Eckpunkt (unter
    /// der Fahrbahn) schneidet den Asphaltrand – auf der A 9 bis fast 1 m darüber. Sichtbar ist
    /// dort ohnehin das Straßengelände (60 m breit), das Füllgelände liegt darunter.
    /// </summary>
    public static float FlatReach = Env("OSM2ETS_CAP_FLAT", 17f);
    public const float DeckDrop = 0.3f, BridgeMargin = 0.5f, BridgeSlope = 0.5f, Cell = 25f, Reach = 12f, LaneHalf = 3f;
    /// <summary>Spurpunkte der Prefabs mit Höhe (vor dem Füllgelände gesetzt).</summary>
    public static List<Vector3>? PrefabLanes;

    private static float Env(string n, float d) =>
        float.TryParse(Environment.GetEnvironmentVariable(n), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;

    // Segment: Anfang/Ende, halbe Modellbreite, Modellmitte (rechts +), Absenkung, Brücke
    private readonly List<(Vector3 A, Vector3 B, float Hw, float Center, float Drop, bool Bridge)> segs = [];
    private readonly List<object?> owners = [];
    /// <summary>Je Segment: Anfang bzw. Ende ist das Ende der Straße (dahinter Prefab oder andere Straße).</summary>
    private readonly List<(bool A, bool B)> ends = [];
    private (bool A, bool B) addingEnds = (true, true);
    /// <summary>Unter dem Asphalt erst so weit innerhalb des Modellrands die volle Absenkung.</summary>
    public const float UnderTaper = 1f;
    private object? adding;
    private readonly Dictionary<(int, int), List<int>> index = [];

    public static RoadCap Build(List<RoadEntry> roads, Dictionary<RoadEntry, ResolvedStyle[]> styles,
                                List<(Vector3[] Nodes, float HalfWidth)>? prefabs, bool roadsToo = true)
    {
        var c = new RoadCap();
        foreach (var r in roads)
        {
            if (!styles.TryGetValue(r, out var st)) continue;
            for (int k = 0; k < r.Points.Count - 1; k++)
            {
                var run = r.Runs.FirstOrDefault(x => x.From <= k && k < x.To);
                if (run is null || run.Tunnel) continue;
                if (!roadsToo && !run.Bridge) continue;   // Fahrbahnen regelt dann CoverCap
                var s = st[Math.Min(k, st.Length - 1)];
                var a = MapWriter.ToVector(r.Points[k]); var b = MapWriter.ToVector(r.Points[k + 1]);
                c.adding = r;
                c.addingEnds = (k == 0, k == r.Points.Count - 2);
                c.Add(a, b, s.Width / 2, s.ModelCenter, run.Bridge ? DeckDrop : 0f, run.Bridge);
                c.adding = null;
                c.addingEnds = (true, true);
            }
        }
        // Prefabs: Spurpunkte (alle ~2 m) mit halber Spurbreite + Rand
        if (PrefabLanes is { } lanes)
            foreach (var p in lanes) c.Add(p, p + new Vector3(0.01f, 0, 0), LaneHalf, 0f, 0f, false);
        else if (prefabs is not null)
            foreach (var (nodes, hw) in prefabs)
            {
                var ctr = nodes.Aggregate(Vector3.Zero, (x, y) => x + y) / nodes.Length;
                foreach (var n in nodes) c.Add(n, ctr, hw, 0f, 0f, false);
            }
        return c;
    }

    private void Add(Vector3 a, Vector3 b, float hw, float center, float drop, bool bridge)
    {
        int i = segs.Count;
        segs.Add((a, b, hw, center, drop, bridge));
        owners.Add(adding);
        ends.Add(addingEnds);
        float pad = hw + MathF.Abs(center) + MathF.Max(Margin, FlatReach) + Reach;
        int x0 = (int)MathF.Floor((MathF.Min(a.X, b.X) - pad) / Cell), x1 = (int)MathF.Floor((MathF.Max(a.X, b.X) + pad) / Cell);
        int z0 = (int)MathF.Floor((MathF.Min(a.Z, b.Z) - pad) / Cell), z1 = (int)MathF.Floor((MathF.Max(a.Z, b.Z) + pad) / Cell);
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                if (!index.TryGetValue((x, z), out var l)) index[(x, z)] = l = [];
                l.Add(i);
            }
    }

    public static int Lowered;
    private static readonly Vector2? Probe = Environment.GetEnvironmentVariable("OSM2ETS_CAPPROBE") is { } e
        ? new Vector2(float.Parse(e.Split(',')[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(e.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture)) : null;

    /// <summary>Höchste erlaubte Geländehöhe (vor dem Absenken um FillDrop); +∞ fern von Straßen.</summary>
    public float At(float x, float z)
    {
        if (!index.TryGetValue(((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell)), out var l)) return float.PositiveInfinity;
        float cap = float.PositiveInfinity;
        var p = new Vector2(x, z);
        foreach (var i in l)
        {
            var (a, b, hw, center, drop, bridge) = segs[i];
            var a2 = new Vector2(a.X, a.Z); var ab = new Vector2(b.X, b.Z) - a2;
            float l2 = ab.LengthSquared();
            if (l2 < 1e-8f) continue;
            float u = Vector2.Dot(p - a2, ab) / l2;
            float uc = Math.Clamp(u, 0, 1);
            var dir = ab / MathF.Sqrt(l2);
            var right = new Vector2(-dir.Y, dir.X);
            // seitlicher Abstand vom Modellrand (< 0 = auf der Fahrbahn), längs: über das Segmentende hinaus
            float lat = MathF.Abs(Vector2.Dot(p - a2, right) - center) - hw;
            float lon = (u < 0 ? -u : u > 1 ? u - 1 : 0) * MathF.Sqrt(l2);
            float sd = lon > 0 ? MathF.Max(lat, lon) : lat;
            float outside = MathF.Max(0, sd);
            // hinter dem Ende der Straße (Prefab, andere Straße) kein breiter flacher Bereich –
            // dort gilt deren eigene Grenze; an inneren Knoten schon (sonst Kerbe in Kurven)
            bool pastEnd = (u < 0 && ends[i].A) || (u > 1 && ends[i].B);
            float y = a.Y + (b.Y - a.Y) * uc - drop;
            if (bridge)
            {
                // knapp unter dem Überbau, daneben sanft zurück aufs Raster (keine Stufe unter der Kante);
                // flach so weit wie bei Straßen – Brücken auf Geländehöhe (Bachdurchlass, OSM-Brücke
                // ohne Tal im Raster) bekämen sonst über die groben Füllquads Erde aufs Deck
                float beyondB = outside - (pastEnd ? BridgeMargin : MathF.Max(BridgeMargin, FlatReach));
                if (beyondB > Reach) continue;
                y += MathF.Max(0, beyondB) * BridgeSlope;
            }
            else
            {
                float flat = owners[i] is null || pastEnd ? Margin : MathF.Max(Margin, FlatReach);
                float beyond = outside - flat;
                if (beyond > Reach) continue;
                y += MathF.Max(0, beyond) * Slope;
                // nur UNTER dem Asphalt tiefer (Hermite-Mulden, grobe Füllquads); am Rand und daneben
                // nicht – dort wäre das Füllgelände sonst bis 0,55 m unter der Fahrbahn sichtbar
                // (Wand/Bankett-Seite der Straße zwischen zwei Fahrbahnen, v78)
                if (sd < 0) y -= UnderDrop * MathF.Min(1, -sd / UnderTaper);
            }
            if (Probe is { } pr && Vector2.Distance(pr, p) < 4f && y < 1e6f)
                Console.Error.WriteLine($"CAP at ({x:0.0},{z:0.0}): seg {a} -> {b} hw {hw:0.0} c {center:0.0} bridge {bridge} outside {outside:0.0} cap {y:0.00}");
            cap = MathF.Min(cap, y);
        }
        return cap;
    }

    public static int RoadTerrainLowered;

    /// <summary>
    /// Für das Straßengelände einer Straße: höchste Höhe über der Fahrbahn ANDERER Straßen und
    /// Prefabs (nur innerhalb ihrer Modellfläche, ohne Rand). Sonst ragt in spitzen Zwickeln
    /// (Gabelung zweier Rampen, Einfädelung) das Böschungsgelände der einen Straße über den
    /// Asphalt der anderen – im Editor als dunkler Keil. +∞, wenn keine andere Straße.
    /// </summary>
    public float OverOthers(float x, float z, object owner)
    {
        if (!index.TryGetValue(((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell)), out var l)) return float.PositiveInfinity;
        float cap = float.PositiveInfinity;
        var p = new Vector2(x, z);
        foreach (var i in l)
        {
            if (ReferenceEquals(owners[i], owner)) continue;
            var (a, b, hw, center, drop, _) = segs[i];
            var a2 = new Vector2(a.X, a.Z); var ab = new Vector2(b.X, b.Z) - a2;
            float l2 = ab.LengthSquared();
            if (l2 < 1e-8f) continue;
            float u = Vector2.Dot(p - a2, ab) / l2;
            if (u < 0 || u > 1) continue;
            var dir = ab / MathF.Sqrt(l2);
            var right = new Vector2(-dir.Y, dir.X);
            if (MathF.Abs(Vector2.Dot(p - a2, right) - center) > hw) continue;
            cap = MathF.Min(cap, a.Y + (b.Y - a.Y) * u - drop - 0.1f);
        }
        return cap;
    }

    /// <summary>
    /// Höhenunterschied, ab dem eine andere Straße für das Straßengelände kein Nachbar ist (Brücke
    /// darüber, Straße darunter). Sonst verdeckte an der BT 46 über der A 9 die Brücke im Grundriss
    /// die eigentliche Nachbarfahrbahn: im Zwickel Rampe/Hauptfahrbahn wechselten Spalten mit und
    /// ohne Rampe zur Nachbarfahrbahn – Zickzack-Falten im Gelände neben der Brücke.
    /// </summary>
    public const float NeighbourMaxDh = 5f;

    /// <summary>
    /// Nächste ANDERE Straße (bzw. Prefab-Spur): Abstand von ihrem Modellrand (höchstens maxD),
    /// ihre Fahrbahnhöhe an der nächsten Stelle und ob sie eine Brücke ist. Mit
    /// <paramref name="refY"/> zählen nur Straßen höchstens <see cref="NeighbourMaxDh"/> darüber
    /// oder darunter.
    /// </summary>
    public (float Dist, float Height, bool Bridge) NearestOther(float x, float z, object owner, float maxD, float refY = float.NaN)
    {
        if (!index.TryGetValue(((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell)), out var l)) return (maxD, float.NaN, false);
        float best = maxD, bh = float.NaN; bool bb = false;
        var p = new Vector2(x, z);
        foreach (var i in l)
        {
            if (ReferenceEquals(owners[i], owner)) continue;
            var (a, b, hw, center, _, bridge) = segs[i];
            var a2 = new Vector2(a.X, a.Z); var ab = new Vector2(b.X, b.Z) - a2;
            if (owners[i] is null)
            {
                // Prefab-Spurpunkt: Kreis wie in RayFree
                if (!float.IsNaN(refY) && MathF.Abs(a.Y - refY) > NeighbourMaxDh) continue;
                float dc = MathF.Max(0, Vector2.Distance(p, a2) - hw);
                if (dc < best) { best = dc; bh = a.Y; bb = false; }
                continue;
            }
            float l2 = ab.LengthSquared();
            if (l2 < 1e-8f) continue;
            float u = Vector2.Dot(p - a2, ab) / l2;
            float hy = a.Y + (b.Y - a.Y) * Math.Clamp(u, 0, 1);
            if (!float.IsNaN(refY) && MathF.Abs(hy - refY) > NeighbourMaxDh) continue;
            var dir = ab / MathF.Sqrt(l2);
            var right = new Vector2(-dir.Y, dir.X);
            float lat = MathF.Abs(Vector2.Dot(p - a2, right) - center) - hw;
            float lon = (u < 0 ? -u : u > 1 ? u - 1 : 0) * MathF.Sqrt(l2);
            float d = MathF.Max(0, MathF.Max(lat, lon));
            if (d < best) { best = d; bh = hy; bb = bridge; }
        }
        return (best, bh, bb);
    }

    /// <summary>
    /// Freie Strecke entlang eines Strahls (vom eigenen Modellrand nach außen) bis in das Modell
    /// einer ANDEREN Straße oder an eine Prefab-Spur, höchstens maxD. Brücken zählen nicht (Gelände
    /// läuft darunter durch). Stücke, die an [segA, segB] gerade
    /// anschließen (Fortsetzung über einen gemeinsamen Knoten), werden übergangen.
    /// </summary>
    public float RayFree(Vector2 o, Vector2 dir, float maxD, object owner, Vector2 segA, Vector2 segB)
    {
        float best = maxD;
        var seen = new HashSet<int>();
        var sdir = segB - segA;
        var sd = sdir.LengthSquared() > 1e-8f ? Vector2.Normalize(sdir) : Vector2.UnitX;
        for (float s = 0; ; s += Cell / 2)
        {
            var p = o + dir * MathF.Min(s, maxD);
            if (index.TryGetValue(((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell)), out var l))
                foreach (var i in l)
                {
                    if (!seen.Add(i) || ReferenceEquals(owners[i], owner)) continue;
                    var (a, b, hw, center, _, bridge) = segs[i];
                    if (bridge) continue;
                    if (owners[i] is null)
                    {
                        // Prefab-Spurpunkt: Kreis mit Radius hw (halbe Spur + Rand)
                        var c = new Vector2(a.X, a.Z) - o;
                        float tc = Vector2.Dot(c, dir);
                        float d2 = c.LengthSquared() - tc * tc;
                        if (d2 > hw * hw) continue;
                        float th = MathF.Sqrt(hw * hw - d2);
                        float te = tc - th, tx = tc + th;
                        if (tx < 0) continue;
                        best = MathF.Min(best, MathF.Max(0, te));
                        continue;
                    }
                    var a2 = new Vector2(a.X, a.Z); var ab = new Vector2(b.X, b.Z) - a2;
                    float len = ab.Length();
                    if (len < 1e-4f) continue;
                    var u = ab / len; var r = new Vector2(-u.Y, u.X);
                    var b2 = a2 + ab;
                    if ((Vector2.Distance(a2, segB) < 0.5f || Vector2.Distance(b2, segA) < 0.5f || Vector2.Distance(a2, segA) < 0.5f || Vector2.Distance(b2, segB) < 0.5f)
                        && MathF.Abs(Vector2.Dot(u, sd)) > 0.95f) continue;
                    // Strahl gegen das Rechteck u ∈ [0, len], v ∈ [center − hw, center + hw]
                    var w = o - a2;
                    float u0 = Vector2.Dot(w, u), du = Vector2.Dot(dir, u);
                    float v0 = Vector2.Dot(w, r), dv = Vector2.Dot(dir, r);
                    float t0 = 0, t1 = maxD;
                    if (!Slab(u0, du, 0, len, ref t0, ref t1)) continue;
                    if (!Slab(v0, dv, center - hw, center + hw, ref t0, ref t1)) continue;
                    if (t0 < best) best = t0;
                }
            if (s >= maxD) break;
        }
        return best;
    }

    private static bool Slab(float p0, float dp, float lo, float hi, ref float t0, ref float t1)
    {
        if (MathF.Abs(dp) < 1e-8f) return p0 >= lo && p0 <= hi;
        float ta = (lo - p0) / dp, tb = (hi - p0) / dp;
        if (ta > tb) (ta, tb) = (tb, ta);
        t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
        return t0 <= t1;
    }

    /// <summary>Abstand vom Modellrand der nächsten ANDEREN Straße (bzw. eines Prefabs), höchstens maxD.</summary>
    public float OtherDistance(float x, float z, object owner, float maxD)
    {
        if (!index.TryGetValue(((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell)), out var l)) return maxD;
        float best = maxD;
        var p = new Vector2(x, z);
        foreach (var i in l)
        {
            if (ReferenceEquals(owners[i], owner)) continue;
            var (a, b, hw, center, _, _) = segs[i];
            var a2 = new Vector2(a.X, a.Z); var ab = new Vector2(b.X, b.Z) - a2;
            float l2 = ab.LengthSquared();
            if (l2 < 1e-8f) continue;
            float u = Vector2.Dot(p - a2, ab) / l2;
            var dir = ab / MathF.Sqrt(l2);
            var right = new Vector2(-dir.Y, dir.X);
            float lat = MathF.Abs(Vector2.Dot(p - a2, right) - center) - hw;
            float lon = (u < 0 ? -u : u > 1 ? u - 1 : 0) * MathF.Sqrt(l2);
            best = MathF.Min(best, MathF.Max(0, MathF.Max(lat, lon)));
        }
        return best;
    }

    /// <summary>Gelände für das Füllgelände: Raster, über Fahrbahnen höchstens wie oben.</summary>
    public float Height(TerrainGridEntry grid, float x, float z)
    {
        float h = grid.HeightAt(x, z);
        if (float.IsNaN(h)) return h;
        {
            // Prefabs immer (sonst liegt das Gelände im Zwickel eines geneigten Prefabs bis
            // ~0,8 m unter dessen Fahrbahn – im Editor schwarze Seitenwand der Spur), Straßen
            // nur mit OSM2ETS_FILL_FLOOR
            float fl = Floor(x, z);
            if (fl > h) { Interlocked.Increment(ref Raised); h = fl; }
        }
        float c = At(x, z);
        if (c < h) { Interlocked.Increment(ref Lowered); return c; }
        return h;
    }

    /// <summary>Raster mit Prefab-Boden (erster Teil von <see cref="Height"/>).</summary>
    public float Base(TerrainGridEntry grid, float x, float z)
    {
        float h = grid.HeightAt(x, z);
        if (float.IsNaN(h)) return h;
        float fl = Floor(x, z);
        if (fl > h) { Interlocked.Increment(ref Raised); h = fl; }
        return h;
    }

    /// <summary>Decke (zweiter Teil von <see cref="Height"/>).</summary>
    public float Ceil(float x, float z, float h)
    {
        float c = At(x, z);
        if (c < h) { Interlocked.Increment(ref Lowered); return c; }
        return h;
    }

    public static int Raised;
    public static bool UseFloor = Environment.GetEnvironmentVariable("OSM2ETS_FILL_FLOOR") is not null;   // Standard aus: brachte im Zwickel kaum etwas, erzeugte aber Kanten

    /// <summary>
    /// Untergrenze für das Füllgelände: Bis <see cref="Margin"/> Meter neben dem Modellrand
    /// höchstens <see cref="FloorGap"/> unter der Fahrbahn, danach mit <see cref="Slope"/>
    /// fallend. Sonst liegt in schmalen Zwickeln (Gabelung, Prefab-Ende), wo das Straßengelände
    /// kaum Breite hat, das grobe Raster sichtbar unter dem Asphaltrand – im Editor ein
    /// dunkler Streifen. Brücken und Tunnel zählen nicht (darunter bleibt Bodenhöhe).
    /// </summary>
    public float Floor(float x, float z)
    {
        if (!index.TryGetValue(((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell)), out var l)) return float.NegativeInfinity;
        float fl = float.NegativeInfinity;
        var p = new Vector2(x, z);
        foreach (var i in l)
        {
            var (a, b, hw, center, drop, bridge) = segs[i];
            if (bridge) continue;
            bool prefab = owners[i] is null;
            if (!prefab && !UseFloor) continue;
            var a2 = new Vector2(a.X, a.Z); var ab = new Vector2(b.X, b.Z) - a2;
            float l2 = ab.LengthSquared();
            if (l2 < 1e-8f) continue;
            float u = Vector2.Dot(p - a2, ab) / l2;
            float uc = Math.Clamp(u, 0, 1);
            var dir = ab / MathF.Sqrt(l2);
            var right = new Vector2(-dir.Y, dir.X);
            float lat = MathF.Abs(Vector2.Dot(p - a2, right) - center) - hw;
            float lon = (u < 0 ? -u : u > 1 ? u - 1 : 0) * MathF.Sqrt(l2);
            float beyond = MathF.Max(0, MathF.Max(lat, lon)) - Margin;
            if (beyond > Reach) continue;
            float y = a.Y + (b.Y - a.Y) * uc - (prefab ? 0f : FloorGap) - MathF.Max(0, beyond) * Slope;
            fl = MathF.Max(fl, y);
        }
        return fl;
    }

    /// <summary>Füllgelände höchstens so tief unter der Fahrbahn (vor FillDrop).</summary>
    public const float FloorGap = 0.05f;
}
