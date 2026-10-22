// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Numerics;
using TruckLib;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Unterführungen wie in der SCS-Karte: Unter einer Brücke, die eine Straße des Netzes (oder ein
/// Prefab) überquert, liegt das Gelände auf Höhe der unteren Fahrbahn; von beiden Brückenenden
/// fällt es als Böschung ab (Neigung <see cref="Slope"/>, steiler, wenn der Platz bis zur unteren
/// Fahrbahn nicht reicht), seitlich über den Brückenrand hinaus als Kegel. Umgesetzt als
/// Obergrenze der Rasterhöhe (<see cref="TerrainGridEntry.Post"/>) – Straßengelände und
/// Füllgelände folgen ihr. Die Böschung unter der Brücke bekommt Steinmaterial
/// (<see cref="StoneMaterial"/>, wie die Terrain-Stücke unter den Brückenenden in der SCS-Karte).
/// Vorher stieg das Gelände unter solchen Brücken bis an die Fahrbahn (Brücke überschüttet),
/// die untere Straße lief durch einen Schlitz.
/// </summary>
public sealed class Underpasses
{
    public static bool Enabled = Environment.GetEnvironmentVariable("OSM2ETS_NO_UNDERPASS") is null;
    /// <summary>Höhenabstand Brücke – untere Fahrbahn, ab dem eine Kreuzung als Unterführung gilt (m).</summary>
    public const float MinClear = 3f;
    /// <summary>Böschungsneigung; 1 : 1,5 wie unter der SCS-Brücke (10 m Höhe auf 15 m).</summary>
    public static float Slope = float.TryParse(Environment.GetEnvironmentVariable("OSM2ETS_ABUT_SLOPE"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0.67f;
    /// <summary>Steiler wird die Böschung nicht (≈ 72°), auch wenn der Platz fehlt.</summary>
    public const float MaxSlope = 3f;
    /// <summary>Gelände so weit unter der unteren Fahrbahn (m).</summary>
    public const float FloorDrop = 0.15f;
    /// <summary>Flach neben dem Modellrand der unteren Straße (m).</summary>
    public const float Shoulder = 1.5f;
    /// <summary>Über den Modellrand der Brücke hinaus flach (m), danach Kegel.</summary>
    public const float SideMargin = 2f;
    /// <summary>Am Brückenende höchstens bis so weit unter die Fahrbahn (m).</summary>
    public const float DeckDrop = 0.3f;
    /// <summary>Steinmaterial der Böschung (Token aus der SCS-Karte, Terrain unter Brückenenden).</summary>
    public static string StoneMaterial = Environment.GetEnvironmentVariable("OSM2ETS_ABUT_MATERIAL") ?? "175";
    /// <summary>Quads, deren höchste Ecke so weit über dem Boden der Unterführung liegt, gelten als Böschung.</summary>
    public const float PaveAbove = 0.4f;

    private sealed class Span
    {
        public required string Id;
        public required Vector2[] P;
        public required float[] Y;
        public required float[] Cum;
        public float Left, Right;   // flache Breite links/rechts der Linie (Modellrand + SideMargin)
        public readonly List<(float S, float Y, float W)> Lower = [];
        public float SlopeA = Slope, SlopeB = Slope;
        public float Reach;          // seitliche Reichweite des Kegels über Left/Right hinaus
        public Vector2 Min, Max;
        public float Len => Cum[^1];
        /// <summary>Ebenerdige Abschnitte in der Nähe (auch die eigene Straße vor/hinter der Brücke):
        /// ihr Damm bleibt stehen – die Grenze steigt neben ihnen mit der Böschung an.</summary>
        public readonly List<(Vector2 A, Vector2 B, float YA, float YB, float Hw)> Support = [];
    }

    private readonly List<Span> spans = [];
    public int Count => spans.Count;
    public IEnumerable<string> Ids => spans.Select(x => x.Id);
    public int Paved { get; private set; }

    public static Underpasses Build(List<RoadEntry> roads, Dictionary<RoadEntry, ResolvedStyle[]> styles, List<Vector3> prefabLanes)
    {
        var u = new Underpasses();
        var segs = new List<(RoadEntry R, Vector3 A, Vector3 B, float Hw, bool Bridge)>();
        var pts = new Dictionary<RoadEntry, List<Vector3>>();
        foreach (var r in roads)
        {
            if (MapWriter.Validate(r, out var p, out var ros) is not null || !styles.TryGetValue(r, out var st)) continue;
            pts[r] = p;
            for (int i = 0; i < p.Count - 1; i++)
            {
                var sty = st[Math.Min(i, st.Length - 1)];
                var run = r.Runs[ros[i]];
                // halbe Breite um die Linie: der weitere der beiden Modellränder
                segs.Add((r, p[i], p[i + 1], sty.Width / 2 + MathF.Abs(sty.ModelCenter), run.Bridge || run.Tunnel));
            }
        }
        foreach (var r in roads)
        {
            if (!pts.TryGetValue(r, out var p)) continue;
            var st = styles[r];
            foreach (var run in r.Runs.Where(x => x.Bridge))
            {
                int n = run.To - run.From + 1;
                if (n < 2) continue;
                var P = new Vector2[n]; var Y = new float[n]; var C = new float[n];
                for (int k = 0; k < n; k++)
                {
                    var q = p[run.From + k];
                    P[k] = new Vector2(q.X, q.Z); Y[k] = q.Y;
                    if (k > 0) C[k] = C[k - 1] + Vector2.Distance(P[k - 1], P[k]);
                }
                float left = 0, right = 0;
                for (int i = run.From; i < run.To; i++)
                {
                    var sty = st[Math.Min(i, st.Length - 1)];
                    left = MathF.Max(left, sty.Width / 2 - sty.ModelCenter);
                    right = MathF.Max(right, sty.Width / 2 + sty.ModelCenter);
                }
                var sp = new Span { Id = $"{r.Id}[{run.From}-{run.To}]", P = P, Y = Y, Cum = C, Left = left + SideMargin, Right = right + SideMargin };
                // untere Straßen: Schnitt der Linien in der Draufsicht
                foreach (var o in segs)
                {
                    if (ReferenceEquals(o.R, r)) continue;
                    var a = new Vector2(o.A.X, o.A.Z); var b = new Vector2(o.B.X, o.B.Z);
                    for (int k = 0; k < n - 1; k++)
                    {
                        if (!Cross(P[k], P[k + 1], a, b, out float t, out float w)) continue;
                        float yb = Y[k] + (Y[k + 1] - Y[k]) * t, yl = o.A.Y + (o.B.Y - o.A.Y) * w;
                        if (yb - yl < MinClear) continue;
                        var d1 = Vector2.Normalize(P[k + 1] - P[k]); var d2 = Vector2.Normalize(b - a);
                        float sin = MathF.Max(0.3f, MathF.Abs(d1.X * d2.Y - d1.Y * d2.X));
                        sp.Lower.Add((C[k] + (C[k + 1] - C[k]) * t, yl, o.Hw / sin));
                    }
                }
                // Prefab-Spuren unter der Brücke (je Meter längs die tiefste)
                var byS = new Dictionary<int, float>();
                foreach (var l in prefabLanes)
                {
                    if (!Project(sp, new Vector2(l.X, l.Z), out float s0, out float v0, out float yd)) continue;
                    if (v0 < -sp.Left || v0 > sp.Right || yd - l.Y < MinClear) continue;
                    int key = (int)MathF.Round(s0);
                    byS[key] = byS.TryGetValue(key, out var y0) ? MathF.Min(y0, l.Y) : l.Y;
                }
                foreach (var (key, y) in byS) sp.Lower.Add((key, y, 3f));
                if (sp.Lower.Count == 0) continue;   // Brücke über Tal/Fluss: nichts tun
                // Böschung je Ende: muss vom Brückenende bis an die flache Sohle reichen
                var first = sp.Lower.MinBy(x => x.S - x.W);
                var last = sp.Lower.MaxBy(x => x.S + x.W);
                sp.SlopeA = SlopeFor(Y[0] - DeckDrop - (first.Y - FloorDrop), first.S - first.W - Shoulder);
                sp.SlopeB = SlopeFor(Y[^1] - DeckDrop - (last.Y - FloorDrop), sp.Len - (last.S + last.W) - Shoulder);
                float floor = sp.Lower.Min(x => x.Y) - FloorDrop;
                sp.Reach = MathF.Max(0, (Y.Max() + 2f - floor) / Slope);
                float pad = MathF.Max(sp.Left, sp.Right) + sp.Reach;
                sp.Min = new Vector2(P.Min(q => q.X), P.Min(q => q.Y)) - new Vector2(pad);
                sp.Max = new Vector2(P.Max(q => q.X), P.Max(q => q.Y)) + new Vector2(pad);
                foreach (var o in segs)
                {
                    if (o.Bridge) continue;
                    var a = new Vector2(o.A.X, o.A.Z); var b = new Vector2(o.B.X, o.B.Z);
                    float rr = o.Hw + Shoulder + (Y.Max() + 2f - floor) / Slope;
                    if (MathF.Max(a.X, b.X) < sp.Min.X - rr || MathF.Min(a.X, b.X) > sp.Max.X + rr ||
                        MathF.Max(a.Y, b.Y) < sp.Min.Y - rr || MathF.Min(a.Y, b.Y) > sp.Max.Y + rr) continue;
                    sp.Support.Add((a, b, o.A.Y, o.B.Y, o.Hw));
                }
                u.spans.Add(sp);
            }
        }
        return u;
    }

    private static float SlopeFor(float drop, float room)
    {
        if (drop <= 0) return Slope;
        return Math.Clamp(drop / MathF.Max(room, 0.5f), Slope, MaxSlope);
    }

    /// <summary>Obergrenze der Geländehöhe an (x, z); h unverändert außerhalb jeder Unterführung.</summary>
    public float Cap(float x, float z, float h)
    {
        var q = new Vector2(x, z);
        foreach (var sp in spans)
        {
            if (q.X < sp.Min.X || q.Y < sp.Min.Y || q.X > sp.Max.X || q.Y > sp.Max.Y) continue;
            if (!Project(sp, q, out float s, out float v, out _)) continue;
            float e = MathF.Max(0, MathF.Max(v - sp.Right, -v - sp.Left));
            if (e > sp.Reach) continue;
            float c = Floor(sp, s) + Slope * e;
            if (c >= h) continue;
            // neben ebenerdigen Straßen (andere Richtungsfahrbahn, Fortsetzung hinter dem Brückenende)
            // nicht in deren Damm schneiden
            foreach (var (a, b, ya, yb, hw) in sp.Support)
            {
                var ab = b - a; float l2 = ab.LengthSquared();
                float t = l2 > 1e-6f ? Math.Clamp(Vector2.Dot(q - a, ab) / l2, 0, 1) : 0;
                float d = Vector2.Distance(q, a + ab * t);
                float sup = ya + (yb - ya) * t - FloorDrop - Slope * MathF.Max(0, d - hw - Shoulder);
                if (sup > c) c = sup;
            }
            if (c < h) h = c;
        }
        return h;
    }

    /// <summary>Boden der Unterführung an der Stelle s: Sohle der unteren Straßen, zu den Enden Böschung.</summary>
    private static float Floor(Span sp, float s)
    {
        float best = float.MaxValue;
        foreach (var (sk, yk, wk) in sp.Lower)
        {
            float g = s < sk ? sp.SlopeA : sp.SlopeB;
            float c = yk - FloorDrop + g * MathF.Max(0, MathF.Abs(s - sk) - (wk + Shoulder));
            if (c < best) best = c;
        }
        return best;
    }

    /// <summary>Lage von q zur Brückenlinie: Bogenlänge s ∈ [0, Länge], Abstand v (rechts positiv).</summary>
    private static bool Project(Span sp, Vector2 q, out float s, out float v, out float yDeck)
    {
        s = v = yDeck = 0;
        float best = float.MaxValue; int bestK = -1; float bestT = 0;
        for (int k = 0; k < sp.P.Length - 1; k++)
        {
            var a = sp.P[k]; var ab = sp.P[k + 1] - a;
            float l2 = ab.LengthSquared(); if (l2 < 1e-6f) continue;
            float t = Vector2.Dot(q - a, ab) / l2;
            float tc = Math.Clamp(t, 0, 1);
            float d = Vector2.Distance(q, a + ab * tc);
            if (d >= best) continue;
            best = d; bestK = k; bestT = t;
            float len = MathF.Sqrt(l2);
            var dir = ab / len;
            var right = new Vector2(-dir.Y, dir.X);
            s = sp.Cum[k] + len * tc;
            v = Vector2.Dot(q - (a + ab * tc), right);
            yDeck = sp.Y[k] + (sp.Y[k + 1] - sp.Y[k]) * tc;
        }
        // nur zwischen den Brückenenden: vor dem Anfang bzw. hinter dem Ende zählt nicht
        if (bestK < 0) return false;
        if (bestK == 0 && bestT < 0) return false;
        if (bestK == sp.P.Length - 2 && bestT > 1) return false;
        return true;
    }

    private static bool Cross(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1, out float t, out float u)
    {
        t = u = 0;
        var r = a1 - a0; var s = b1 - b0;
        float den = r.X * s.Y - r.Y * s.X;
        if (MathF.Abs(den) < 1e-8f) return false;
        var w = b0 - a0;
        t = (w.X * s.Y - w.Y * s.X) / den;
        u = (w.X * r.Y - w.Y * r.X) / den;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1;
    }

    /// <summary>
    /// Steinmaterial auf die Böschungsquads des Straßengeländes unter Brücken (Reihen ab 1;
    /// Reihe 0 liegt am Modellrand und steht nicht in den Höhenpunkten). Reihenfolge der Quads
    /// wie in der SCS-Karte: Spalte für Spalte, je Spalte die Reihen nach außen.
    /// </summary>
    public int Pave(List<RoadEntry> roads, Dictionary<RoadEntry, Road> firstItem, Dictionary<RoadEntry, Road> lastItem)
    {
        int n = 0;
        var stone = new Token(StoneMaterial);
        foreach (var entry in roads)
        {
            if (!firstItem.TryGetValue(entry, out var it)) continue;
            for (var rd = it; rd is not null; rd = rd.ForwardNode.ForwardItem as Road)
            {
                foreach (var side in new[] { rd.Left.Terrain, rd.Right.Terrain })
                {
                    var q = side.QuadData;
                    if (side.Size <= 0 || q.Offsets.Count == 0 || q.Quads.Count != q.Cols * q.Rows) continue;
                    var at = new Dictionary<(int, int), Vector3>();
                    foreach (var o in q.Offsets) at[(o.X, o.Y)] = rd.Node.Position + o.Data;   // Weltachsen ab Anfangsknoten
                    int idx = -1;
                    for (int i = 0; i < q.Cols; i++)
                        for (int j = 1; j < q.Rows; j++)
                        {
                            if (!at.TryGetValue((i, j), out var p00) || !at.TryGetValue((i + 1, j), out var p10) ||
                                !at.TryGetValue((i, j + 1), out var p01) || !at.TryGetValue((i + 1, j + 1), out var p11)) continue;
                            if (!OnSlope(p00, p10, p01, p11)) continue;
                            if (idx < 0)
                            {
                                idx = q.BrushMaterials.FindIndex(m => m.Name == stone);
                                if (idx < 0) { q.BrushMaterials.Add(new Material(stone)); idx = q.BrushMaterials.Count - 1; }
                            }
                            int k = i * q.Rows + j;
                            var quad = q.Quads[k];
                            quad.MainMaterial = (Nibble)idx;
                            q.Quads[k] = quad;
                            n++;
                        }
                }
                if (rd == lastItem[entry]) break;
            }
        }
        Paved = n;
        return n;
    }

    /// <summary>Alle vier Ecken unter einer Brücke (im flachen Bereich) und das Quad auf der Böschung?</summary>
    private bool OnSlope(params Vector3[] c)
    {
        foreach (var sp in spans)
        {
            bool all = true; float lift = 0;
            foreach (var p in c)
            {
                var q = new Vector2(p.X, p.Z);
                if (q.X < sp.Min.X || q.Y < sp.Min.Y || q.X > sp.Max.X || q.Y > sp.Max.Y) { all = false; break; }
                if (!Project(sp, q, out float s, out float v, out _) || v > sp.Right || v < -sp.Left) { all = false; break; }
                // Höhe über der Sohle der nächstgelegenen unteren Straße
                var near = sp.Lower.MinBy(x => MathF.Abs(s - x.S));
                lift = MathF.Max(lift, p.Y - (near.Y - FloorDrop));
            }
            if (all && lift > PaveAbove) return true;
        }
        return false;
    }
}
