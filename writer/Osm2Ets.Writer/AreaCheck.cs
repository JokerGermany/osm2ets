using System.Numerics;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Lage der geschriebenen Autobahnen gegenüber den OSM-Fahrbahnflächen (area:highway): Spurmitten
/// der Autobahnstraßen (ger16, ger18) alle 4 m; gezählt wird, was mehr als <see cref="Tol"/> außerhalb
/// liegt. ETS-Spuren sind 4,5 m breit (echt etwa 3,75 m), die äußeren Spurmitten liegen deshalb von
/// Haus aus rund 0,75 m weiter außen. Rampen haben im Testgebiet keine Flächen und bleiben außen vor.
/// </summary>
public static class AreaCheck
{
    public const float Tol = 1f;          // ab so viel außerhalb zählt ein Punkt (m)
    public const float Corridor = 25f;    // nur Punkte so nah an einer Fläche

    // Spurmitten relativ zur Straßenlinie (gemessene Spurlinien, siehe mapping.jsonc)
    private static readonly Dictionary<string, float[]> Centers = new()
    {
        ["ger16"] = [-6.7f, -2.2f, 2.3f], ["ger18"] = [-6.7f, -2.2f, 2.3f, 6.9f],
    };

    public sealed record Result(int Points, int Outside, float Max, List<(Vector2 At, int N, float Max)> Spots);

    public static Result? Run(Map map, List<AreaEntry> areas)
    {
        if (areas.Count == 0) return null;
        var polys = areas.Where(a => a.Points.Count >= 4)
                         .Select(a => a.Points.Select(p => new Vector2((float)p[0], (float)p[1])).ToArray()).ToList();
        var boxes = polys.Select(p => (Min: p.Aggregate(Vector2.Min), Max: p.Aggregate(Vector2.Max))).ToList();
        int total = 0;
        var bad = new List<(Vector2 P, float D)>();
        foreach (var r in map.MapItems.Values.OfType<Road>())
        {
            if (!Centers.TryGetValue(r.RoadType.ToString(), out var cs)) continue;
            float len = Vector3.Distance(r.Node.Position, r.ForwardNode.Position);
            int n = Math.Max(1, (int)(len / 4f));
            for (int i = 0; i <= n; i++)
            {
                var (p, rt) = Hermite(r, (float)i / n);
                foreach (var c in cs)
                {
                    var q = new Vector2(p.X + rt.X * c, p.Z + rt.Y * c);
                    float d = Distance(q, polys, boxes);
                    if (d > Corridor) continue;
                    total++;
                    if (d > Tol) bad.Add((q, d));
                }
            }
        }
        var spots = bad.GroupBy(b => ((int)MathF.Floor(b.P.X / 50), (int)MathF.Floor(b.P.Y / 50)))
                       .Select(g => (At: g.Aggregate(Vector2.Zero, (s, b) => s + b.P) / g.Count(), N: g.Count(), Max: g.Max(b => b.D)))
                       .OrderByDescending(g => g.N).ToList();
        return new Result(total, bad.Count, bad.Count > 0 ? bad.Max(b => b.D) : 0, spots);
    }

    /// <summary>0 innerhalb einer Fläche, sonst Abstand zur nächsten.</summary>
    private static float Distance(Vector2 q, List<Vector2[]> polys, List<(Vector2 Min, Vector2 Max)> boxes)
    {
        float best = float.MaxValue;
        for (int k = 0; k < polys.Count; k++)
        {
            var (mn, mx) = boxes[k];
            float bx = MathF.Max(0, MathF.Max(mn.X - q.X, q.X - mx.X)), bz = MathF.Max(0, MathF.Max(mn.Y - q.Y, q.Y - mx.Y));
            if (MathF.Sqrt(bx * bx + bz * bz) >= best) continue;
            var poly = polys[k];
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                if ((a.Y > q.Y) != (b.Y > q.Y) && q.X < (b.X - a.X) * (q.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
                var ab = b - a; float l2 = ab.LengthSquared();
                float t = l2 > 0 ? Math.Clamp(Vector2.Dot(q - a, ab) / l2, 0, 1) : 0;
                best = MathF.Min(best, Vector2.Distance(q, a + ab * t));
            }
            if (inside) return 0;
        }
        return best;
    }

    // Hermite-Mittellinie wie im Spiel (Tangentenlänge = Knotenabstand)
    private static (Vector3 P, Vector2 Right) Hermite(Road it, float t)
    {
        var a = it.Node.Position; var b = it.ForwardNode.Position; float L = Vector3.Distance(a, b);
        var t0 = Vector3.Transform(-Vector3.UnitZ, it.Node.Rotation) * L; var t1 = Vector3.Transform(-Vector3.UnitZ, it.ForwardNode.Rotation) * L;
        if (Vector3.Dot(t0, b - a) < 0) t0 = -t0; if (Vector3.Dot(t1, b - a) < 0) t1 = -t1;
        float t2 = t * t, t3 = t2 * t;
        var p = (2 * t3 - 3 * t2 + 1) * a + (t3 - 2 * t2 + t) * t0 + (-2 * t3 + 3 * t2) * b + (t3 - t2) * t1;
        var d = (6 * t2 - 6 * t) * a + (3 * t2 - 4 * t + 1) * t0 + (-6 * t2 + 6 * t) * b + (3 * t2 - 2 * t) * t1;
        var dir = new Vector2(d.X, d.Z); dir = dir.LengthSquared() > 1e-8f ? Vector2.Normalize(dir) : Vector2.UnitX;
        return (p, new Vector2(-dir.Y, dir.X));
    }
}
