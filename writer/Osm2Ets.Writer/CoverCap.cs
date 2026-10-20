// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Obergrenze für das Füllgelände aus den tatsächlich geschriebenen Fahrbahnen und dem
/// Straßengelände (statt aus einer festen Zone neben jeder Straße).
///
/// Bisher lag das Füllgelände bis <see cref="RoadCap.FlatReach"/> (17 m) neben jeder Fahrbahn
/// flach auf Fahrbahnhöhe – gedacht für Stellen, an denen das 60 m breite Straßengelände
/// darüber liegt. Wo das Straßengelände schmaler ist (Brückenwiderlager, Zwickel, Kurven-
/// innenseiten), stieg es mit dem Raster an und endete einige Meter ÜBER dem flachen
/// Füllgelände: an der Rampe unter der BT 46 (A 9) bis 5 m, kartenweit 157 Kanten über 1 m.
///
/// Jetzt:
/// <list type="bullet">
/// <item>unter Asphalt höchstens <see cref="AsphaltMargin"/>, unter Straßengelände höchstens
/// <see cref="TerrainMargin"/> unter dessen Oberfläche (beide Dreiecksteilungen der Quads),</item>
/// <item>daneben zwischen zwei Böschungen mit Steigung <see cref="Slope"/> ab dem nächsten Rand,
/// nach oben und nach unten (keine Wand und kein Spalt, wo das Straßengelände über oder unter
/// der Rasterhöhe endet),</item>
/// <item>nach dem Setzen der groben Füllquads (bis 16 m) wird jedes Quad gegen die Oberfläche
/// darüber geprüft und, wo es durchstößt, abgesenkt (<see cref="Settle"/>).</item>
/// </list>
/// Brücken und Tunnel zählen nicht (darunter bzw. darüber bleibt das Raster).
/// </summary>
public sealed class CoverCap
{
    public static bool Enabled = Environment.GetEnvironmentVariable("OSM2ETS_FILL_OLDCAP") is null;
    public const float AsphaltMargin = 0.30f, TerrainMargin = 0.10f;
    /// <summary>
    /// Durchstoßungen bis hierhin bleiben (das Füllgelände liegt dann noch mindestens
    /// TerrainMargin − SettleTol unter dem Straßengelände). Grob- und Straßengelände folgen dem
    /// Raster mit verschiedenen Stützstellen; schon das ergibt einige Zentimeter Abweichung, und
    /// jede Absenkung dafür setzte das Füllgelände am Rand des Straßengeländes sichtbar tiefer.
    /// </summary>
    public static float SettleTol = float.TryParse(Environment.GetEnvironmentVariable("OSM2ETS_SETTLE_TOL"), System.Globalization.CultureInfo.InvariantCulture, out var st) ? st : 0.05f;
    public static float Slope = RoadCap.Slope;
    public const float ConeReach = 15f;
    private const float Cell = 4f, SrcCell = 5f;

    // Quad mit erlaubter Höhe je Ecke (Oberfläche − Rand); Ecken a, b, c, d im Umlauf
    private readonly List<(Vector3 A, Vector3 B, Vector3 C, Vector3 D)> quads = [];
    private readonly Dictionary<(int, int), List<int>> qIndex = [];
    private readonly List<Vector3> src = [];
    /// <summary>Je Quelle: Höhe des Randes für die Böschung nach unten (Oberfläche − TerrainMargin).</summary>
    private readonly List<float> srcFloor = [];
    private readonly Dictionary<(int, int), (List<int> Idx, float Min, float Max)> sIndex = [];

    public int QuadCount => quads.Count;
    public int SourceCount => src.Count;
    public static int ConeLowered, ConeRaised, CoverLowered, Settled, SettledQuads;
    public static List<(float Ex, float X, float Z)>? Debug = Environment.GetEnvironmentVariable("OSM2ETS_SETTLE_DEBUG") is not null ? [] : null;
    public static float SettledMax;

    private void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        int k = quads.Count;
        quads.Add((a, b, c, d));
        float x0 = MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X)), x1 = MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X));
        float z0 = MathF.Min(MathF.Min(a.Z, b.Z), MathF.Min(c.Z, d.Z)), z1 = MathF.Max(MathF.Max(a.Z, b.Z), MathF.Max(c.Z, d.Z));
        for (int i = (int)MathF.Floor(x0 / Cell); i <= (int)MathF.Floor(x1 / Cell); i++)
            for (int j = (int)MathF.Floor(z0 / Cell); j <= (int)MathF.Floor(z1 / Cell); j++)
            {
                if (!qIndex.TryGetValue((i, j), out var l)) qIndex[(i, j)] = l = [];
                l.Add(k);
            }
    }

    private void AddSource(Vector3 p, float margin)
    {
        int k = src.Count;
        src.Add(p);
        float fl = p.Y + margin - TerrainMargin;
        srcFloor.Add(fl);
        var key = ((int)MathF.Floor(p.X / SrcCell), (int)MathF.Floor(p.Z / SrcCell));
        if (!sIndex.TryGetValue(key, out var e)) e = ([], float.PositiveInfinity, float.NegativeInfinity);
        e.Idx.Add(k);
        sIndex[key] = (e.Idx, MathF.Min(e.Min, p.Y), MathF.Max(e.Max, fl));
    }

    /// <summary>
    /// Aus den geschriebenen Straßenstücken (nach dem Formen des Straßengeländes).
    /// </summary>
    public static CoverCap Build(List<RoadEntry> roads, Dictionary<RoadEntry, Road> firstItem, Dictionary<RoadEntry, Road> lastItem,
                                 Dictionary<RoadEntry, ResolvedStyle[]> styles)
    {
        var c = new CoverCap();
        foreach (var entry in roads)
        {
            if (!firstItem.TryGetValue(entry, out var it) || !styles.TryGetValue(entry, out var st)) continue;
            var chain = new List<(Road Rd, bool Skip, int Seg)>();
            int seg = 0;
            for (var rd = it; rd is not null; rd = rd.ForwardNode.ForwardItem as Road, seg++)
            {
                var run = entry.Runs.FirstOrDefault(x => x.From <= seg && seg < x.To);
                chain.Add((rd, run is null || run.Bridge || run.Tunnel, seg));
                if (rd == lastItem[entry]) break;
            }
            for (int k = 0; k < chain.Count; k++)
            {
                var (rd, skip, sg) = chain[k];
                if (skip) continue;
                var (eL, eR) = TerrainClearance.Edges(st[Math.Min(sg, st.Length - 1)]);
                // offenes Ende: Straßenende, Prefab oder Brücke/Tunnel dahinter
                bool openStart = k == 0 || chain[k - 1].Skip || rd.Node.BackwardItem is not Road;
                bool openEnd = k == chain.Count - 1 || chain[k + 1].Skip || rd.ForwardNode.ForwardItem is not Road;
                c.AddRoad(rd, eL, eR, openStart, openEnd);
            }
        }
        return c;
    }

    private void AddRoad(Road rd, float eL, float eR, bool openStart, bool openEnd)
    {
        var pa = rd.Node.Position; var pb = rd.ForwardNode.Position;
        float len3 = Vector3.Distance(pa, pb);
        if (len3 < 1e-3f) return;
        Vector3 Dir3(System.Numerics.Quaternion q)
        {
            var v = Vector3.Transform(-Vector3.UnitZ, q);
            return Vector3.Dot(v, pb - pa) < 0 ? -v : v;
        }
        var dA = Dir3(rd.Node.Rotation); var dB = Dir3(rd.ForwardNode.Rotation);
        Vector3 C3(float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * pa + (t3 - 2 * t2 + t) * len3 * dA + (-2 * t3 + 3 * t2) * pb + (t3 - t2) * len3 * dB;
        }
        Vector2 N(float t)
        {
            float t2 = t * t;
            var d = (6 * t2 - 6 * t) * pa + (3 * t2 - 4 * t + 1) * len3 * dA + (-6 * t2 + 6 * t) * pb + (3 * t2 - 2 * t) * len3 * dB;
            var d2 = new Vector2(d.X, d.Z);
            d2 = d2.LengthSquared() > 1e-8f ? Vector2.Normalize(d2) : Vector2.Normalize(new Vector2(pb.X - pa.X, pb.Z - pa.Z));
            return new Vector2(-d2.Y, d2.X);   // rechts
        }
        Vector3 Edge(float t, float off, float drop)
        {
            var p = C3(t); var n = N(t);
            return new Vector3(p.X + n.X * off, p.Y - drop, p.Z + n.Y * off);
        }
        // Asphalt
        int steps = Math.Max(2, (int)MathF.Ceiling(len3 / 2f));
        for (int k = 0; k < steps; k++)
        {
            float t0 = (float)k / steps, t1 = (float)(k + 1) / steps;
            AddQuad(Edge(t0, -eL, AsphaltMargin), Edge(t0, eR, AsphaltMargin), Edge(t1, eR, AsphaltMargin), Edge(t1, -eL, AsphaltMargin));
        }
        int srcSteps = Math.Max(2, (int)MathF.Ceiling(len3 / 1f));
        for (int k = 0; k <= srcSteps; k++)
        {
            float t = (float)k / srcSteps;
            AddSource(Edge(t, -eL, AsphaltMargin), AsphaltMargin); AddSource(Edge(t, eR, AsphaltMargin), AsphaltMargin);
        }
        foreach (var (open, t) in new[] { (openStart, 0f), (openEnd, 1f) })
        {
            if (!open) continue;
            int m = Math.Max(1, (int)MathF.Ceiling((eL + eR) / 1f));
            for (int k = 1; k < m; k++) AddSource(Edge(t, -eL + (eL + eR) * k / m, AsphaltMargin), AsphaltMargin);
        }
        // Straßengelände beider Seiten (Reihe 0 setzt das Spiel an den Modellrand)
        foreach (var (side, sign, edge) in new[] { (rd.Left.Terrain, -1f, eL), (rd.Right.Terrain, 1f, eR) })
        {
            var q = side.QuadData;
            if (side.Size <= 0 || q.Cols < 1 || q.Rows < 1 || q.Offsets.Count == 0) continue;
            var v = new Dictionary<(int, int), Vector3>();
            foreach (var o in q.Offsets) v[(o.X, o.Y)] = pa + o.Data - new Vector3(0, TerrainMargin, 0);
            for (int i = 0; i <= q.Cols; i++) v[(i, 0)] = Edge((float)i / q.Cols, sign * edge, TerrainMargin);
            int rows = v.Keys.Max(k => k.Item2);
            for (int i = 0; i < q.Cols; i++)
                for (int j = 0; j < rows; j++)
                    if (v.TryGetValue((i, j), out var a) && v.TryGetValue((i + 1, j), out var b) &&
                        v.TryGetValue((i + 1, j + 1), out var cc) && v.TryGetValue((i, j + 1), out var d))
                        AddQuad(a, b, cc, d);
            // Kegelquellen: äußerste Reihe (je Spalte, dazwischen alle ~1 m) und die Endspalten
            for (int i = 0; i <= q.Cols; i++)
            {
                int jm = rows; while (jm > 0 && !v.ContainsKey((i, jm))) jm--;
                if (!v.TryGetValue((i, jm), out var p)) continue;
                AddSource(p, TerrainMargin);
                if (i < q.Cols)
                {
                    int jn = rows; while (jn > 0 && !v.ContainsKey((i + 1, jn))) jn--;
                    if (v.TryGetValue((i + 1, jn), out var p2))
                    {
                        int m = (int)MathF.Ceiling(Vector3.Distance(p, p2) / 1f);
                        for (int k = 1; k < m; k++) AddSource(Vector3.Lerp(p, p2, (float)k / m), TerrainMargin);
                    }
                }
            }
            foreach (var (open, i) in new[] { (openStart, 0), (openEnd, q.Cols) })
            {
                if (!open) continue;
                for (int j = 1; j <= rows; j++)
                    if (v.TryGetValue((i, j), out var p) && v.TryGetValue((i, j - 1), out var p0))
                    {
                        int m = (int)MathF.Ceiling(Vector3.Distance(p, p0) / 1f);
                        for (int k = 1; k <= m; k++) AddSource(Vector3.Lerp(p0, p, (float)k / m), TerrainMargin);
                    }
            }
        }
    }

    private static bool InTri(float x, float z, Vector3 a, Vector3 b, Vector3 c, out float h)
    {
        h = float.NaN;
        float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(d) < 1e-9f) return false;
        float w1 = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d;
        float w2 = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d;
        float w3 = 1 - w1 - w2;
        const float e = -1e-4f;
        if (w1 < e || w2 < e || w3 < e) return false;
        h = w1 * a.Y + w2 * b.Y + w3 * c.Y;
        return true;
    }

    /// <summary>
    /// Höchste erlaubte (geschriebene) Füllhöhe unter Asphalt/Straßengelände an (x, z);
    /// NaN, wo keins liegt. Je Quad zählt die tiefere der beiden Dreiecksteilungen.
    /// </summary>
    public float Allowed(float x, float z)
    {
        if (!qIndex.TryGetValue(((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell)), out var l)) return float.NaN;
        float best = float.NaN;
        foreach (var k in l)
        {
            var (a, b, c, d) = quads[k];
            float h1 = float.NaN, h2 = float.NaN;
            // Teilung 1: a-b-c / a-c-d, Teilung 2: a-b-d / b-c-d
            if (!InTri(x, z, a, b, c, out h1)) InTri(x, z, a, c, d, out h1);
            if (!InTri(x, z, a, b, d, out h2)) InTri(x, z, b, c, d, out h2);
            float h = float.IsNaN(h1) ? h2 : float.IsNaN(h2) ? h1 : MathF.Min(h1, h2);
            if (float.IsNaN(h)) continue;
            // oberste Fläche zählt (darunter ist das Füllgelände ohnehin verdeckt)
            if (float.IsNaN(best) || h > best) best = h;
        }
        return best;
    }

    /// <summary>
    /// Böschungen ab den Rändern von Fahrbahn und Straßengelände (höchstens ConeReach weit):
    /// Decke = min(erlaubte Randhöhe + Slope·Abstand), Boden = max(Randhöhe − Slope·Abstand).
    /// Liefert je den Abstand zur maßgebenden Quelle (für den Ausgleich, wenn Boden &gt; Decke).
    /// </summary>
    public (float Ceil, float CeilD, float Floor, float FloorD) Cones(float x, float z, float h)
    {
        float ceil = float.PositiveInfinity, ceilD = 0, floor = float.NegativeInfinity, floorD = 0;
        int ci = (int)MathF.Floor(x / SrcCell), cj = (int)MathF.Floor(z / SrcCell);
        int r = (int)MathF.Ceiling(ConeReach / SrcCell);
        for (int i = ci - r; i <= ci + r; i++)
            for (int j = cj - r; j <= cj + r; j++)
            {
                if (!sIndex.TryGetValue((i, j), out var e)) continue;
                float dx = MathF.Max(0, MathF.Max(i * SrcCell - x, x - (i + 1) * SrcCell));
                float dz = MathF.Max(0, MathF.Max(j * SrcCell - z, z - (j + 1) * SrcCell));
                float dmin = MathF.Sqrt(dx * dx + dz * dz);
                if (dmin > ConeReach) continue;
                bool needC = e.Min + Slope * dmin < MathF.Min(ceil, h);
                bool needF = e.Max - Slope * dmin > MathF.Max(floor, h);
                if (!needC && !needF) continue;
                foreach (var k in e.Idx)
                {
                    var p = src[k];
                    float d = MathF.Sqrt((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z));
                    if (d > ConeReach) continue;
                    float c = p.Y + Slope * d;
                    if (c < ceil) { ceil = c; ceilD = d; }
                    float f = srcFloor[k] - Slope * d;
                    if (f > floor) { floor = f; floorD = d; }
                }
            }
        return (ceil, ceilD, floor, floorD);
    }

    /// <summary>
    /// Geschriebene Füllhöhe an (x, z) begrenzen: unter Fahrbahn/Straßengelände deren erlaubte
    /// Höhe, sonst zwischen den Böschungen ab dem nächsten Rand (oben wie unten: endet das
    /// Straßengelände über oder unter der Rasterhöhe, schließt das Füllgelände mit höchstens
    /// Slope an – keine Kante, kein Spalt).
    /// </summary>
    public float Limit(float x, float z, float h)
    {
        float a = Allowed(x, z);
        if (!float.IsNaN(a))
        {
            // kleine Abweichungen (Straßengelände folgt dem Raster mit anderen Stützstellen)
            // bleiben – sonst läge das Füllgelände am Rand des Straßengeländes tiefer als dieses
            if (a < h - SettleTol) { CoverLowered++; return a; }
            return h;
        }
        var (c, cd, f, fd) = Cones(x, z, h);
        if (f > c)
        {
            // zwei Ränder verschiedener Höhe dichter als die Böschung erlaubt: linear zwischen beiden
            float hc = c - Slope * cd, hf = f + Slope * fd;
            float w = fd / MathF.Max(fd + cd, 1e-3f);
            ConeLowered++;
            return hf + (hc - hf) * w;
        }
        if (c < h) { ConeLowered++; return c; }
        if (f > h) { ConeRaised++; return f; }
        return h;
    }

    /// <summary>
    /// Füllquads, die die Oberfläche darüber durchstoßen, absenken: Stichproben je Quad (beide
    /// Dreiecksteilungen), Absenkung auf die Ecken nach ihrem Gewicht verteilt. Gleiche Ecken
    /// (gleiche Lage) in Nachbarstücken und beiden Seiten eines Stücks werden gemeinsam
    /// abgesenkt – keine Fugen.
    /// </summary>
    public void Settle(List<Terrain> fill)
    {
        Settled = 0; SettledQuads = 0; SettledMax = 0;
        var key = new Dictionary<(int, int), int>();
        var pos = new List<Vector3>();
        int Key(Vector3 p)
        {
            var k = ((int)MathF.Round(p.X * 50), (int)MathF.Round(p.Z * 50));
            if (!key.TryGetValue(k, out var i)) { i = pos.Count; key[k] = i; pos.Add(p); }
            else if (p.Y < pos[i].Y) pos[i] = p;   // dieselbe Lage: tiefere Höhe gilt für beide
            return i;
        }
        var qs = new List<(int A, int B, int C, int D)>();
        var refs = new List<(Terrain T, RoadTerrain S, int Off, int V)>();
        foreach (var t in fill)
        {
            var pa = t.Node.Position;
            foreach (var side in new[] { t.Left.Terrain, t.Right.Terrain })
            {
                var q = side.QuadData;
                var at = new Dictionary<(int, int), int>();
                for (int o = 0; o < q.Offsets.Count; o++)
                {
                    var off = q.Offsets[o];
                    int v = Key(pa + off.Data);
                    at[(off.X, off.Y)] = v;
                    refs.Add((t, side, o, v));
                }
                foreach (var ((i, j), v00) in at)
                    if (at.TryGetValue((i + 1, j), out var v10) && at.TryGetValue((i + 1, j + 1), out var v11) && at.TryGetValue((i, j + 1), out var v01))
                        qs.Add((v00, v10, v11, v01));
            }
        }
        var h = pos.Select(p => p.Y).ToArray();
        // Gewicht beim Absenken: verdeckt 1, sichtbar 0,1
        var vis = pos.Select(p => float.IsNaN(Allowed(p.X, p.Z)) ? 0.1f : 1f).ToArray();
        float Vis(int i) => vis[i];
        var touched = new bool[qs.Count];
        // Ecke → Quads: nach einer Absenkung nur die betroffenen Quads erneut prüfen
        var vq = new List<int>[pos.Count];
        for (int k = 0; k < qs.Count; k++)
            foreach (var v in new[] { qs[k].A, qs[k].B, qs[k].C, qs[k].D }) (vq[v] ??= []).Add(k);
        var dirty = new bool[qs.Count]; Array.Fill(dirty, true);
        var stamp = new int[quads.Count]; int stampId = 0;
        var cand = new List<int>();
        for (int pass = 0; pass < 8; pass++)
        {
            int changed = 0;
            var next = new bool[qs.Count];
            float tol = pass < 7 ? SettleTol : SettleTol - 0.005f;
            for (int k = 0; k < qs.Count; k++)
            {
                if (!dirty[k]) continue;
                var (ia, ib, ic, id) = qs[k];
                var a = pos[ia]; var b = pos[ib]; var c = pos[ic]; var d = pos[id];
                float x0 = MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X)), x1 = MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X));
                float z0 = MathF.Min(MathF.Min(a.Z, b.Z), MathF.Min(c.Z, d.Z)), z1 = MathF.Max(MathF.Max(a.Z, b.Z), MathF.Max(c.Z, d.Z));
                var A = a with { Y = h[ia] }; var B = b with { Y = h[ib] }; var Cc = c with { Y = h[ic] }; var D = d with { Y = h[id] };
                float fmax = MathF.Max(MathF.Max(A.Y, B.Y), MathF.Max(Cc.Y, D.Y));
                // Fahrbahn-/Geländequads darüber, die überhaupt durchstoßen werden könnten
                cand.Clear(); stampId++;
                for (int ci = (int)MathF.Floor(x0 / Cell); ci <= (int)MathF.Floor(x1 / Cell); ci++)
                    for (int cj = (int)MathF.Floor(z0 / Cell); cj <= (int)MathF.Floor(z1 / Cell); cj++)
                        if (qIndex.TryGetValue((ci, cj), out var l))
                            foreach (var qi in l)
                            {
                                if (stamp[qi] == stampId) continue;
                                stamp[qi] = stampId;
                                var K = quads[qi];
                                float kmin = MathF.Min(MathF.Min(K.A.Y, K.B.Y), MathF.Min(K.C.Y, K.D.Y));
                                if (fmax <= kmin + tol) continue;
                                float kx0 = MathF.Min(MathF.Min(K.A.X, K.B.X), MathF.Min(K.C.X, K.D.X)), kx1 = MathF.Max(MathF.Max(K.A.X, K.B.X), MathF.Max(K.C.X, K.D.X));
                                float kz0 = MathF.Min(MathF.Min(K.A.Z, K.B.Z), MathF.Min(K.C.Z, K.D.Z)), kz1 = MathF.Max(MathF.Max(K.A.Z, K.B.Z), MathF.Max(K.C.Z, K.D.Z));
                                if (kx1 < x0 || kx0 > x1 || kz1 < z0 || kz0 > z1) continue;
                                cand.Add(qi);
                            }
                if (cand.Count == 0) continue;
                float worst = 0; bool found = false; Vector3 ww = default; (int, int, int) wt = default;
                foreach (var (p, q, r, i1, i2, i3) in new[] { (A, B, Cc, ia, ib, ic), (A, Cc, D, ia, ic, id), (A, B, D, ia, ib, id), (B, Cc, D, ib, ic, id) })
                {
                    float den = (q.Z - r.Z) * (p.X - r.X) + (r.X - q.X) * (p.Z - r.Z);
                    if (MathF.Abs(den) < 1e-9f) continue;
                    // Höchstwert von (Füllfläche − erlaubte Höhe) liegt an einer Ecke der Schnittfläche:
                    // Ecken beider Flächen und Schnittpunkte der Kanten (inkl. Diagonalen des Quads)
                    void Eval(float x, float z)
                    {
                        float w1 = ((q.Z - r.Z) * (x - r.X) + (r.X - q.X) * (z - r.Z)) / den;
                        float w2 = ((r.Z - p.Z) * (x - r.X) + (p.X - r.X) * (z - r.Z)) / den;
                        float w3 = 1 - w1 - w2;
                        if (w1 < -1e-4f || w2 < -1e-4f || w3 < -1e-4f) return;
                        float fv = w1 * p.Y + w2 * q.Y + w3 * r.Y;
                        if (fv - tol <= worst) return;
                        float al = Allowed(x, z);
                        if (float.IsNaN(al)) return;
                        float ex = fv - al - tol;
                        if (ex > worst + 1e-4f) { worst = ex; found = true; ww = new Vector3(w1, w2, w3); wt = (i1, i2, i3); }
                    }
                    var tv = new[] { new Vector2(p.X, p.Z), new Vector2(q.X, q.Z), new Vector2(r.X, r.Z) };
                    foreach (var qi in cand)
                    {
                        var K = quads[qi];
                        var kv = new[] { new Vector2(K.A.X, K.A.Z), new Vector2(K.B.X, K.B.Z), new Vector2(K.C.X, K.C.Z), new Vector2(K.D.X, K.D.Z) };
                        foreach (var v in kv) Eval(v.X, v.Y);
                        if (Cross(kv[0], kv[2], kv[1], kv[3], out var m)) Eval(m.X, m.Y);
                        foreach (var v in tv) Eval(v.X, v.Y);
                        for (int e = 0; e < 3; e++)
                        {
                            var e0 = tv[e]; var e1 = tv[(e + 1) % 3];
                            for (int s = 0; s < 6; s++)
                            {
                                var (s0, s1) = s switch { 0 => (kv[0], kv[1]), 1 => (kv[1], kv[2]), 2 => (kv[2], kv[3]), 3 => (kv[3], kv[0]), 4 => (kv[0], kv[2]), _ => (kv[1], kv[3]) };
                                if (Cross(e0, e1, s0, s1, out var xp)) Eval(xp.X, xp.Y);
                            }
                        }
                    }
                }
                if (!found) continue;
                if (pass == 0 && Debug is not null) Debug.Add((worst, (a.X + c.X) / 2, (a.Z + c.Z) / 2));
                // Absenkung mit minimaler Summe der Quadrate: δk = ex·wk/Σw²; im letzten Durchgang gleichmäßig
                var low = new List<int>();
                if (pass < 7)
                {
                    // verdeckte Ecken (unter Fahrbahn/Straßengelände) tragen den Großteil – sichtbare
                    // Ecken daneben möglichst wenig (sonst Stufe am Rand des Straßengeländes)
                    float g1 = ww.X * Vis(wt.Item1), g2 = ww.Y * Vis(wt.Item2), g3 = ww.Z * Vis(wt.Item3);
                    float s2 = ww.X * g1 + ww.Y * g2 + ww.Z * g3;
                    if (s2 < 1e-6f) { g1 = ww.X; g2 = ww.Y; g3 = ww.Z; s2 = ww.X * ww.X + ww.Y * ww.Y + ww.Z * ww.Z; }
                    float e = worst + 0.005f;
                    h[wt.Item1] -= e * g1 / s2; h[wt.Item2] -= e * g2 / s2; h[wt.Item3] -= e * g3 / s2;
                    low.AddRange([wt.Item1, wt.Item2, wt.Item3]);
                }
                else { float e = worst + 0.005f; h[ia] -= e; h[ib] -= e; h[ic] -= e; h[id] -= e; low.AddRange([ia, ib, ic, id]); }
                foreach (var v in low) foreach (var qk in vq[v]) next[qk] = true;
                touched[k] = true; changed++;
            }
            dirty = next;
            if (changed == 0) break;
        }
        for (int k = 0; k < qs.Count; k++) if (touched[k]) SettledQuads++;
        if (Debug is { Count: > 0 })
        {
            foreach (var lim in new[] { 0.01f, 0.03f, 0.05f, 0.1f, 0.2f, 0.5f, 1f, 2f })
                Console.Error.WriteLine($"SETTLE Quads mit Durchstoß > {lim} m: {Debug.Count(d => d.Ex > lim)}");
            foreach (var d in Debug.OrderByDescending(d => d.Ex).Take(25)) Console.Error.WriteLine($"SETTLE {d.Ex:0.00} m bei ({d.X:0.0},{d.Z:0.0})");
        }
        foreach (var (t, side, o, v) in refs)
        {
            float dh = pos[v].Y - h[v];
            if (dh <= 1e-4f) continue;
            var off = side.QuadData.Offsets[o];
            float target = h[v] - t.Node.Position.Y;
            if (target < off.Data.Y - 1e-4f)
            {
                SettledMax = MathF.Max(SettledMax, off.Data.Y - target);
                side.QuadData.Offsets[o] = new VertexData { X = off.X, Y = off.Y, Data = off.Data with { Y = target } };
                Settled++;
            }
        }
    }

    /// <summary>Schnittpunkt der Strecken a0–a1 und b0–b1.</summary>
    private static bool Cross(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1, out Vector2 p)
    {
        p = default;
        var r = a1 - a0; var s = b1 - b0;
        float den = r.X * s.Y - r.Y * s.X;
        if (MathF.Abs(den) < 1e-9f) return false;
        var w = b0 - a0;
        float t = (w.X * s.Y - w.Y * s.X) / den, u = (w.X * r.Y - w.Y * r.X) / den;
        if (t < 0 || t > 1 || u < 0 || u > 1) return false;
        p = a0 + r * t;
        return true;
    }

    private bool AnyCover(float x0, float z0, float x1, float z1)
    {
        for (int i = (int)MathF.Floor(x0 / Cell); i <= (int)MathF.Floor(x1 / Cell); i++)
            for (int j = (int)MathF.Floor(z0 / Cell); j <= (int)MathF.Floor(z1 / Cell); j++)
                if (qIndex.ContainsKey((i, j))) return true;
        return false;
    }
}
