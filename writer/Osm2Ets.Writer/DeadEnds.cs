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
    public const float Extra = 0.5f;
    /// <summary>Sperre an einer Autobahn: so weit außerhalb ihres Modellrands (m).</summary>
    public const float EdgeGap = 0.5f;

    public sealed record Placed(ulong Uid, string Road, string End, long? OsmNode, List<long> OsmWays, float X, float Y, float Z);

    public static List<Placed> Apply(Map map, List<RoadEntry> roads, Dictionary<RoadEntry, ResolvedStyle[]> styles,
                                     Dictionary<RoadEntry, Road> first, Dictionary<RoadEntry, Road> last)
    {
        var placed = new List<Placed>();
        var done = new HashSet<INode>();
        Networks = 0;
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
        var majorHw = new List<float>();
        foreach (var r in roads)
            if (r.Runs.Any(x => x.Highway is "motorway" or "motorway_link"))
                for (int i = 1; i < r.Points.Count; i++)
                {
                    majorSegs.Add((new((float)r.Points[i - 1][0], (float)r.Points[i - 1][2]), new((float)r.Points[i][0], (float)r.Points[i][2])));
                    var st = styles.TryGetValue(r, out var sa) ? sa[Math.Min(i - 1, sa.Length - 1)] : null;
                    majorHw.Add(st is null ? 0 : st.Width / 2 + MathF.Abs(st.ModelCenter));
                }
        // Abstand vom Punkt bis zum Modellrand der nächsten Autobahn/Rampe (negativ = darin) und deren Richtung
        (float, Vector2) EdgeInfo(Vector2 p)
        {
            float best = float.MaxValue; Vector2 tg = Vector2.Zero;
            for (int k = 0; k < majorSegs.Count; k++)
            {
                var (a, b) = majorSegs[k];
                var ab = b - a; float l2 = ab.LengthSquared();
                float t = l2 > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0, 1) : 0;
                float e = Vector2.Distance(p, a + ab * t) - majorHw[k];
                if (e < best) { best = e; tg = l2 > 0 ? ab / MathF.Sqrt(l2) : Vector2.Zero; }
            }
            return (best, tg);
        }
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
            if (Overlaps.Through.Contains(r)) continue;   // Verbindung zwischen zwei Fahrbahnen: keine Sperren
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
                // an der Autobahn: Sperre am Rand der Autobahn, parallel zu ihr (die Zufahrt läuft
                // schräg auf den Randstreifen, ihr Ende ragt hinein)
                var at = new Vector2(p.X, p.Z);
                var nn = new Vector2(-d.Y, d.X);
                float half = styles[r][atStart ? 0 : ^1].Width / 2 + Extra;
                if (other is null)
                {
                    var (ed, tg) = EdgeInfo(at);
                    if (ed < EdgeGap) at += d * (EdgeGap - ed);
                    // längs zur Autobahn nur bei steil auftreffender Zufahrt; endet sie in einer
                    // Übergangsspur parallel zur Autobahn, steht die Sperre quer zu dieser
                    if (tg != Vector2.Zero && MathF.Abs(Vector2.Dot(d, tg)) < 0.7f)
                    {
                        float sin = MathF.Max(0.5f, MathF.Abs(d.X * tg.Y - d.Y * tg.X));
                        nn = tg; half /= sin;
                    }
                }
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
        // Sackgassen-Netze: Arm einer erreichbaren Kreuzung, hinter dem die Straßen (auch Sichtstücke
        // ohne Verkehr, z. B. Zufahrt zu einem Schnellrestaurant) weder eine andere Kreuzung noch eine
        // Autobahn noch ein offenes Ende erreichen – im Spiel eine Sackgasse ohne Wendemöglichkeit
        if (UseNetworks)
        {
            var entryOf = new Dictionary<IMapItem, (RoadEntry E, bool AtStart)>();
            foreach (var r in roads)
            {
                if (first.TryGetValue(r, out var f1)) entryOf[f1] = (r, true);
                if (last.TryGetValue(r, out var l1)) entryOf.TryAdd(l1, (r, false));
            }
            var byNode = new Dictionary<long, List<RoadEntry>>();
            foreach (var r in roads)
                foreach (var e in new[] { r.Start, r.End })
                    if (e?.OsmNode is long on) (byNode.TryGetValue(on, out var l) ? l : byNode[on] = []).Add(r);
            Prefab? PrefabAt(RoadEntry r, bool atStart)
            {
                if (atStart) return first.TryGetValue(r, out var f) ? f.Node.BackwardItem as Prefab : null;
                return last.TryGetValue(r, out var l) ? l.ForwardNode.ForwardItem as Prefab : null;
            }
            foreach (var pf in reach.OfType<Prefab>())
                foreach (var nd in pf.Nodes)
                {
                    var arm = (IMapItem?)(ReferenceEquals(nd.ForwardItem, pf) ? nd.BackwardItem : nd.ForwardItem);
                    if (arm is not Road || !entryOf.TryGetValue(arm, out var ea) || done.Contains(nd)) continue;
                    var (e0, s0) = ea;
                    var near = s0 ? e0.Start?.OsmNode : e0.End?.OsmNode;
                    bool escape = false; int seen = 0;
                    var vis = new HashSet<RoadEntry>(); var st2 = new Stack<RoadEntry>(); st2.Push(e0);
                    while (st2.Count > 0 && !escape)
                    {
                        var r = st2.Pop();
                        if (!vis.Add(r)) continue;
                        if (++seen > 300) { escape = true; break; }
                        if (r.Runs.Any(x => x.Highway is "motorway" or "motorway_link" or "trunk" or "trunk_link")) { escape = true; break; }
                        foreach (var atS in new[] { true, false })
                        {
                            var e = atS ? r.Start : r.End;
                            if (e is { Open: true }) { escape = true; break; }
                            var pa = PrefabAt(r, atS);
                            if (pa is not null && !ReferenceEquals(pa, pf)) { escape = true; break; }
                            if (e?.OsmNode is long on && on != near && byNode.TryGetValue(on, out var nb))
                                foreach (var o in nb) if (!vis.Contains(o)) st2.Push(o);
                        }
                    }
                    if (escape || !done.Add(nd)) continue;
                    var p = nd.Position;
                    var rd0 = (Road)arm;
                    var q = ReferenceEquals(rd0.Node, nd) ? rd0.ForwardNode.Position : rd0.Node.Position;
                    var d = Vector2.Normalize(new Vector2(q.X - p.X, q.Z - p.Z));
                    if (float.IsNaN(d.X)) continue;
                    var st = styles[e0][s0 ? 0 : ^1];
                    var at = new Vector2(p.X, p.Z) + d * Inset;
                    var n = new Vector2(-d.Y, d.X);
                    float half = st.Width / 2 + Extra;
                    var item = Buildings.Add(map, new Vector3(at.X + n.X * half, p.Y, at.Y + n.Y * half), new Vector3(at.X - n.X * half, p.Y, at.Y - n.Y * half), Scheme);
                    item.Stretch = 1f;
                    placed.Add(new Placed(item.Uid, e0.Id, s0 ? "start" : "end", near, (s0 ? e0.Runs[0] : e0.Runs[^1]).OsmWays, p.X, p.Y, p.Z));
                    Networks++;
                }
        }
        return placed;
    }

    /// <summary>Sperre auch vor Sackgassen-Netzen (aus: OSM2ETS_DEAD_END_NO_NETWORK=1).</summary>
    public static bool UseNetworks = Environment.GetEnvironmentVariable("OSM2ETS_DEAD_END_NO_NETWORK") != "1";
    public static int Networks;

    public static void WriteSidecar(string path, List<Placed> placed)
    {
        var doc = new { format = "osm2ets/sidecar", version = 1, dead_ends = placed.Select(x => new {
            uid = x.Uid.ToString("X16"), road = x.Road, end = x.End, osm_node = x.OsmNode, osm_ways = x.OsmWays, position = new[] { x.X, x.Y, x.Z } }) };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }
}
