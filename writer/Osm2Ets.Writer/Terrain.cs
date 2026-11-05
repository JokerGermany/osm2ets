// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;

namespace Osm2Ets.Writer;

/// <summary>
/// Begrenzt das Gelände neben einer Straße, damit es nicht in benachbarte
/// Fahrbahnen (Gegenfahrbahn, Rampen) hineinragt und dort "durch den Asphalt
/// schaut". Je Abschnitt und Seite wird der Abstand zur nächsten anderen Straße
/// gemessen; Gelände = min(Wunsch, Abstand − eigener Modellrand − fremder Modellrand − Rand).
/// Die Modellränder sind je Seite verschieden (Einbahn-Vorlagen sind asymmetrisch, z. B.
/// ger16 −9,0…+8,0 m); gemessen wird jeweils der Rand, der zur anderen Straße zeigt.
/// </summary>
public static class TerrainClearance
{
    public const float Margin = 0f;
    public static float SearchRadius = 80f;
    private const float Cell = 100f;
    /// <summary>Lücken zwischen zwei Straßen teilen (je Straße bis zur Mitte) – bei breitem Gelände.</summary>
    public static bool Share = false;
    /// <summary>Nur mit symmetrischen Rändern (OSM2ETS_RT_SYMMETRIC): Modell schmaler als die Vorlage angenommen.</summary>
    public const float MedianExtra = 1.5f;
    /// <summary>Beim Teilen reicht jedes Gelände so weit über die Mitte (keine Fuge zwischen beiden).</summary>
    public const float ShareOverlap = 0.5f;
    /// <summary>So weit bleibt das Gelände vor dem Modellrand einer anderen Straße (nie über fremden Asphalt).</summary>
    public const float EdgeClear = 0.1f;
    /// <summary>Gezählt: Seiten, die wegen eines fremden Modellrands schmaler als MinSide blieben.</summary>
    public static int HardLimited;
    /// <summary>Wunschbreite je Seite, wenn flächiges Gelände aus dem Höhenraster kommt.</summary>
    public const float WideTerrain = 60f;

    /// <summary>Innenseite von Kurven auf den halben Kurvenradius begrenzen (sonst Falten).</summary>
    public static int ClampCurves(List<RoadEntry> roads, Dictionary<RoadEntry, ResolvedStyle[]> styles)
    {
        int n = 0;
        foreach (var r in roads)
        {
            var st = styles[r]; var p = r.Points;
            Vector2 P(int i) => new((float)p[i][0], (float)p[i][2]);
            for (int i = 0; i < st.Length; i++)
            {
                int a = Math.Max(0, i - 1), b = Math.Min(p.Count - 1, i + 2);
                if (b - a < 2) continue;
                var d1 = P(a + 1) - P(a); var d2 = P(b) - P(b - 1);
                if (d1.LengthSquared() < 1e-4f || d2.LengthSquared() < 1e-4f) continue;
                d1 = Vector2.Normalize(d1); d2 = Vector2.Normalize(d2);
                float ang = MathF.Acos(Math.Clamp(Vector2.Dot(d1, d2), -1, 1));
                if (ang < 0.02f) continue;
                float len = 0; for (int k = a; k < b; k++) len += Vector2.Distance(P(k), P(k + 1));
                float half = len / ang / 2;
                bool right = Vector2.Dot(d2, new Vector2(-d1.Y, d1.X)) > 0;
                if (right && st[i].TerrainRight > half) { st[i] = st[i] with { TerrainRight = MathF.Floor(half) }; n++; }
                if (!right && st[i].TerrainLeft > half) { st[i] = st[i] with { TerrainLeft = MathF.Floor(half) }; n++; }
            }
        }
        return n;
    }

    /// <summary>Straßengelände (wo vorhanden) auf breite Wunschbreite setzen.</summary>
    public static void Widen(Dictionary<RoadEntry, ResolvedStyle[]> styles)
    {
        foreach (var st in styles.Values)
            for (int i = 0; i < st.Length; i++)
                st[i] = st[i] with
                {
                    TerrainLeft = st[i].TerrainLeft > 0 ? MathF.Max(st[i].TerrainLeft, WideTerrain) : 0,
                    TerrainRight = st[i].TerrainRight > 0 ? MathF.Max(st[i].TerrainRight, WideTerrain) : 0,
                };
        Share = true;
        SearchRadius = WideTerrain * 2 + 40;
    }

    /// <summary>EdgeL/EdgeR: Abstand der Straßenlinie vom linken/rechten Modellrand.</summary>
    private readonly record struct Seg(RoadEntry Road, int Index, Vector2 A, Vector2 B, float EdgeL, float EdgeR);

    /// <summary>Modellränder eines Stils (links, rechts) relativ zur Straßenlinie.</summary>
    public static (float L, float R) Edges(ResolvedStyle st)
    {
        float hw = st.Width / 2, mc = MapWriter.UseModelEdges ? st.ModelCenter : 0f;
        return (MathF.Max(0, hw - mc), MathF.Max(0, hw + mc));
    }

    public static int Apply(List<RoadEntry> roads, Dictionary<RoadEntry, ResolvedStyle[]> styles,
                            List<(Vector3[] Nodes, float HalfWidth)>? obstacles = null)
    {
        var segs = new List<Seg>();
        var grid = new Dictionary<(int, int), List<int>>();
        void AddSeg(Seg sg)
        {
            int k = segs.Count;
            segs.Add(sg);
            var min = Vector2.Min(sg.A, sg.B) - new Vector2(SearchRadius);
            var max = Vector2.Max(sg.A, sg.B) + new Vector2(SearchRadius);
            for (int gx = (int)MathF.Floor(min.X / Cell); gx <= (int)MathF.Floor(max.X / Cell); gx++)
            for (int gz = (int)MathF.Floor(min.Y / Cell); gz <= (int)MathF.Floor(max.Y / Cell); gz++)
            {
                if (!grid.TryGetValue((gx, gz), out var l)) grid[(gx, gz)] = l = [];
                l.Add(k);
            }
        }
        // Prefabs: Strecken Knoten → Mitte (Road = null, begrenzt nur fremdes Gelände)
        foreach (var (nodes, hw) in obstacles ?? [])
        {
            var c = nodes.Aggregate(Vector3.Zero, (a, b) => a + b) / nodes.Length;
            foreach (var n in nodes)
                AddSeg(new Seg(null!, -1, new Vector2(n.X, n.Z), new Vector2(c.X, c.Z), hw, hw));
        }
        int firstRoadSeg = segs.Count;
        foreach (var r in roads)
        {
            var st = styles[r];
            for (int i = 0; i < r.Points.Count - 1; i++)
            {
                var a = new Vector2((float)r.Points[i][0], (float)r.Points[i][2]);
                var b = new Vector2((float)r.Points[i + 1][0], (float)r.Points[i + 1][2]);
                var (el, er) = Edges(st[i]);
                AddSeg(new Seg(r, i, a, b, el, er));
            }
        }

        int clamped = 0;
        float MinSide = float.TryParse(Environment.GetEnvironmentVariable("OSM2ETS_RT_MIN"), System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : 2f;
        foreach (var s in segs.Skip(firstRoadSeg))
        {
            var style = styles[s.Road][s.Index];
            float left = style.TerrainLeft, right = style.TerrainRight;
            // höchstens bis kurz vor den Modellrand einer anderen Straße (auch nicht für MinSide)
            float hardL = float.MaxValue, hardR = float.MaxValue;
            bool exact = MapWriter.UseModelEdges;
            // je Spalte begrenzt (TerrainFill.ClampColumns): Breite nach der breitesten Stelle des
            // Abschnitts, die engeren Stellen kürzt das Straßengelände selbst je Spalte
            bool perColumn = exact && Share && TerrainFill.ClampColumns && TerrainFill.GapRamp;
            float maxL = 0, maxR = 0, maxHardL = 0, maxHardR = 0;
            var dir = s.B - s.A;
            if (dir.LengthSquared() == 0) continue;
            dir = Vector2.Normalize(dir);
            // ETS2: x = Osten, z = Süden. Rechts der Fahrtrichtung = (-dir.Y, dir.X)
            var rightN = new Vector2(-dir.Y, dir.X);
            var cand = new HashSet<int>();
            foreach (var t in new[] { 0.1f, 0.5f, 0.9f })
            {
                var p = Vector2.Lerp(s.A, s.B, t);
                if (grid.TryGetValue(((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell)), out var l))
                    foreach (var k in l) cand.Add(k);
            }
            foreach (var t in new[] { 0.05f, 0.25f, 0.5f, 0.75f, 0.95f })
            {
                if (perColumn) { left = style.TerrainLeft; right = style.TerrainRight; hardL = hardR = float.MaxValue; }
                var p = Vector2.Lerp(s.A, s.B, t);
                foreach (var k in cand)
                {
                    var o = segs[k];
                    if (Share && o.Road is null) continue;   // Prefabs: Füllgelände/Spuren regeln das
                    if (o.Road == s.Road && Math.Abs(o.Index - s.Index) <= 1) continue;
                    var q = Closest(o.A, o.B, p);
                    var d = q - p;
                    float dist = d.Length();
                    if (dist > SearchRadius) continue;
                    // nur Straßen, die etwa parallel daneben liegen; kreuzende Straßen
                    // (Brücken, Kreuzungen) begrenzen das Gelände nicht
                    if ((o.B - o.A).LengthSquared() < 1e-4f) continue;
                    var odir = Vector2.Normalize(o.B - o.A);
                    float cos = MathF.Abs(Vector2.Dot(odir, dir));
                    var oRight = new Vector2(-odir.Y, odir.X);
                    if (cos >= 0.7f)
                    {
                        // nur, wenn die Nachbarstraße wirklich seitlich liegt (nicht weit voraus)
                        if (MathF.Abs(Vector2.Dot(d, rightN)) < 0.5f * dist) continue;
                        bool onRight = Vector2.Dot(d, rightN) >= 0;
                        // zugewandte Modellränder: eigener auf der Seite der Nachbarstraße, fremder
                        // auf der Seite, auf der diese Straße von ihr aus liegt
                        float own = onRight ? s.EdgeR : s.EdgeL;
                        float oth = Vector2.Dot(-d, oRight) >= 0 ? o.EdgeR : o.EdgeL;
                        float gap = dist - own - oth - Margin;
                        float free = MathF.Max(0, gap);
                        if (exact)
                        {
                            // beide Straßen füllen die Lücke: jede bis knapp über die Mitte
                            if (Share && o.Road is not null) free = free / 2 + ShareOverlap;
                            if (o.Road is not null)
                            {
                                float hard = MathF.Max(0, gap - EdgeClear);
                                free = MathF.Min(free, hard);
                                if (onRight) hardR = MathF.Min(hardR, hard); else hardL = MathF.Min(hardL, hard);
                            }
                        }
                        // alt (symmetrische Ränder): beide bis zur Mitte bzw. etwas unter die Nachbarfahrbahn
                        else if (Share && o.Road is not null) free = free / 2 + MedianExtra;
                        else if (o.Road is not null) free += MedianExtra;
                        if (onRight) right = MathF.Min(right, free);
                        else left = MathF.Min(left, free);
                        continue;
                    }
                    // schräg oder quer liegende Straße (Kreuzung, Rampe, Überführung): dort,
                    // wo der Geländestreifen sie trifft, endet er – sonst ragt er mit seiner
                    // eigenen Höhe über die fremde Fahrbahn
                    float sin = MathF.Max(MathF.Sqrt(1 - cos * cos), 0.3f);
                    float othE = Vector2.Dot(p - o.A, oRight) >= 0 ? o.EdgeR : o.EdgeL;
                    foreach (var (n, isRight) in new[] { (rightN, true), (-rightN, false) })
                    {
                        float want = isRight ? right : left;
                        if (want <= 0) continue;
                        if (!RaySegment(p, n, o.A, o.B, out float u)) continue;
                        float own = isRight ? s.EdgeR : s.EdgeL;
                        float reach = own + want + othE / sin + Margin;
                        if (u > reach) continue;
                        float gap = u - own - othE / sin - Margin;
                        float free = MathF.Max(0, gap);
                        if (Share && o.Road is not null) free = free / 2 + (exact ? ShareOverlap : MedianExtra);
                        if (exact && o.Road is not null)
                        {
                            float hard = MathF.Max(0, gap - EdgeClear);
                            free = MathF.Min(free, hard);
                            if (isRight) hardR = MathF.Min(hardR, hard); else hardL = MathF.Min(hardL, hard);
                        }
                        if (isRight) right = MathF.Min(right, free); else left = MathF.Min(left, free);
                    }
                }
                if (perColumn)
                {
                    maxL = MathF.Max(maxL, left); maxR = MathF.Max(maxR, right);
                    maxHardL = MathF.Max(maxHardL, hardL); maxHardR = MathF.Max(maxHardR, hardR);
                }
            }
            if (perColumn) { left = maxL; right = maxR; hardL = maxHardL; hardR = maxHardR; }
            // Mindestbreite wie in der SCS-Karte (1,5–2,2 m an der Zwickel-/Mittelstreifenseite):
            // ohne Straßengelände stößt der Asphaltrand direkt an das tiefer liegende Füllgelände,
            // im Editor als schwarzer Streifen entlang der Fahrbahn
            left = MathF.Max(left, MathF.Min(style.TerrainLeft, MinSide));
            right = MathF.Max(right, MathF.Min(style.TerrainRight, MinSide));
            // nie über den Asphalt einer anderen Straße (dort läge das Gelände auf eigener Höhe
            // über der fremden Fahrbahn bzw. flimmerte mit ihr)
            if (left > hardL) { left = hardL; if (hardL < MinSide) HardLimited++; }
            if (right > hardR) { right = hardR; if (hardR < MinSide) HardLimited++; }
            if (left < style.TerrainLeft - 0.01f || right < style.TerrainRight - 0.01f)
            {
                styles[s.Road][s.Index] = style with { TerrainLeft = MathF.Floor(left * 2) / 2, TerrainRight = MathF.Floor(right * 2) / 2 };
                clamped++;
            }
        }
        return clamped;
    }

    /// <summary>Schnitt des Strahls p + u·n (u ≥ 0) mit der Strecke a–b.</summary>
    private static bool RaySegment(Vector2 p, Vector2 n, Vector2 a, Vector2 b, out float u)
    {
        u = 0;
        var e = b - a;
        float den = n.X * e.Y - n.Y * e.X;
        if (MathF.Abs(den) < 1e-6f) return false;
        var w = a - p;
        u = (w.X * e.Y - w.Y * e.X) / den;
        float t = (w.X * n.Y - w.Y * n.X) / den;
        return u >= 0 && t >= 0 && t <= 1;
    }

    private static Vector2 Closest(Vector2 a, Vector2 b, Vector2 p)
    {
        var ab = b - a;
        float l2 = ab.LengthSquared();
        if (l2 == 0) return a;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0, 1);
        return a + t * ab;
    }
}

/// <summary>
/// Vereinheitlicht die Spuranzahl einer Richtungsfahrbahn über die ganze Kette
/// (nach Länge gewichtete Mehrheit), damit nicht auf kurzen Stücken zwischen
/// 2- und 3-spurigen Vorlagen gewechselt wird. Ein echter Spurwechsel braucht
/// ein Übergangs-Prefab, das es noch nicht gibt.
/// </summary>
public static class LaneSmoothing
{
    public static void Apply(RoadEntry r)
    {
        var weights = new Dictionary<int, double>();
        foreach (var run in r.Runs)
        {
            if (!run.Oneway || run.Lanes is null) continue;
            double len = 0;
            for (int i = run.From; i < run.To; i++)
                len += Dist(r.Points[i], r.Points[i + 1]);
            weights[run.Lanes.Value] = weights.GetValueOrDefault(run.Lanes.Value) + len;
        }
        if (weights.Count < 2) return;
        int best = weights.MaxBy(kv => kv.Value).Key;
        for (int k = 0; k < r.Runs.Count; k++)
        {
            var run = r.Runs[k];
            if (!run.Oneway) continue;
            // Zusatzspur am Anfang bzw. Ende (eine Spur mehr, bis 400 m) bleibt: die Anschluss-
            // Prefabs brauchen sie (Beschleunigungs-/Verzögerungsstreifen) und setzen sie selbst zurück
            if (KeepAux && (k == 0 || k == r.Runs.Count - 1) && r.Runs.Count > 1 && run.Lanes == best + 1)
            {
                double len = 0;
                for (int i = run.From; i < run.To; i++) len += Dist(r.Points[i], r.Points[i + 1]);
                if (len <= 400) continue;
            }
            run.Lanes = best;
        }
    }

    /// <summary>Zusatzspuren an den Enden behalten (aus: OSM2ETS_SMOOTH_AUX=1).</summary>
    public static bool KeepAux = Environment.GetEnvironmentVariable("OSM2ETS_SMOOTH_AUX") != "1";

    private static double Dist(double[] a, double[] b) =>
        Math.Sqrt(Math.Pow(b[0] - a[0], 2) + Math.Pow(b[2] - a[2], 2));
}

/// <summary>
/// Schiebt gegenläufige Richtungsfahrbahnen auseinander, wenn ihre OSM-Mittellinien
/// enger liegen, als die ETS2-Vorlagen breit sind (sonst überlappen die Fahrbahnen).
/// Nur Lageänderung quer zur Fahrtrichtung, geglättet entlang der Straße.
/// Die saubere Lösung wäre eine gemeinsame Mittellinie mit Vorlagen wie ger11/ger14
/// ("hw … offset"); das ist noch nicht umgesetzt.
/// </summary>
public static class CarriagewaySeparation
{
    public const float Median = 2.0f;
    public const float MaxShift = 6f;

    public static int Apply(List<RoadEntry> roads, Dictionary<RoadEntry, ResolvedStyle[]> styles)
    {
        var cands = roads.Where(r => r.Runs.All(x => x.Oneway) && r.Points.Count >= 2).ToList();
        var segs = new List<(RoadEntry R, Vector2 A, Vector2 B, float Hw)>();
        foreach (var r in cands)
            for (int i = 0; i < r.Points.Count - 1; i++)
                segs.Add((r, P(r.Points[i]), P(r.Points[i + 1]), styles[r][i].Width / 2));

        int moved = 0;
        foreach (var r in cands)
        {
            int n = r.Points.Count;
            var shift = new float[n];
            var normal = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                var p = P(r.Points[i]);
                var dir = Vector2.Normalize(P(r.Points[Math.Min(i + 1, n - 1)]) - P(r.Points[Math.Max(i - 1, 0)]));
                normal[i] = new Vector2(-dir.Y, dir.X); // rechts
                float hw = styles[r][Math.Min(i, n - 2)].Width / 2;
                foreach (var o in segs)
                {
                    if (o.R == r) continue;
                    var od = o.B - o.A;
                    if (od.LengthSquared() == 0) continue;
                    od = Vector2.Normalize(od);
                    if (Vector2.Dot(od, dir) > -0.9f) continue; // nur Gegenrichtung
                    var q = Closest(o.A, o.B, p);
                    var d = q - p;
                    float dist = d.Length();
                    if (dist > 30f) continue;
                    float need = hw + o.Hw + Median;
                    if (dist >= need) continue;
                    // Gegenfahrbahn liegt links (Rechtsverkehr) -> nach rechts ausweichen
                    float side = Vector2.Dot(d, normal[i]) < 0 ? 1f : -1f;
                    float s = side * Math.Min((need - dist) / 2, MaxShift);
                    if (Math.Abs(s) > Math.Abs(shift[i])) shift[i] = s;
                }
            }
            // glätten (gleitendes Maximum-Mittel über 2 Nachbarn)
            var sm = new float[n];
            for (int i = 0; i < n; i++)
            {
                float acc = 0; int c = 0;
                for (int k = Math.Max(0, i - 2); k <= Math.Min(n - 1, i + 2); k++) { acc += shift[k]; c++; }
                sm[i] = acc / c;
                if (Math.Abs(shift[i]) > Math.Abs(sm[i])) sm[i] = (sm[i] + shift[i]) / 2;
            }
            // an Prefab-Anschlüssen nichts verschieben (Lage ist dort durch das Prefab festgelegt)
            var cum = new float[n];
            for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + Vector2.Distance(P(r.Points[i - 1]), P(r.Points[i]));
            float Fade(int i)
            {
                // die ersten/letzten 30 m fest, danach über 40 m einblenden
                float f = 1f;
                if (r.End?.Kind == "prefab") f = Math.Min(f, (cum[n - 1] - cum[i] - 30f) / 40f);
                if (r.Start?.Kind == "prefab") f = Math.Min(f, (cum[i] - 30f) / 40f);
                return Math.Clamp(f, 0f, 1f);
            }
            for (int i = 0; i < n; i++) sm[i] *= Fade(i);
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                if (Math.Abs(sm[i]) < 0.05f) continue;
                var p = r.Points[i];
                r.Points[i] = [p[0] + normal[i].X * sm[i], p[1], p[2] + normal[i].Y * sm[i]];
                any = true;
            }
            if (any) moved++;
        }
        return moved;
    }

    private static Vector2 P(double[] p) => new((float)p[0], (float)p[2]);

    /// <summary>Schnitt des Strahls p + u·n (u ≥ 0) mit der Strecke a–b.</summary>
    private static bool RaySegment(Vector2 p, Vector2 n, Vector2 a, Vector2 b, out float u)
    {
        u = 0;
        var e = b - a;
        float den = n.X * e.Y - n.Y * e.X;
        if (MathF.Abs(den) < 1e-6f) return false;
        var w = a - p;
        u = (w.X * e.Y - w.Y * e.X) / den;
        float t = (w.X * n.Y - w.Y * n.X) / den;
        return u >= 0 && t >= 0 && t <= 1;
    }

    private static Vector2 Closest(Vector2 a, Vector2 b, Vector2 p)
    {
        var ab = b - a;
        float l2 = ab.LengthSquared();
        if (l2 == 0) return a;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0, 1);
        return a + t * ab;
    }
}
