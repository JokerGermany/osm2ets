// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;

namespace Osm2Ets.Writer;

/// <summary>
/// Wählt an jedem OSM-Knoten das Paar von Straßenenden, das am geradesten
/// durchläuft, und verbindet es. ETS2 erlaubt je Knoten nur zwei verbundene
/// Straßenstücke (Rückwärts- und Vorwärtsseite); Abzweige brauchen Prefabs.
/// Zweirichtungsstraßen werden bei Bedarf umgedreht, damit Ende auf Anfang trifft.
/// </summary>
public static class Connector
{
    /// <summary>Größte Abweichung vom Geradeauslauf, die noch verbunden wird.</summary>
    public const double MaxTurnDeg = 50.0;

    private sealed record End(RoadEntry Road, bool AtStart, Vector2 Away);

    public static (List<(RoadEntry From, RoadEntry To)> Pairs, int Reversed) Plan(List<RoadEntry> roads, IEnumerable<RoadEntry>? fixedRoads = null)
    {
        var fixedOrientation = new HashSet<RoadEntry>(fixedRoads ?? []);
        var reversedSet = new HashSet<RoadEntry>();
        var used = new HashSet<(RoadEntry, bool)>();
        var pairs = new List<(RoadEntry, RoadEntry)>();

        var byNode = new Dictionary<long, List<(RoadEntry Road, bool AtStart)>>();
        foreach (var r in roads)
        {
            if (r.Points.Count < 2) continue;
            foreach (bool atStart in new[] { true, false })
            {
                var info = atStart ? r.Start : r.End;
                if (info?.Kind != "node" || info.OsmNode is null) continue;
                if (!byNode.TryGetValue(info.OsmNode.Value, out var list)) byNode[info.OsmNode.Value] = list = [];
                list.Add((r, atStart));
            }
        }

        // Kandidaten über alle Knoten, beste (geradeste, gleiche Rolle) zuerst
        var candidates = new List<(double Score, (RoadEntry, bool) A, (RoadEntry, bool) B)>();
        foreach (var ends in byNode.Values)
        {
            for (int i = 0; i < ends.Count; i++)
            for (int j = i + 1; j < ends.Count; j++)
            {
                var a = ends[i]; var b = ends[j];
                if (a.Road == b.Road) continue; // geschlossener Ring: nicht mit sich selbst
                var va = Away(a.Road, a.AtStart); var vb = Away(b.Road, b.AtStart);
                double cos = Vector2.Dot(va, vb); // -1 = genau gegenüber = geradeaus
                double turn = 180.0 - Math.Acos(Math.Clamp(cos, -1, 1)) * 180.0 / Math.PI;
                if (turn > MaxTurnDeg) continue;
                double score = turn + (a.Road.Role == b.Road.Role ? 0 : 30) + (a.Road.Role == "core" ? 0 : 5);
                candidates.Add((score, a, b));
            }
        }
        candidates.Sort((x, y) => x.Score.CompareTo(y.Score));

        foreach (var (_, a, b) in candidates)
        {
            if (used.Contains(a) || used.Contains(b)) continue;
            // Gewünscht: Ende (AtStart=false) von "from" trifft Anfang (AtStart=true) von "to".
            // Effektive Lage nach eventuellem Umdrehen:
            bool aStart = a.Item2 ^ reversedSet.Contains(a.Item1);
            bool bStart = b.Item2 ^ reversedSet.Contains(b.Item1);
            if (aStart == bStart)
            {
                // eins von beiden umdrehen, wenn erlaubt
                var flip = CanFlip(b.Item1, fixedOrientation) ? b.Item1 : CanFlip(a.Item1, fixedOrientation) ? a.Item1 : null;
                if (flip is null) continue;
                if (!reversedSet.Add(flip)) reversedSet.Remove(flip);
                aStart = a.Item2 ^ reversedSet.Contains(a.Item1);
                bStart = b.Item2 ^ reversedSet.Contains(b.Item1);
            }
            fixedOrientation.Add(a.Item1);
            fixedOrientation.Add(b.Item1);
            used.Add(a); used.Add(b);
            pairs.Add(aStart ? (b.Item1, a.Item1) : (a.Item1, b.Item1));
        }

        foreach (var r in reversedSet) Reverse(r);
        return (pairs, reversedSet.Count);
    }

    private static bool CanFlip(RoadEntry r, HashSet<RoadEntry> fixedSet) =>
        !fixedSet.Contains(r) && r.Runs.All(x => !x.Oneway);

    /// <summary>Richtung vom Knoten weg in die Straße hinein (x/z).</summary>
    private static Vector2 Away(RoadEntry r, bool atStart)
    {
        var p = r.Points;
        double[] a = atStart ? p[0] : p[^1];
        double[] b = atStart ? p[1] : p[^2];
        var v = new Vector2((float)(b[0] - a[0]), (float)(b[2] - a[2]));
        return v.LengthSquared() > 0 ? Vector2.Normalize(v) : v;
    }

    /// <summary>Dreht eine Straße um (Punkte, Läufe, Endinformationen).</summary>
    public static void Reverse(RoadEntry r)
    {
        int segments = r.Points.Count - 1;
        r.Points.Reverse();
        r.Runs = r.Runs.Select(x => { var (f, t) = (segments - x.To, segments - x.From); x.From = f; x.To = t; return x; })
                       .OrderBy(x => x.From).ToList();
        (r.Start, r.End) = (r.End, r.Start);
    }
}
