// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using TruckLib;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Ebenerdige Kreuzungen (T und X) normaler Zweirichtungsstraßen mit SCS-Prefabs.
/// Prefabs und Knotengeometrie aus den .ppd-Dateien von ETS2 1.61 (cross_temp/ger):
///   T  r1: ger_r1_x_r1_t_narrow_tmpl            Knoten 0 = Seitenarm, 1/2 = durchgehend
///   X  r1: ger_r1_x_r1_narrow_tmpl              Knoten 0..3 im Abstand von 90°
///   T  r2: ger_r2_no_offset_x_r1_narrow_t_tmpl  Knoten 0 = Seitenarm (r1), 1/2 = r2
///   X  r2: ger_r2_no_offset_x_r1_narrow_tmpl    Knoten 1/3 = r2, 0/2 = r1
/// Variantennamen aus den .pmd-Dateien (r1: "clean", r2: "default"), Look "ger_1".
/// </summary>
public static class Junctions
{
    public sealed record Def(Interchanges.PrefabDef Prefab, ushort[] Through, ushort[] Side);

    public static readonly Def TR1 = new(new("mod_ger_67", "/prefab2/cross_temp/ger/ger_r1_x_r1_t_narrow_tmpl.ppd", "clean", "ger_1"), [1, 2], [0]);
    public static readonly Def XR1 = new(new("mod_ger_63", "/prefab2/cross_temp/ger/ger_r1_x_r1_narrow_tmpl.ppd", "clean", "ger_1"), [1, 3], [0, 2]);
    public static readonly Def TR2 = new(new("2i03c", "/prefab2/cross_temp/ger/ger_r2_no_offset_x_r1_narrow_t_tmpl.ppd", "default", "ger_1"), [1, 2], [0]);
    public static readonly Def XR2 = new(new("ger_2i03f", "/prefab2/cross_temp/ger/ger_r2_no_offset_x_r1_narrow_tmpl.ppd", "default", "ger_1"), [1, 3], [0, 2]);

    /// <summary>Größte erlaubte Abweichung eines Arms von der Prefab-Richtung (Grad).</summary>
    public const float MaxDeviation = 40f;

    /// <summary>Gerader Einlauf in den Prefab-Knoten (m); kürzer als an Anschlussstellen.</summary>
    public const float Lead = 8f;

    public sealed record Arm(RoadEntry Road, Vector2 Away);

    /// <summary>Straßen, deren Richtung wegen Knoten 0 eines Prefabs festliegt (nicht mehr umdrehen).</summary>
    public static readonly HashSet<RoadEntry> Fixed = [];

    /// <summary>Liegt der Anfang (statt des Endes) der Straße an <paramref name="pos"/>?</summary>
    public static bool StartsAt(RoadEntry r, Vector3 pos) =>
        Vector2.Distance(P(r.Points[0]), new Vector2(pos.X, pos.Z)) < Vector2.Distance(P(r.Points[^1]), new Vector2(pos.X, pos.Z));

    public sealed class Plan
    {
        public required long OsmNode;
        public required Vector3 Anchor;
        public required Def Def;
        public required Arm[] Arms;           // Arms[i] gehört zu Prefab-Knoten NodeOf[i]
        public ushort[] NodeOf = [];
        public Quaternion Rotation;
        public Vector3 Position;               // Lage von Knoten 0 (für Prefab.Add)
        public Vector3[] NodePos = [];
        public Vector3[] Inward = [];
        public Prefab? Placed;
    }

    public static List<Plan> Find(List<RoadEntry> roads, Func<RoadEntry, bool, string> roadTypeAt, List<string> warnings)
    {
        var ends = new Dictionary<long, List<(RoadEntry R, bool AtStart)>>();
        foreach (var r in roads)
            foreach (var atStart in new[] { true, false })
            {
                var info = atStart ? r.Start : r.End;
                if (info?.Kind == "node" && info.OsmNode is long id)
                {
                    if (!ends.TryGetValue(id, out var l)) ends[id] = l = [];
                    l.Add((r, atStart));
                }
            }

        var plans = new List<Plan>();
        foreach (var (node, list) in ends)
        {
            if (list.Count is not (3 or 4)) continue;
            var runs = list.Select(e => e.AtStart ? e.R.Runs[0] : e.R.Runs[^1]).ToList();
            if (runs.Any(x => x.Highway is "motorway" or "trunk" && x.Oneway)) continue; // Autobahnknoten
            if (list.Any(e => e.R.Closed)) continue;
            int oneway = runs.Count(x => x.Oneway);
            if (oneway > list.Count - 2) continue;  // Rampengabelung o. Ä.

            var arms = list.Select(e => new Arm(e.R, Away(e.R, e.AtStart))).ToArray();
            if (arms.Any(a => a.Away.LengthSquared() < 0.5f)) continue;
            // durchgehendes Paar: zweispurig in beide Richtungen, möglichst gegenüberliegend
            (int A, int B) best = (-1, -1);
            float bestDot = 1;
            for (int i = 0; i < arms.Length; i++)
                for (int j = i + 1; j < arms.Length; j++)
                {
                    if (runs[i].Oneway || runs[j].Oneway) continue;
                    float d = Vector2.Dot(arms[i].Away, arms[j].Away);
                    if (d < bestDot) { bestDot = d; best = (i, j); }
                }
            if (best.A < 0 || bestDot > -MathF.Cos(MaxDeviation * MathF.PI / 180))
            {
                warnings.Add($"Knoten {node}: Kreuzung ohne gegenüberliegende Arme – kein Prefab");
                continue;
            }
            bool r2 = roadTypeAt(list[best.A].R, list[best.A].AtStart) == "ger29"
                   && roadTypeAt(list[best.B].R, list[best.B].AtStart) == "ger29";
            var def = list.Count == 3 ? (r2 ? TR2 : TR1) : (r2 ? XR2 : XR1);
            var anchor = MapWriter.ToVector(list[0].AtStart ? list[0].R.Points[0] : list[0].R.Points[^1]);
            // Arme so ordnen: durchgehend A, durchgehend B, dann die übrigen
            var order = new List<int> { best.A, best.B };
            order.AddRange(Enumerable.Range(0, arms.Length).Where(i => i != best.A && i != best.B));
            plans.Add(new Plan { OsmNode = node, Anchor = anchor, Def = def, Arms = order.Select(i => arms[i]).ToArray() });
        }
        return plans;
    }

    private static Vector2 Away(RoadEntry r, bool atStart)
    {
        var p = r.Points;
        Vector2 a = atStart ? P(p[0]) : P(p[^1]);
        int i = atStart ? 1 : p.Count - 2;
        Vector2 b = a;
        while (i >= 0 && i < p.Count)
        {
            b = P(p[i]);
            if (Vector2.Distance(a, b) > 40) break;
            i += atStart ? 1 : -1;
        }
        var v = b - a;
        return v.LengthSquared() > 1e-6f ? Vector2.Normalize(v) : Vector2.Zero;
    }

    private static Vector2 P(double[] p) => new((float)p[0], (float)p[2]);

    private static float Ang(Vector2 v) => MathF.Atan2(v.Y, v.X);

    private static float Wrap(float a)
    {
        while (a > MathF.PI) a -= 2 * MathF.PI;
        while (a < -MathF.PI) a += 2 * MathF.PI;
        return a;
    }

    /// <summary>Wählt Drehung und Knotenzuordnung, prüft Armlängen und kürzt die Straßen.</summary>
    public static bool Prepare(Plan p, Interchanges.PpdSource src, out string? reason)
    {
        reason = null;
        var ppd = src.Get(p.Def.Prefab.PpdPath);
        var unit = new Token(p.Def.Prefab.Unit);
        var local = Interchanges.CalibrateNodes(ppd, unit, Quaternion.Identity);
        var localIn = Interchanges.WorldDirectionsOf(ppd, local);
        Vector2 Out(int k) => -new Vector2(localIn[k].X, localIn[k].Z);

        // Kandidaten: durchgehende Arme auf Through[0]/[1] (beide Richtungen),
        // Seitenarme auf die Side-Knoten (alle Zuordnungen)
        var sideArms = p.Arms.Skip(2).ToArray();
        var sidePerms = Permutations(p.Def.Side.Length).ToList();
        float bestErr = float.MaxValue;
        ushort[]? bestNodes = null; float bestRot = 0; bool blocked0 = false;
        foreach (var flip in new[] { false, true })
            foreach (var perm in sidePerms)
            {
                if (sideArms.Length > p.Def.Side.Length) continue;
                var nodes = new List<ushort> { p.Def.Through[flip ? 1 : 0], p.Def.Through[flip ? 0 : 1] };
                nodes.AddRange(perm.Take(sideArms.Length).Select(k => p.Def.Side[k]));
                // mittlere Drehung (Kreismittel der Winkeldifferenzen)
                float sx = 0, sy = 0;
                for (int i = 0; i < nodes.Count; i++)
                {
                    float d = Ang(p.Arms[i].Away) - Ang(Out(nodes[i]));
                    sx += MathF.Cos(d); sy += MathF.Sin(d);
                }
                float rot = MathF.Atan2(sy, sx);
                float err = 0, worst = 0;
                for (int i = 0; i < nodes.Count; i++)
                {
                    float e = MathF.Abs(Wrap(Ang(p.Arms[i].Away) - Ang(Out(nodes[i])) - rot));
                    err += e * e; worst = MathF.Max(worst, e);
                }
                if (worst * 180 / MathF.PI > MaxDeviation) continue;
                // TruckLib: an Knoten 0 (Ursprung) muss eine Straße mit ihrem Ende anschließen
                int at0 = nodes.IndexOf(0);
                if (at0 >= 0)
                {
                    var r0 = p.Arms[at0].Road;
                    if (StartsAt(r0, p.Anchor) && (Fixed.Contains(r0) || r0.Runs.Any(x => x.Oneway))) { blocked0 = true; continue; }
                }
                if (err < bestErr) { bestErr = err; bestNodes = nodes.ToArray(); bestRot = rot; }
            }
        if (bestNodes is null) { reason = blocked0 ? "Einbahn-Arm beginnt an Prefab-Knoten 0 (von TruckLib nicht anschließbar)" : "Winkel der Arme passen nicht zum Prefab"; return false; }
        p.NodeOf = bestNodes;

        // Drehung als Quaternion: lokalen Ausgang von Knoten 0 auf die Zielrichtung drehen
        var v0 = Out(p.NodeOf[0]);
        var target = new Vector2(MathF.Cos(Ang(v0) + bestRot), MathF.Sin(Ang(v0) + bestRot));
        p.Rotation = Interchanges.YawTo(ppd, unit, new Vector3(v0.X, 0, v0.Y), new Vector3(target.X, 0, target.Y));
        var placed = Interchanges.CalibrateNodes(ppd, unit, p.Rotation);
        // Kreuzungsmitte = Mitte der beiden durchgehenden Knoten
        var center = (placed[p.Def.Through[0]] + placed[p.Def.Through[1]]) / 2;
        var shift = p.Anchor - center;
        p.NodePos = placed.Select(v => v + shift).ToArray();
        p.Position = p.NodePos[0];
        p.Inward = Interchanges.WorldDirectionsOf(ppd, placed);
        if (Environment.GetEnvironmentVariable("OSM2ETS_DEBUG") is not null)
            for (int i = 0; i < p.Arms.Length; i++)
            {
                var k = p.NodeOf[i];
                var toC = p.Anchor - p.NodePos[k]; toC.Y = 0;
                var arm = new Vector3(p.Arms[i].Away.X, 0, p.Arms[i].Away.Y);
                Console.Error.WriteLine($"DBG {p.OsmNode} node{k} inward·toCenter={Vector3.Dot(Vector3.Normalize(toC), p.Inward[k]):0.000} " +
                                        $"arm·outward={Vector3.Dot(arm, -p.Inward[k]):0.000} r={toC.Length():0.0}");
            }

        // erst prüfen, dann kürzen
        for (int i = 0; i < p.Arms.Length; i++)
        {
            float r = Vector2.Distance(P(V(p.NodePos[p.NodeOf[i]])), P(V(p.Anchor)));
            if (Interchanges.ArcLengthOf(p.Arms[i].Road) < r + Lead + 8)
            { reason = $"{p.Arms[i].Road.Id} zu kurz"; return false; }
        }
        // eine Straße kann mit beiden Enden an derselben Kreuzung hängen – dann nicht
        if (p.Arms.Select(a => a.Road).Distinct().Count() != p.Arms.Length)
        { reason = "Straße mit beiden Enden an der Kreuzung"; return false; }
        for (int i = 0; i < p.Arms.Length; i++)
        {
            var a = p.Arms[i];
            var n = p.NodePos[p.NodeOf[i]];
            var inward = p.Inward[p.NodeOf[i]];
            if (p.NodeOf[i] == 0)
            {
                if (StartsAt(a.Road, p.Anchor)) Connector.Reverse(a.Road);
                Fixed.Add(a.Road);
            }
            bool atStart = StartsAt(a.Road, p.Anchor);
            if (atStart) Interchanges.SetHead(a.Road, n, -inward, Lead);
            else Interchanges.SetTail(a.Road, n, inward, Lead);
            if (atStart) a.Road.Start = new EndInfo { Kind = "prefab" };
            else a.Road.End = new EndInfo { Kind = "prefab" };
        }
        return true;
    }

    private static double[] V(Vector3 v) => [v.X, v.Y, v.Z];

    private static IEnumerable<int[]> Permutations(int n)
    {
        if (n == 1) { yield return [0]; yield break; }
        yield return [0, 1];
        yield return [1, 0];
    }

    public static Prefab Build(Map map, Plan p, Interchanges.PpdSource src)
    {
        var ppd = src.Get(p.Def.Prefab.PpdPath);
        var pf = Prefab.Add(map, p.Position, p.Def.Prefab.Unit, ppd, p.Rotation);
        pf.Variant = p.Def.Prefab.Variant;
        pf.Look = p.Def.Prefab.Look;
        pf.ViewDistance = 1400;
        for (int i = 0; i < p.Arms.Length; i++)
        {
            var k = p.NodeOf[i];
            var inward = p.Inward[k];
            pf.Nodes[k].Rotation = MathEx.GetNodeRotation(StartsAt(p.Arms[i].Road, pf.Nodes[k].Position) ? -inward : inward);
        }
        p.Placed = pf;
        return pf;
    }
}
