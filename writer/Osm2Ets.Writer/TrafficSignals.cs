// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Ampeln: Kreuzungs-Prefabs, an denen OSM eine Ampel hat (Knoten highway=traffic_signals am
/// Kreuzungsknoten oder an der Haltlinie davor), bekommen ein Ampelprofil. Nur Prefabs, deren .ppd
/// Ampelpositionen hat (z. B. ger_r1_x_r1_(t_)narrow_tmpl); für andere eine Meldung.
/// Profil aus den Ampelplätzen der .ppd, dazu das Prefab-Flag „Ampeln“ (<see cref="ItemFlags.PrefabSemaphores"/>,
/// ohne das das Spiel trotz Profil keine Ampeln aufstellt). Aus: OSM2ETS_NO_SIGNALS=1, Profil erzwingen:
/// OSM2ETS_SIGNAL_PROFILE (leer: kein Profil, wie es der Editor speichert), ohne Flag: OSM2ETS_SIGNAL_NO_FLAG=1.
/// </summary>
internal static class TrafficSignals
{
    public static bool Enabled = Environment.GetEnvironmentVariable("OSM2ETS_NO_SIGNALS") != "1";
    /// <summary>Prefab-Flag „Ampeln“ setzen (aus: OSM2ETS_SIGNAL_NO_FLAG=1, nur zum Vergleich).</summary>
    public static bool SetFlag = Environment.GetEnvironmentVariable("OSM2ETS_SIGNAL_NO_FLAG") != "1";
    /// <summary>Ersatzprofil, wenn die Ampelplätze der .ppd keins nennen.</summary>
    public static string Profile = Environment.GetEnvironmentVariable("OSM2ETS_SIGNAL_PROFILE") ?? "cr_1x1";
    /// <summary>Prefabs mit dem Zusatzteil _semaphores (im Editor gesehen; die Teilnamen stehen in der .pmg).</summary>
    private static readonly HashSet<string> WithSemaphorePart = ["ch_14001"];

    /// <summary>
    /// Profil aus den Ampelplätzen der .ppd (jeder Platz nennt das Profil, für das er gebaut ist;
    /// ger_r1_turn_x_r1_t_tmpl: cr_t1x1t – das einzige, das der Editor dort anbietet). Das häufigste gilt.
    /// </summary>
    public static string? FromPpd(TruckLib.Models.Ppd.PrefabDescriptor ppd) => ppd.Semaphores
        .Select(s => s.Profile.ToString()).Where(t => !string.IsNullOrEmpty(t))
        .GroupBy(t => t).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
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
            p.SemaphoreProfile = Environment.GetEnvironmentVariable("OSM2ETS_SIGNAL_PROFILE") is null && FromPpd(ppd) is { } pm ? pm : Profile;
            // Ohne dieses Flag stellt das Spiel trotz Profil keine Ampeln auf. Der Editor setzt es, wenn man dort
            // ein Profil wählt (gefunden im Vergleich mit einer im Editor gespeicherten Karte; TruckLib ab
            // 0.5.3 beschreibt es als Prefab.Semaphores).
            if (SetFlag) ItemFlags.Set(p, ItemFlags.PrefabSemaphores, true);
            // wie in der Europa-Karte: Zusatzteil _semaphores dazu (nur wo das Modell es nachweislich hat;
            // die Teilnamen stehen in der .pmg, die hier nicht vorliegt)
            if (WithSemaphorePart.Contains(p.Model.ToString()) && !p.AdditionalParts.Any(t => t.ToString() == "_semaphores"))
                p.AdditionalParts.Add("_semaphores");
            set++;
        }
        return (set, missing);
    }
}
