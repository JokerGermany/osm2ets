// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;

namespace Osm2Ets.Writer;

/// <summary>
/// Begrenzt das Gelände neben einer Straße, damit es nicht in benachbarte
/// Fahrbahnen (Gegenfahrbahn, Rampen) hineinragt und dort "durch den Asphalt
/// schaut". Je Abschnitt und Seite wird der Abstand zur nächsten anderen Straße
/// gemessen; Gelände = min(Wunsch, Abstand − halbe eigene Breite − halbe fremde
/// Breite − Rand).
/// </summary>
public static class TerrainClearance
{
    public const float Margin = 1.0f;
    public const float SearchRadius = 80f;
    private const float Cell = 100f;

    private readonly record struct Seg(RoadEntry Road, int Index, Vector2 A, Vector2 B, float HalfWidth);

    public static int Apply(List<RoadEntry> roads, Dictionary<RoadEntry, ResolvedStyle[]> styles)
    {
        var segs = new List<Seg>();
        var grid = new Dictionary<(int, int), List<int>>();
        foreach (var r in roads)
        {
            var st = styles[r];
            for (int i = 0; i < r.Points.Count - 1; i++)
            {
                var a = new Vector2((float)r.Points[i][0], (float)r.Points[i][2]);
                var b = new Vector2((float)r.Points[i + 1][0], (float)r.Points[i + 1][2]);
                int k = segs.Count;
                segs.Add(new Seg(r, i, a, b, st[i].Width / 2));
                var min = Vector2.Min(a, b) - new Vector2(SearchRadius);
                var max = Vector2.Max(a, b) + new Vector2(SearchRadius);
                for (int gx = (int)MathF.Floor(min.X / Cell); gx <= (int)MathF.Floor(max.X / Cell); gx++)
                for (int gz = (int)MathF.Floor(min.Y / Cell); gz <= (int)MathF.Floor(max.Y / Cell); gz++)
                {
                    if (!grid.TryGetValue((gx, gz), out var l)) grid[(gx, gz)] = l = [];
                    l.Add(k);
                }
            }
        }

        int clamped = 0;
        foreach (var s in segs)
        {
            var style = styles[s.Road][s.Index];
            float left = style.TerrainLeft, right = style.TerrainRight;
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
            foreach (var t in new[] { 0.1f, 0.5f, 0.9f })
            {
                var p = Vector2.Lerp(s.A, s.B, t);
                foreach (var k in cand)
                {
                    var o = segs[k];
                    if (o.Road == s.Road && Math.Abs(o.Index - s.Index) <= 1) continue;
                    var q = Closest(o.A, o.B, p);
                    var d = q - p;
                    float dist = d.Length();
                    if (dist > SearchRadius) continue;
                    // nur Straßen, die etwa parallel daneben liegen; kreuzende Straßen
                    // (Brücken, Kreuzungen) begrenzen das Gelände nicht
                    var odir = Vector2.Normalize(o.B - o.A);
                    if (MathF.Abs(Vector2.Dot(odir, dir)) < 0.7f) continue;
                    float free = MathF.Max(0, dist - s.HalfWidth - o.HalfWidth - Margin);
                    if (Vector2.Dot(d, rightN) >= 0) right = MathF.Min(right, free);
                    else left = MathF.Min(left, free);
                }
            }
            if (left < style.TerrainLeft - 0.01f || right < style.TerrainRight - 0.01f)
            {
                styles[s.Road][s.Index] = style with { TerrainLeft = MathF.Round(left, 1), TerrainRight = MathF.Round(right, 1) };
                clamped++;
            }
        }
        return clamped;
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
        foreach (var run in r.Runs)
            if (run.Oneway) run.Lanes = best;
    }

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

    private static Vector2 Closest(Vector2 a, Vector2 b, Vector2 p)
    {
        var ab = b - a;
        float l2 = ab.LengthSquared();
        if (l2 == 0) return a;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0, 1);
        return a + t * ab;
    }
}
