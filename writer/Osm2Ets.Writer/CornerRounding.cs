using System.Numerics;

namespace Osm2Ets.Writer;

/// <summary>
/// Rundet Ecken in grob gezeichneten OSM-Linien von Autobahnen, Kraftfahrstraßen und ihren
/// Rampen: statt eines Knicks am OSM-Knoten ein Kreisbogen (Punkte alle <see cref="Step"/> m).
/// Sonst zeigt ETS2 an jedem OSM-Knoten einen sichtbaren Knick im Fahrbahnrand.
/// </summary>
public static class CornerRounding
{
    public static bool Enabled = Environment.GetEnvironmentVariable("OSM2ETS_NO_ROUND") is null;
    public const float MinTurnDeg = 1.0f, Step = 8f;
    public const float RadiusMain = 1500f, RadiusLink = 400f;

    public static int Apply(List<RoadEntry> roads)
    {
        if (!Enabled) return 0;
        int corners = 0;
        foreach (var r in roads)
        {
            if (r.Points.Count < 3 || r.Runs.Count == 0) continue;
            bool main = r.Runs.All(x => x.Highway is "motorway" or "trunk");
            bool link = r.Runs.All(x => x.Highway is "motorway" or "trunk" or "motorway_link" or "trunk_link");
            if (!link) continue;
            float R = main ? RadiusMain : RadiusLink;
            var pts = r.Points;
            var outPts = new List<double[]> { pts[0] };
            var map = new int[pts.Count];            // alter Index -> neuer Index
            map[0] = 0;
            var fixedIdx = new HashSet<int>(r.Runs.SelectMany(x => new[] { x.From, x.To }));
            for (int i = 1; i < pts.Count - 1; i++)
            {
                // Laufgrenzen (Brücken, Spurwechsel, Kreuzungspunkte) und Brücken/Tunnel unverändert
                var run = r.Runs.FirstOrDefault(x => x.From <= i && i < x.To) ?? r.Runs[^1];
                if (fixedIdx.Contains(i) || run.Bridge || run.Tunnel)
                { map[i] = outPts.Count; outPts.Add(pts[i]); continue; }
                var a = P(pts[i - 1]); var b = P(pts[i]); var c = P(pts[i + 1]);
                float l1 = Vector2.Distance(a, b), l2 = Vector2.Distance(b, c);
                if (l1 < 1e-3f || l2 < 1e-3f) { map[i] = outPts.Count; outPts.Add(pts[i]); continue; }
                var u1 = (b - a) / l1; var u2 = (c - b) / l2;
                float th = MathF.Acos(Math.Clamp(Vector2.Dot(u1, u2), -1, 1));
                if (th * 180 / MathF.PI < MinTurnDeg || th > MathF.PI / 2)
                { map[i] = outPts.Count; outPts.Add(pts[i]); continue; }
                // Tangentenlänge: Wunschradius, höchstens knapp die halbe Nachbarstrecke
                float t = MathF.Min(R * MathF.Tan(th / 2), 0.45f * MathF.Min(l1, l2));
                if (t < 2f) { map[i] = outPts.Count; outPts.Add(pts[i]); continue; }
                var p0 = b - u1 * t; var p2 = b + u2 * t;
                double y0 = pts[i][1] + (pts[i - 1][1] - pts[i][1]) * t / l1;
                double y2 = pts[i][1] + (pts[i + 1][1] - pts[i][1]) * t / l2;
                int m = Math.Max(2, (int)MathF.Ceiling(2 * t / Step));
                int mid = -1;
                for (int j = 0; j <= m; j++)
                {
                    float s = (float)j / m;
                    // quadratische Bézierkurve p0–b–p2 (berührt beide Geraden tangential)
                    var q = (1 - s) * (1 - s) * p0 + 2 * (1 - s) * s * b + s * s * p2;
                    double y = (1 - s) * (1 - s) * y0 + 2 * (1 - s) * s * pts[i][1] + s * s * y2;
                    if (j == m / 2) mid = outPts.Count;
                    outPts.Add([q.X, y, q.Y]);
                }
                map[i] = mid;
                corners++;
            }
            map[^1] = outPts.Count;
            outPts.Add(pts[^1]);
            if (outPts.Count == pts.Count) continue;
            foreach (var x in r.Runs) { x.From = map[x.From]; x.To = map[x.To]; }
            r.Points = outPts;
        }
        return corners;
    }

    private static Vector2 P(double[] p) => new((float)p[0], (float)p[2]);
}
