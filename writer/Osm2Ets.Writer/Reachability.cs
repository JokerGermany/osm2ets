using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Geheimstraßen (erst nach dem Befahren auf der Karte), die der Spieler nicht erreichen kann,
/// erscheinen gar nicht auf der Karte: man kann sie nie befahren, also nie aufdecken. Erreichbar =
/// über Knoten und Prefabs verbunden mit einem Autobahn-/Kraftstraßenstück. Straßen hinter einer
/// Sperrbake (gesperrte Zufahrt, Ende ins Nichts) hängen nicht an diesem Netz und fallen damit heraus.
/// Ohne Autobahn in der Karte: Teil mit den meisten sichtbaren, nicht geheimen Straßen.
/// </summary>
public static class Reachability
{
    public static int Apply(Map map, IEnumerable<IMapItem>? seeds = null)
    {
        var items = map.MapItems.Values.Where(i => i is Road or Prefab).ToList();
        var parent = new Dictionary<ulong, ulong>();
        ulong Find(ulong x) { while (parent.TryGetValue(x, out var p) && p != x) { parent[x] = parent.GetValueOrDefault(p, p); x = p; } return x; }
        void Union(ulong a, ulong b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
        IEnumerable<INode> NodesOf(IMapItem it) => it switch
        {
            Road r => new[] { r.Node, r.ForwardNode },
            Prefab p => p.Nodes,
            _ => Array.Empty<INode>(),
        };
        foreach (var it in items)
        {
            parent.TryAdd(it.Uid, it.Uid);
            foreach (var n in NodesOf(it))
            {
                parent.TryAdd(n.Uid, n.Uid);
                Union(it.Uid, n.Uid);
            }
        }
        var mains = new HashSet<ulong>();
        if (seeds != null) foreach (var s0 in seeds) if (parent.ContainsKey(s0.Uid)) mains.Add(Find(s0.Uid));
        if (mains.Count == 0)
        {
            var score = new Dictionary<ulong, int>();
            foreach (var r in items.OfType<Road>().Where(r => !r.Secret && r.ShowInUiMap))
            { var k = Find(r.Uid); score[k] = score.GetValueOrDefault(k) + 1; }
            if (score.Count == 0) return 0;
            mains.Add(score.MaxBy(kv => kv.Value).Key);
        }
        int n2 = 0;
        foreach (var r in items.OfType<Road>().Where(r => r.Secret && !mains.Contains(Find(r.Uid))))
        { r.Secret = false; r.ShowInUiMap = false; n2++; }
        return n2;
    }
}
