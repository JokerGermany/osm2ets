using System.Numerics;
using System.Text.Json;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Sperrbaken (Buildings „dead end“, scheme18) quer über befahrbare Straßenenden, die im Spiel ins
/// Nichts führen: in OSM geht die Straße auf befahrbaren Wegen weiter, die nicht in dieser Karte
/// sind (Stufengrenze), oder sie ist am Kartenrand abgeschnitten (Issue #2). Enden, die an einer
/// anderen Straße oder einem Prefab hängen, bleiben frei. Jede gesetzte Sperre steht mit uid und
/// OSM-Knoten in der Begleitdatei, damit eine spätere Stufe sie beim Weiterbauen entfernen kann.
/// Aus: OSM2ETS_NO_DEAD_ENDS=1.
/// </summary>
internal static class DeadEnds
{
    public static bool Enabled = Environment.GetEnvironmentVariable("OSM2ETS_NO_DEAD_ENDS") != "1";
    public const string Scheme = "scheme18";   // „dead end“, im Editor 7,09 m lang
    public const float Inset = 0.5f;           // so weit vor dem Straßenende (m)
    public const float Extra = 0.5f;           // je Seite über den Modellrand hinaus (m)

    public sealed record Placed(ulong Uid, string Road, string End, long? OsmNode, List<long> OsmWays, float X, float Y, float Z);

    public static List<Placed> Apply(Map map, List<RoadEntry> roads, Dictionary<RoadEntry, ResolvedStyle[]> styles,
                                     Dictionary<RoadEntry, Road> first, Dictionary<RoadEntry, Road> last)
    {
        var placed = new List<Placed>();
        var done = new HashSet<INode>();
        if (!Enabled) return placed;
        // nur Kreuzungen, die der Spieler von der Autobahn aus erreicht: Netz ab allen Autobahn- und
        // Rampenstücken über Knoten und Prefabs durchlaufen (Straßen ohne Anschluss an die Autobahn
        // sind in L1 nur Sichtstücke)
        var reach = new HashSet<IMapItem>();
        var stack = new Stack<IMapItem>();
        foreach (var r in roads)
            if (r.Runs.Any(x => x.Highway is "motorway" or "motorway_link" or "trunk" or "trunk_link") && first.TryGetValue(r, out var f0))
                stack.Push(f0);
        while (stack.Count > 0)
        {
            var it = stack.Pop();
            if (!reach.Add(it)) continue;
            IEnumerable<INode> nodes = it switch { Road rd => [rd.Node, rd.ForwardNode], Prefab pf => pf.Nodes, _ => [] };
            foreach (var nd in nodes)
                foreach (var o in new[] { nd.ForwardItem, nd.BackwardItem })
                    if (o is IMapItem oi && !reach.Contains(oi)) stack.Push(oi);
        }
        // Gesperrte Zufahrten (access=no|private) direkt an Autobahn/Rampe: Sperre am Anschluss,
        // damit der Stummel sichtbar, aber nicht befahrbar ist
        var major = new HashSet<IMapItem>();
        foreach (var r in roads)
            if (r.Runs.Any(x => x.Highway is "motorway" or "motorway_link") && first.TryGetValue(r, out var m0) && last.TryGetValue(r, out var m1))
            { major.Add(m0); major.Add(m1); }
        var majorSegs = new List<(Vector2, Vector2)>();
        foreach (var r in roads)
            if (r.Runs.Any(x => x.Highway is "motorway" or "motorway_link"))
                for (int i = 1; i < r.Points.Count; i++)
                    majorSegs.Add((new((float)r.Points[i - 1][0], (float)r.Points[i - 1][2]), new((float)r.Points[i][0], (float)r.Points[i][2])));
        bool NearMajor(Vector2 p, float tol)
        {
            foreach (var (a, b) in majorSegs)
            {
                var ab = b - a; float l2 = ab.LengthSquared();
                float t = l2 > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0, 1) : 0;
                if (Vector2.Distance(p, a + ab * t) <= tol) return true;
            }
            return false;
        }
        foreach (var r in roads)
        {
            if (!first.TryGetValue(r, out var fi) || !last.TryGetValue(r, out var la)) continue;
            foreach (var atStart in new[] { true, false })
            {
                var run = atStart ? r.Runs[0] : r.Runs[^1];
                if (run.Access is null) continue;
                var road = atStart ? fi : la;
                var node = atStart ? fi.Node : la.ForwardNode;
                var other = (IMapItem?)(atStart ? node.BackwardItem : node.ForwardItem);
                bool atMajor = other is not null && (major.Contains(other)
                    || other is Prefab pm && pm.Nodes.Any(n => (n.ForwardItem is IMapItem a && major.Contains(a)) || (n.BackwardItem is IMapItem b && major.Contains(b))));
                // Abzweig ohne Prefab, am Fahrbahnrand gekürzt: OSM-Endpunkt liegt auf der Autobahn
                // (Achse der Autobahn höchstens halbe Breite + Rand entfernt)
                if (!atMajor && other is null && NearMajor(new(node.Position.X, node.Position.Z), 25f)) atMajor = true;
                if (!atMajor || !done.Add(node)) continue;
                var p = node.Position;
                var q = atStart ? fi.ForwardNode.Position : la.Node.Position;
                var d = Vector2.Normalize(new Vector2(q.X - p.X, q.Z - p.Z));
                if (float.IsNaN(d.X)) continue;
                var at = new Vector2(p.X, p.Z) + d * Inset;
                var nn = new Vector2(-d.Y, d.X);
                float half = styles[r][atStart ? 0 : ^1].Width / 2 + Extra;
                var a2 = at + nn * half; var b2 = at - nn * half;
                var item = Buildings.Add(map, new Vector3(a2.X, p.Y, a2.Y), new Vector3(b2.X, p.Y, b2.Y), Scheme);
                item.Stretch = 1f;
                placed.Add(new Placed(item.Uid, r.Id, atStart ? "start" : "end", (atStart ? r.Start : r.End)?.OsmNode, run.OsmWays, p.X, p.Y, p.Z));
            }
        }
        foreach (var r in roads)
        {
            if (!first.TryGetValue(r, out var fi) || !last.TryGetValue(r, out var la)) continue;
            foreach (var atStart in new[] { true, false })
            {
                var e = atStart ? r.Start : r.End;
                if (e is not { Open: true }) continue;
                var node = atStart ? fi.Node : la.ForwardNode;
                // hängt am Knoten noch etwas anderes (Straße, Prefab), ist das Ende nicht offen
                var other = atStart ? node.BackwardItem : node.ForwardItem;
                if (other is not null) continue;
                var st = styles[r][atStart ? 0 : ^1];
                if (st.AiVehicles == false) continue;               // Wege ohne Verkehr: keine Sperre
                // Die Sperre gehört an die Kreuzung, an der die Sackgasse abzweigt (Issue #2): vom
                // offenen Ende die Straßenkette zurückgehen bis zum Prefab. Endet die Kette auch auf
                // der anderen Seite frei, ist das Stück nicht erreichbar – keine Sperre.
                IMapItem cur = atStart ? fi : la;
                var from = node;
                INode? junction = null; INode? inner = null;
                for (int step = 0; step < 500; step++)
                {
                    if (cur is not Road rd) break;
                    var far = ReferenceEquals(rd.Node, from) ? rd.ForwardNode : rd.Node;
                    var next = (IMapItem?)(ReferenceEquals(far.BackwardItem, rd) ? far.ForwardItem : far.BackwardItem);
                    if (next is Prefab pfj) { if (reach.Contains(pfj)) { junction = far; inner = from; } break; }
                    if (next is null) break;
                    from = far; cur = (IMapItem)next;
                }
                if (junction is null || inner is null) continue;
                if (!done.Add(junction)) continue;
                var p = junction.Position;
                var q = (cur as Road)!.Node == junction ? (cur as Road)!.ForwardNode.Position : (cur as Road)!.Node.Position;
                var d = Vector2.Normalize(new Vector2(q.X - p.X, q.Z - p.Z));   // von der Kreuzung in die Sackgasse
                if (float.IsNaN(d.X)) continue;
                var at = new Vector2(p.X, p.Z) + d * Inset;
                var n = new Vector2(-d.Y, d.X);
                float half = st.Width / 2 + Extra;
                var a = at + n * half; var b = at - n * half;
                var item = Buildings.Add(map, new Vector3(a.X, p.Y, a.Y), new Vector3(b.X, p.Y, b.Y), Scheme);
                item.Stretch = 1f;
                placed.Add(new Placed(item.Uid, r.Id, atStart ? "start" : "end", e.OsmNode, (atStart ? r.Runs[0] : r.Runs[^1]).OsmWays, p.X, p.Y, p.Z));
            }
        }
        return placed;
    }

    public static void WriteSidecar(string path, List<Placed> placed)
    {
        var doc = new { format = "osm2ets/sidecar", version = 1, dead_ends = placed.Select(x => new {
            uid = x.Uid.ToString("X16"), road = x.Road, end = x.End, osm_node = x.OsmNode, osm_ways = x.OsmWays, position = new[] { x.X, x.Y, x.Z } }) };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }
}
