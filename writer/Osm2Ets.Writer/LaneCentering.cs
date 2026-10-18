// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;

namespace Osm2Ets.Writer;

/// <summary>
/// OSM zeichnet eine Fahrbahn mittig ein. Manche ETS2-Straßenmodelle liegen nicht mittig auf
/// ihrer Linie (z. B. ger16 „hw 3 lanes one way“: Spuren von −9,0 bis +4,7 m, Seitenstreifen
/// bis +8,0 m). Die Linie wird deshalb je Stil um <see cref="ResolvedStyle.Offset"/> seitlich
/// verschoben, so dass die Fahrspuren mittig auf der OSM-Linie liegen. Wechsel laufen weich
/// über <see cref="Ramp"/> Meter; an Knoten treffen sich alle Straßen im Mittel ihrer Versätze.
/// </summary>
public static class LaneCentering
{
    public const float Ramp = 50f;
    public static int Shifted;

    private static Vector2 P(double[] p) => new((float)p[0], (float)p[2]);

    public static void Apply(List<RoadEntry> roads, Func<RunEntry, ResolvedStyle> resolve)
    {
        Shifted = 0;
        ResolvedStyle St(RoadEntry r, int k) => resolve(r.Runs.FirstOrDefault(x => x.From <= k && k < x.To) ?? r.Runs[^1]);
        float Seg(RoadEntry r, int k) => St(r, k).Offset;

        // Versatz am Knoten: der der breitesten angeschlossenen Straße (die Hauptfahrbahn bleibt,
        // Rampen passen sich an); bei gleicher Breite das Mittel
        var node = new Dictionary<long, (float Sum, int N)>();
        var widest = new Dictionary<long, float>();
        var mainCount = new Dictionary<long, int>();
        foreach (var r in roads)
        {
            if (r.Points.Count < 2) continue;
            foreach (var (info, k) in new[] { (r.Start, 0), (r.End, r.Points.Count - 2) })
                if (info?.Kind == "node" && info.OsmNode is long id)
                    widest[id] = MathF.Max(widest.GetValueOrDefault(id), St(r, k).Width);
        }
        foreach (var r in roads)
        {
            if (r.Points.Count < 2) continue;
            foreach (var (info, k) in new[] { (r.Start, 0), (r.End, r.Points.Count - 2) })
                if (info?.Kind == "node" && info.OsmNode is long id)
                {
                    node.TryGetValue(id, out var e);
                    bool main = St(r, k).Width >= widest[id] - 0.01f;
                    // N zählt alle Enden (für "Knoten mit ≥ 2 Enden"), Sum/M nur die breitesten
                    node[id] = (e.Sum + (main ? Seg(r, k) : 0), e.N + 1);
                    if (main) mainCount[id] = mainCount.GetValueOrDefault(id) + 1;
                }
        }

        foreach (var id in node.Keys.ToList())
            node[id] = (node[id].Sum / mainCount[id], node[id].N);

        foreach (var r in roads)
        {
            int n = r.Points.Count;
            if (n < 2) continue;
            var seg = Enumerable.Range(0, n - 1).Select(k => Seg(r, k)).ToArray();
            if (seg.All(v => v == 0)
                && !EndTarget(r.Start, node, out _) && !EndTarget(r.End, node, out _)) continue;
            var s = new double[n];
            for (int i = 1; i < n; i++) s[i] = s[i - 1] + Vector2.Distance(P(r.Points[i - 1]), P(r.Points[i]));

            // Punktwert: Segmentwerte, über ±Ramp/2 gemittelt (weicher Übergang)
            float SegAt(double x)
            {
                int k = 0;
                while (k < n - 2 && s[k + 1] < x) k++;
                return seg[k];
            }
            var off = new float[n];
            for (int i = 0; i < n; i++)
            {
                double a = Math.Max(0, s[i] - Ramp / 2), b = Math.Min(s[^1], s[i] + Ramp / 2);
                int m = 9; float acc = 0;
                for (int j = 0; j < m; j++) acc += SegAt(a + (b - a) * j / (m - 1));
                off[i] = acc / m;
            }
            // Enden auf den Knotenwert ziehen
            double len = s[^1];
            foreach (var atStart in new[] { true, false })
            {
                if (!EndTarget(atStart ? r.Start : r.End, node, out float t)) continue;
                int k = atStart ? 0 : n - 1;
                float d = t - off[k];
                if (MathF.Abs(d) < 1e-4f) continue;
                double reach = Math.Min(Ramp, len / 2);
                for (int i = 0; i < n; i++)
                {
                    double dist = atStart ? s[i] : len - s[i];
                    float f = 1 - (float)(dist / reach);
                    if (f <= 0) continue;
                    off[i] += d * f * f * (3 - 2 * f);
                }
            }
            if (off.All(v => MathF.Abs(v) < 1e-4f)) continue;

            var orig = r.Points.Select(P).ToArray();
            for (int i = 0; i < n; i++)
            {
                var d = orig[Math.Min(i + 1, n - 1)] - orig[Math.Max(i - 1, 0)];
                if (d.LengthSquared() < 1e-8f) continue;
                d = Vector2.Normalize(d);
                var right = new Vector2(-d.Y, d.X);
                r.Points[i][0] += right.X * off[i];
                r.Points[i][2] += right.Y * off[i];
            }
            Shifted++;
        }
    }

    private static bool EndTarget(EndInfo? info, Dictionary<long, (float Sum, int N)> node, out float t)
    {
        t = 0;
        if (info?.Kind != "node" || info.OsmNode is not long id || !node.TryGetValue(id, out var e) || e.N < 2) return false;
        t = e.Sum;
        return true;
    }
}
