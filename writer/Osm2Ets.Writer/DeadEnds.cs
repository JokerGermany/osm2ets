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
        if (!Enabled) return placed;
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
                var p = node.Position;
                var q = atStart ? fi.ForwardNode.Position : la.Node.Position;
                var d = Vector2.Normalize(new Vector2(q.X - p.X, q.Z - p.Z));   // ins Straßeninnere
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
