namespace Osm2Ets.Writer;

/// <summary>
/// Kleine Rampen-Dreiecke an Straßen zusammenfassen: Eine Rampe teilt sich kurz vor einer Straße in
/// zwei Arme (Links-/Rechtsabbieger), die beide auf diese Straße treffen. Zwischen den Armen liegt
/// nur ein kurzes Straßenstück. Für zwei Einmündungs-Prefabs und zwei Bögen ist dort kein Platz
/// (BT 46 an der A 9: Arme 19 und 31 m, Straße dazwischen 24 m – Keil und 94 m² Überdeckung).
/// Dann enden beide Arme und das Straßenstück in einer einzigen T-Einmündung: das Straßenstück wird
/// in der Mitte geteilt und die Rampe bis dorthin verlängert. Aus: OSM2ETS_NO_TRIANGLES=1.
/// </summary>
internal static class Triangles
{
    public static bool Enabled = Environment.GetEnvironmentVariable("OSM2ETS_NO_TRIANGLES") != "1";
    public const double MaxArm = 45, MaxBase = 60;
    private static long synth = -2_000_000;

    private static bool IsLink(RoadEntry r) => r.Runs.All(x => x.Highway.EndsWith("_link"));
    private static long? Node(EndInfo? e) => e?.Kind == "node" ? e.OsmNode : null;
    private static double Len(RoadEntry r)
    {
        double s = 0;
        for (int i = 0; i < r.Points.Count - 1; i++)
        {
            var a = r.Points[i]; var b = r.Points[i + 1];
            s += Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]) + (b[2] - a[2]) * (b[2] - a[2]));
        }
        return s;
    }

    public static int Collapse(List<RoadEntry> roads, List<string> warnings)
    {
        if (!Enabled) return 0;
        int n = 0;
        var ends = new Dictionary<long, List<RoadEntry>>();
        foreach (var r in roads)
            foreach (var id in new[] { Node(r.Start), Node(r.End) })
                if (id is long k) (ends.TryGetValue(k, out var l) ? l : ends[k] = []).Add(r);
        foreach (var (apex, at) in ends.ToList())
        {
            if (at.Count != 3) continue;
            var arms = at.Where(r => IsLink(r) && Len(r) <= MaxArm).ToList();
            if (arms.Count != 2) continue;
            var stem = at.Except(arms).Single();
            if (!IsLink(stem) || !roads.Contains(stem)) continue;
            long? Other(RoadEntry r) => Node(r.Start) == apex ? Node(r.End) : Node(r.Start);
            var a = Other(arms[0]); var b = Other(arms[1]);
            if (a is null || b is null || a == b) continue;
            // Straßenstück direkt zwischen den beiden Armenden
            var bas = roads.FirstOrDefault(r => !IsLink(r) && Len(r) <= MaxBase &&
                ((Node(r.Start) == a && Node(r.End) == b) || (Node(r.Start) == b && Node(r.End) == a)));
            if (bas is null || !roads.Contains(arms[0]) || !roads.Contains(arms[1])) continue;
            // Straßenstück in der Mitte teilen (an einem Punkt in der Mitte der Länge)
            double half = Len(bas) / 2, acc = 0; int k = 0;
            while (k < bas.Points.Count - 2 && acc + Seg(bas, k) < half) { acc += Seg(bas, k); k++; }
            double t = Math.Clamp((half - acc) / Math.Max(Seg(bas, k), 1e-6), 0, 1);
            var p0 = bas.Points[k]; var p1 = bas.Points[k + 1];
            var mid = new[] { p0[0] + (p1[0] - p0[0]) * t, p0[1] + (p1[1] - p0[1]) * t, p0[2] + (p1[2] - p0[2]) * t };
            long id = synth--;
            var second = new RoadEntry
            {
                Id = bas.Id + "t", Chain = bas.Chain, Piece = bas.Piece, Role = bas.Role,
                Start = new EndInfo { Kind = "node", OsmNode = id }, End = bas.End,
                Points = new List<double[]> { mid }.Concat(bas.Points.Skip(k + 1)).ToList(),
                Runs = bas.Runs.Where(x => x.To > k).Select(x => x.Span(Math.Max(0, x.From - k), x.To - k)).ToList(),
            };
            int cnt = k + 2;   // Punkte 0..k und mid
            bas.Runs = bas.Runs.Where(x => x.From < k + 1).Select(x => x.Span(x.From, Math.Min(x.To, k + 1))).ToList();
            bas.Points = bas.Points.Take(k + 1).Append(mid).ToList();
            bas.End = new EndInfo { Kind = "node", OsmNode = id };
            if (bas.Points.Count != cnt) continue;
            // Rampe vom Scheitel bis zur Mitte verlängern
            var apexPt = Node(stem.End) == apex ? stem.Points[^1] : stem.Points[0];
            var ext = new[] { apexPt[0] + (mid[0] - apexPt[0]) * 0.5, apexPt[1] + (mid[1] - apexPt[1]) * 0.5, apexPt[2] + (mid[2] - apexPt[2]) * 0.5 };
            if (Node(stem.End) == apex)
            {
                stem.Points = stem.Points.Append(ext).Append(mid).ToList();
                stem.Runs[^1].To = stem.Points.Count - 1;
                stem.End = new EndInfo { Kind = "node", OsmNode = id };
            }
            else
            {
                stem.Points = new List<double[]> { mid, ext }.Concat(stem.Points).ToList();
                foreach (var x in stem.Runs) { x.From += 2; x.To += 2; }
                stem.Runs[0].From = 0;
                stem.Start = new EndInfo { Kind = "node", OsmNode = id };
            }
            roads.Remove(arms[0]); roads.Remove(arms[1]);
            roads.Insert(roads.IndexOf(bas) + 1, second);
            // an den früheren Armenden hängen jetzt nur noch zwei Straßen: zu einer verbinden, sonst
            // sind die Hälften für die Arme des Einmündungs-Prefabs zu kurz
            foreach (var (piece, node) in new[] { (bas, Node(bas.Start)), (second, Node(second.End)) })
                if (node is long nd) Merge(roads, piece, nd);
            warnings.Add($"Info: Rampen-Dreieck am Knoten {apex} zu einer Einmündung zusammengefasst ({arms[0].Id}, {arms[1].Id} entfallen)");
            n++;
        }
        return n;
    }

    /// <summary>Straße r mit der einzigen anderen Straße am Knoten node verbinden (r wird
    /// verlängert, die andere entfernt), wenn dort genau zwei Straßen enden.</summary>
    private static void Merge(List<RoadEntry> roads, RoadEntry r, long node)
    {
        var at = roads.Where(x => x != r && (Node(x.Start) == node || Node(x.End) == node)).ToList();
        if (at.Count != 1) return;
        var o = at[0];
        if (o.Closed || r.Closed) return;
        bool rStart = Node(r.Start) == node, oStart = Node(o.Start) == node;
        // o so ausrichten, dass es an r anschließt
        List<double[]> op = o.Points.ToList(); var oruns = o.Runs.Select(x => x.Span(x.From, x.To)).ToList();
        int on = op.Count - 1;
        if (rStart == oStart)
        {
            // Richtung von o umdrehen geht nur ohne Einbahn
            if (o.Runs.Any(x => x.Oneway)) return;
            op.Reverse();
            oruns = oruns.Select(x => x.Span(on - x.To, on - x.From)).Reverse().ToList();
        }
        if (rStart)
        {
            // o vorn anhängen
            int add = on;
            foreach (var x in r.Runs) { x.From += add; x.To += add; }
            r.Points = op.Concat(r.Points.Skip(1)).ToList();
            r.Runs = oruns.Concat(r.Runs).ToList();
            r.Start = rStart == oStart ? o.End : o.Start;
        }
        else
        {
            int add = r.Points.Count - 1;
            r.Points = r.Points.Concat(op.Skip(1)).ToList();
            r.Runs = r.Runs.Concat(oruns.Select(x => x.Span(x.From + add, x.To + add))).ToList();
            r.End = rStart == oStart ? o.Start : o.End;
        }
        r.LengthM += o.LengthM;
        roads.Remove(o);
    }

    private static double Seg(RoadEntry r, int i)
    {
        var a = r.Points[i]; var b = r.Points[i + 1];
        return Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[2] - a[2]) * (b[2] - a[2]));
    }
}
