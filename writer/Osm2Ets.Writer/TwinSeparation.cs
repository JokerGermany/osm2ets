// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;

namespace Osm2Ets.Writer;

/// <summary>
/// Richtungsfahrbahnen (Autobahn/Kraftfahrstraße, Einbahn) liegen in OSM oft enger
/// nebeneinander, als die ETS2-Straßenmodelle mit Seitenstreifen breit sind – sie
/// überlagern sich dann. Hier werden beide Fahrbahnen eines Paars seitlich so weit
/// auseinandergeschoben, dass die Modelle nebeneinander passen. Die Verschiebung läuft
/// entlang der Straße weich ein und aus; Straßen, die an einem verschobenen Knoten
/// enden (Rampen, Fortsetzungen), werden mitgenommen.
/// </summary>
public static class TwinSeparation
{
    public const float Gap = 0f;            // Luft zwischen den Modellrändern (m)
    public const float MaxGap = 30f;      // nur Paare näher als das
    public const float Taper = 200f;      // Ein-/Auslauflänge der Verschiebung (m)

    public const float PrefabReach = 150f;  // Prefabs einer Anschlussstelle reichen so weit vom Knoten
    public const float PrefabExtra = 0f;    // zusätzliche Spur im Prefab (m)

    public static int Moved;
    public static float MaxShift;

    private static bool IsTwinCandidate(RoadEntry r) =>
        r.Runs.Count > 0 && r.Runs.All(x => x.Oneway && x.Highway is "motorway" or "trunk");

    private static Vector2 P(double[] p) => new((float)p[0], (float)p[2]);

    public static void Apply(List<RoadEntry> roads, Func<RunEntry, float> halfWidth, List<string> warnings)
    {
        Moved = 0; MaxShift = 0;
        var cand = roads.Where(IsTwinCandidate).Where(r => r.Points.Count >= 2).ToList();
        if (cand.Count < 2) return;
        var orig = roads.ToDictionary(r => r, r => r.Points.Select(P).ToArray());

        // Segmente aller Kandidaten mit Richtung und halber Breite
        var segs = new List<(RoadEntry R, Vector2 A, Vector2 B, Vector2 T, float Hw)>();
        foreach (var r in cand)
            for (int i = 0; i < r.Points.Count - 1; i++)
            {
                var a = P(r.Points[i]); var b = P(r.Points[i + 1]);
                var d = b - a; if (d.LengthSquared() < 1e-6f) continue;
                segs.Add((r, a, b, Vector2.Normalize(d), halfWidth(RunAt(r, i))));
            }

        // Anschlussstellen: dort werden die Fahrbahnen durch Prefabs um eine Spur breiter
        var rampNodes = new List<Vector2>();
        foreach (var r in roads)
            if (r.Runs.Any(x => x.Highway is "motorway_link" or "trunk_link"))
            {
                rampNodes.Add(P(r.Points[0])); rampNodes.Add(P(r.Points[^1]));
            }
        float Extra(Vector2 q) => rampNodes.Any(m => Vector2.Distance(m, q) < PrefabReach) ? PrefabExtra : 0f;

        // je Punkt nötige Verschiebung entlang der eigenen Normalen (vorzeichenbehaftet)
        var shift = new Dictionary<RoadEntry, float[]>();
        foreach (var r in cand)
        {
            int n = r.Points.Count;
            var push = new float[n];
            var s = Arc(r);
            for (int i = 0; i < n; i++)
            {
                var p = P(r.Points[i]);
                var t = Tangent(r, i);
                var nrm = new Vector2(-t.Y, t.X);
                float hw = halfWidth(RunAt(r, Math.Min(i, n - 2))) + Extra(p);
                float best = 0;
                foreach (var g in segs)
                {
                    if (g.R == r) continue;
                    if (Vector2.Dot(g.T, t) > -0.95f) continue;   // nur Gegenrichtung
                    var ab = g.B - g.A; float l2 = ab.LengthSquared();
                    float u = Math.Clamp(Vector2.Dot(p - g.A, ab) / l2, 0, 1);
                    var c = g.A + ab * u;
                    if (MathF.Abs(Vector2.Dot(p - c, g.T)) > 1f) continue;   // nur echtes Nebeneinander
                    float d = Vector2.Distance(p, c);
                    if (d > MaxGap || d < 1e-3f) continue;
                    float need = hw + g.Hw + Extra(c) + Gap - d;
                    if (need <= 0) continue;
                    float side = MathF.Sign(Vector2.Dot(p - c, nrm));
                    float v = side * need / 2;
                    if (MathF.Abs(v) > MathF.Abs(best)) best = v;
                    if (Environment.GetEnvironmentVariable("OSM2ETS_TWINDEBUG") is not null && need > 2)
                        Console.Error.WriteLine($"pair {r.Id}[{i}] vs {g.R.Id} d {d:0.0} hw {hw:0.0}+{g.Hw:0.0} need {need:0.0}");
                }
                push[i] = best;
            }
            // weich ein-/auslaufen: Hüllkurve mit linearem Abfall über Taper
            var sm = new float[n];
            for (int i = 0; i < n; i++)
            {
                float v = 0;
                for (int j = 0; j < n; j++)
                {
                    if (push[j] == 0) continue;
                    float f = 1 - MathF.Abs((float)(s[i] - s[j])) / Taper;
                    if (f <= 0) continue;
                    float c = push[j] * f;
                    if (MathF.Abs(c) > MathF.Abs(v)) v = c;
                }
                sm[i] = v;
            }
            if (sm.Any(x => x != 0)) shift[r] = sm;
        }
        if (shift.Count == 0) return;

        // Knotenverschiebungen aus den Kandidaten-Enden (gemittelt)
        var nodeDelta = new Dictionary<long, (Vector2 Sum, int N)>();
        var moved = new Dictionary<RoadEntry, Vector2[]>();
        foreach (var (r, sm) in shift)
        {
            int n = r.Points.Count;
            var dv = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                var t = Tangent(r, i);
                dv[i] = new Vector2(-t.Y, t.X) * sm[i];
            }
            moved[r] = dv;
            foreach (var (info, k) in new[] { (r.Start, 0), (r.End, n - 1) })
                if (info?.Kind == "node" && info.OsmNode is long id)
                {
                    nodeDelta.TryGetValue(id, out var e);
                    nodeDelta[id] = (e.Sum + dv[k], e.N + 1);
                }
        }

        // Original-Lagen der verschobenen Punkte (für die Mitnahme naher Straßen)
        static IEnumerable<long> EndNodes(RoadEntry r) =>
            new[] { r.Start, r.End }.Where(e => e?.Kind == "node" && e.OsmNode is long).Select(e => e!.OsmNode!.Value);

        // angeschlossene Straßen (Rampen): in der Nähe der verschobenen Fahrbahn genauso
        // mitverschieben, damit sie ihre Lage zur Fahrbahn behalten
        var attached = roads.Where(r => !moved.ContainsKey(r) && new[] { r.Start, r.End }
            .Any(e => e?.Kind == "node" && e.OsmNode is long id && nodeDelta.ContainsKey(id))).ToList();
        foreach (var r in attached)
        {
            // nur die Fahrbahnen, an die diese Straße angeschlossen ist (nicht die Gegenrichtung)
            var mine = EndNodes(r).ToHashSet();
            var src = new List<(Vector2 P, Vector2 D)>();
            foreach (var (m, dv) in moved)
                if (EndNodes(m).Any(mine.Contains))
                    for (int i = 0; i < m.Points.Count; i++) src.Add((P(m.Points[i]), dv[i]));
            foreach (var p in r.Points)
            {
                var q = P(p);
                float bd = float.MaxValue; Vector2 bv = default;
                foreach (var (sp, sd) in src)
                {
                    float d = Vector2.DistanceSquared(sp, q);
                    if (d < bd) { bd = d; bv = sd; }
                }
                float w = Math.Clamp(1 - (MathF.Sqrt(bd) - 30f) / 50f, 0, 1);
                p[0] += bv.X * w; p[2] += bv.Y * w;
            }
        }

        foreach (var (r, dv) in moved)
            for (int i = 0; i < r.Points.Count; i++)
            {
                r.Points[i][0] += dv[i].X; r.Points[i][2] += dv[i].Y;
                MaxShift = MathF.Max(MaxShift, dv[i].Length());
                if (Environment.GetEnvironmentVariable("OSM2ETS_TWINDEBUG") is not null && dv[i].Length() > 1f)
                    Console.Error.WriteLine($"twin {r.Id}[{i}] ({r.Points[i][0]:0},{r.Points[i][2]:0}) {dv[i].Length():0.0} m");
            }
        Moved = moved.Count;

        // Enden an Knoten angleichen und angeschlossene Straßen mitziehen
        foreach (var r in roads)
        {
            int n = r.Points.Count;
            var s = Arc(r);
            foreach (var atStart in new[] { true, false })
            {
                var info = atStart ? r.Start : r.End;
                if (info?.Kind != "node" || info.OsmNode is not long id || !nodeDelta.TryGetValue(id, out var e)) continue;
                var target = e.Sum / e.N;
                int k = atStart ? 0 : n - 1;
                var corr = target + orig[r][k] - P(r.Points[k]) ;
                if (corr.LengthSquared() < 1e-6f) continue;
                double len = s[^1];
                float follow = 30f;
                for (int i = 0; i < n; i++)
                {
                    double dist = atStart ? s[i] : len - s[i];
                    float f = 1 - (float)(dist / Math.Min(follow, len * 0.5));
                    if (f <= 0) continue;
                    f = f * f * (3 - 2 * f);
                    r.Points[i][0] += corr.X * f; r.Points[i][2] += corr.Y * f;
                }
            }
        }
        warnings.Add($"Info: Richtungsfahrbahnen auseinandergerückt: {Moved} Straßen, höchstens {MaxShift:0.0} m");
    }

    private static RunEntry RunAt(RoadEntry r, int seg) =>
        r.Runs.FirstOrDefault(x => x.From <= seg && seg < x.To) ?? r.Runs[^1];

    private static Vector2 Tangent(RoadEntry r, int i)
    {
        int n = r.Points.Count;
        var a = P(r.Points[Math.Max(i - 1, 0)]); var b = P(r.Points[Math.Min(i + 1, n - 1)]);
        var d = b - a;
        return d.LengthSquared() > 1e-8f ? Vector2.Normalize(d) : Vector2.UnitX;
    }

    private static double[] Arc(RoadEntry r)
    {
        var s = new double[r.Points.Count];
        for (int i = 1; i < s.Length; i++) s[i] = s[i - 1] + Vector2.Distance(P(r.Points[i - 1]), P(r.Points[i]));
        return s;
    }
}
