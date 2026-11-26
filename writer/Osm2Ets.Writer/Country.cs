// SPDX-License-Identifier: GPL-2.0-or-later
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Land der Karte: Das Spiel bestimmt das Land eines Ortes über Grenzknoten (Knoten-Flag
/// „Country border“ mit der Länder-ID je Seite). Ohne Land fehlen länderabhängige Modelle – z. B.
/// das Ampelmodell city_str (nur als city_str.germany usw. vorhanden), die Ampeln blieben aus.
/// Je zusammenhängendem Netz aus Straßen und Prefabs wird ein Knoten zur Grenze mit dem Land auf
/// beiden Seiten. Länder-ID aus dem Profil des Zwischenformats (de → 5, aus def/country/germany.sui
/// des Spiels: country_id 5); OSM2ETS_COUNTRY_ID überschreibt (0 = kein Land).
/// </summary>
internal static class Country
{
    /// <summary>Profil → country_id (def/country/&lt;land&gt;.sui).</summary>
    private static readonly Dictionary<string, byte> ByProfile = new(StringComparer.OrdinalIgnoreCase) { ["de"] = 5 };
    public static byte? Id;

    public static int Apply(Map map, string? profile)
    {
        var env = Environment.GetEnvironmentVariable("OSM2ETS_COUNTRY_ID");
        Id = env is not null ? (byte.TryParse(env, out var v) && v > 0 ? v : null)
           : profile is not null && ByProfile.TryGetValue(profile, out var p) ? p : null;
        if (Id is not byte id) return 0;
        var seen = new HashSet<IMapItem>();
        int n = 0;
        foreach (var start in map.MapItems.Values.Where(i => i is Road or Prefab))
        {
            if (seen.Contains(start)) continue;
            INode? border = null;
            var st = new Stack<IMapItem>(); st.Push(start);
            while (st.Count > 0)
            {
                var it = st.Pop();
                if (!seen.Add(it)) continue;
                IEnumerable<INode> nodes = it switch { Road rd => [rd.Node, rd.ForwardNode], Prefab pf => pf.Nodes, _ => [] };
                foreach (var nd in nodes)
                {
                    if (border is null && it is Road && (nd.ForwardItem is null || nd.BackwardItem is null)) border = nd;
                    foreach (var o in new[] { nd.ForwardItem, nd.BackwardItem })
                        if (o is IMapItem oi && (oi is Road or Prefab) && !seen.Contains(oi)) st.Push(oi);
                }
            }
            border ??= start switch { Road rd => rd.Node, Prefab pf => pf.Nodes[0], _ => null };
            if (border is null) continue;
            border.IsCountryBorder = true;
            border.ForwardCountry = id; border.BackwardCountry = id;
            n++;
        }
        return n;
    }
}
