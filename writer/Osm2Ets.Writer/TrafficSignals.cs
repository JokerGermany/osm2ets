// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Ampeln: Kreuzungs-Prefabs, an denen OSM eine Ampel hat (Knoten highway=traffic_signals am
/// Kreuzungsknoten oder an der Haltlinie davor), bekommen ein Ampelprofil. Nur Prefabs, deren .ppd
/// Ampelpositionen hat (z. B. ger_r1_x_r1_(t_)narrow_tmpl); für andere eine Meldung.
/// Aus: OSM2ETS_NO_SIGNALS=1, Profil: OSM2ETS_SIGNAL_PROFILE (Standard cr_1x1).
/// </summary>
internal static class TrafficSignals
{
    public static bool Enabled = Environment.GetEnvironmentVariable("OSM2ETS_NO_SIGNALS") != "1";
    public static string Profile = Environment.GetEnvironmentVariable("OSM2ETS_SIGNAL_PROFILE") ?? "cr_1x1";
    /// <summary>Abweichende Profile je Prefab: Abbiegespur × T (ger_r1_turn_x_r1_t_tmpl) → „crossroad 1 turn x1 t“.</summary>
    private static readonly Dictionary<string, string> ByModel = new() { ["ch_14001"] = "cr_1tx1" };
    /// <summary>So weit (m) außerhalb der Knoten eines Prefabs zählt eine Ampel noch zu ihm (Haltlinie).</summary>
    public const float Reach = 12f;

    public static (int Set, List<string> Missing) Apply(Map map, List<AreaEntry> signals)
    {
        var missing = new List<string>();
        if (!Enabled || signals.Count == 0) return (0, missing);
        var prefabs = map.MapItems.Values.OfType<Prefab>()
            .Where(p => p.Nodes.Count >= 3)
            .Select(p =>
            {
                var c = p.Nodes.Aggregate(Vector2.Zero, (a, n) => a + new Vector2(n.Position.X, n.Position.Z)) / p.Nodes.Count;
                float r = p.Nodes.Max(n => Vector2.Distance(c, new Vector2(n.Position.X, n.Position.Z)));
                return (P: p, C: c, R: r);
            }).ToList();
        var hit = new HashSet<Prefab>();
        foreach (var s in signals)
        {
            if (s.Points.Count == 0) continue;
            var q = new Vector2((float)s.Points[0][0], (float)s.Points[0][1]);
            (Prefab P, float D)? best = null;
            foreach (var (p, c, r) in prefabs)
            {
                float d = Vector2.Distance(q, c);
                if (d <= r + Reach && (best is null || d < best.Value.D)) best = (p, d);
            }
            if (best is { } b) hit.Add(b.P);
        }
        int set = 0;
        foreach (var p in hit)
        {
            var ppd = PrefabFootprint.Descriptor(p);
            if (ppd is null || ppd.Semaphores.Count == 0)
            {
                var c = p.Nodes[0].Position;
                missing.Add($"{p.Model} bei x {c.X:0} z {c.Z:0}");
                continue;
            }
            p.SemaphoreProfile = Environment.GetEnvironmentVariable("OSM2ETS_SIGNAL_PROFILE") is null && ByModel.TryGetValue(p.Model.ToString(), out var pm) ? pm : Profile;
            set++;
        }
        return (set, missing);
    }
}
