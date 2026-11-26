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

    /// <summary>OSM-Lage und OSM-Läufe vor jeder Verschiebung (für die Prefab-Achse).</summary>
    public static readonly Dictionary<RoadEntry, (List<double[]> Points, List<RunEntry> Runs)> Original = [];
    /// <summary>OSM-Linie je Straße (nur Lage, bleibt beim Teilen erhalten: beide Teile bekommen die
    /// verbundene Linie).</summary>
    public static readonly Dictionary<RoadEntry, List<System.Numerics.Vector2>> OsmLine = [];
    public static bool PullRampNodes = Environment.GetEnvironmentVariable("OSM2ETS_LC_PULL") == "1";
    public static void JoinLines(RoadEntry a, RoadEntry b)
    {
        if (!OsmLine.TryGetValue(a, out var la) || !OsmLine.TryGetValue(b, out var lb)) return;
        var j = la.Concat(lb.Skip(1)).ToList();
        OsmLine[a] = j; OsmLine[b] = j;
    }

    private static Vector2 P(double[] p) => new((float)p[0], (float)p[2]);

    /// <summary>Wechsel 1+1 ↔ 2+1 ohne Übergang, an denen die Linie gerade durchläuft (für das Protokoll).</summary>
    public static int Kept;

    /// <param name="plainChanges">Knoten, an denen 1+1 und 2+1 ohne Übergangs-Prefab wechseln
    /// (<see cref="Junctions.PlainLaneChanges"/>). Ist die Landstraße dort kürzer als <see cref="Ramp"/>, bleibt
    /// sie ganz in ihrer Lage (Knotenversatz wie an ihrem anderen Ende): Die Spur der Gegenrichtung läuft
    /// gerade weiter, die zusätzliche Spur kommt außen hinzu. Die Verschiebung zur Mitte der 2+1-Straße
    /// übernimmt dann die 2+1-Straße; ist sie kürzer als 2 × <see cref="Ramp"/>, wird sie nicht auf ihre
    /// eigene Mitte gesetzt, sondern läuft gleichmäßig zwischen den Lagen an ihren Enden. Sonst schlug das
    /// kurze Stück einen S-Bogen auf wenigen Metern (Zufahrt Autohof an der B 85: 4,7 m auf 8 m).</param>
    public static void Apply(List<RoadEntry> roads, Func<RunEntry, ResolvedStyle> resolve, IReadOnlySet<long>? plainChanges = null)
    {
        Shifted = 0; Kept = 0;
        Original.Clear();
        OsmLine.Clear();
        foreach (var r in roads) OsmLine[r] = r.Points.Select(p => new System.Numerics.Vector2((float)p[0], (float)p[2])).ToList();
        foreach (var r in roads)
            Original[r] = (r.Points.Select(p => (double[])p.Clone()).ToList(),
                           r.Runs.Select(x => new RunEntry { From = x.From, To = x.To, Lanes = x.Lanes, Highway = x.Highway, Oneway = x.Oneway }).ToList());
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
        // Wechsel 1+1 ↔ 2+1 ohne Übergang mit kurzer Landstraße: Linie der Landstraße beibehalten
        var noCenter = new HashSet<RoadEntry>();
        foreach (var id in plainChanges ?? new HashSet<long>())
        {
            if (!node.ContainsKey(id)) continue;
            var at = roads.Where(r => r.Points.Count >= 2 && (r.Start is { Kind: "node" } && r.Start.OsmNode == id || r.End is { Kind: "node" } && r.End.OsmNode == id)).ToList();
            if (at.Count != 2 || at[0] == at[1]) continue;
            int K(RoadEntry r) => r.Start?.OsmNode == id ? 0 : r.Points.Count - 2;
            var (a, b) = St(at[0], K(at[0])).Width < St(at[1], K(at[1])).Width ? (at[0], at[1]) : (at[1], at[0]);
            if (Interchanges.ArcLengthOf(a) >= Ramp) continue;          // genug Platz für die Rampe auf der Landstraße
            var far = a.Start?.OsmNode == id ? a.End : a.Start;
            float ta = EndTarget(far, node, out var tf) ? tf : Seg(a, a.Start?.OsmNode == id ? a.Points.Count - 2 : 0);
            node[id] = (ta, node[id].N);
            Kept++;
            if (MathF.Abs(ta - Seg(b, K(b))) > 0.01f && Interchanges.ArcLengthOf(b) < 2 * Ramp) noCenter.Add(b);
        }

        // Knoten mit Rampe (Anschlussstelle): früher Lage der OSM-Linie behalten. Abgeschaltet
        // (OSM2ETS_LC_PULL=1 schaltet es ein): die Prefabs sitzen auf der Modelllinie, das Zurückziehen
        // auf 50 m machte die gerade Autobahn vor jeder Anschlussstelle „rund“; der Rest wird an den
        // Prefabs über 150 m angeglichen (Interchanges.LateralTaper)
        if (PullRampNodes)
        foreach (var r in roads)
            if (r.Runs.Any(x => x.Highway is "motorway_link" or "trunk_link"))
                foreach (var e in new[] { r.Start, r.End })
                    if (e?.Kind == "node" && e.OsmNode is long id && node.ContainsKey(id))
                        node[id] = (0f, node[id].N);

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
            double len = s[^1];
            if (noCenter.Contains(r))
            {
                // kurzes 2+1-Stück an einem Wechsel ohne Übergang: gleichmäßig zwischen den Lagen an den Enden
                float t0 = EndTarget(r.Start, node, out var q0) ? q0 : 0f, t1 = EndTarget(r.End, node, out var q1) ? q1 : 0f;
                for (int i = 0; i < n; i++) off[i] = t0 + (t1 - t0) * (float)(len > 0 ? s[i] / len : 0);
            }
            else
            for (int i = 0; i < n; i++)
            {
                double a = Math.Max(0, s[i] - Ramp / 2), b = Math.Min(s[^1], s[i] + Ramp / 2);
                int m = 9; float acc = 0;
                for (int j = 0; j < m; j++) acc += SegAt(a + (b - a) * j / (m - 1));
                off[i] = acc / m;
            }
            // Enden auf den Knotenwert ziehen
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
