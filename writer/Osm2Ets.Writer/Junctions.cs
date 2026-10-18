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

    // Gabelungen einspuriger Einbahnstraßen (Rampen): Knoten 0 = Stamm, 1/2 = Äste (±30°).
    // Variante/Look ungeprüft (keine .pmd vorhanden) – "default"/"ger_1" wie bei den Anschlussstellen.
    public static readonly Def FSPLIT = new(new("ch_4202n", "/prefab2/fork_temp/ger/ger_1lane_road_joint_short_tmpl_v02.ppd", "default", "ger_1"), [1, 2], [0]);
    public static readonly Def FJOIN = new(new("ch_4202o", "/prefab2/fork_temp/ger/ger_1lane_road_joint_short_mirrored_tmpl_v02.ppd", "default", "ger_1"), [1, 2], [0]);

    // Zweirichtungsstraße teilt sich in zwei Einbahnstraßen (z. B. Rampe, die zur
    // getrennten Fahrbahn wird): Knoten 0 = beide Richtungen, 1 = Einbahn hinein, 2 = Einbahn heraus (±15°).
    public static readonly Def SPLIT2 = new(new("ch_42029", "/prefab2/fork_temp/ger/ger_r1_split_short.ppd", "default", "ger_1"), [1, 2], [0]);

    /// <summary>Größte erlaubte Abweichung eines Arms von der Prefab-Richtung (Grad).</summary>
    public const float MaxDeviation = 40f;

    /// <summary>
    /// Arme, die bis zu so viel (Grad) von der Prefab-Richtung abweichen, werden auf einem
    /// Bogen in die Prefab-Richtung umgelegt (spitzwinklige OSM-Einmündungen, Y-Formen).
    /// Sonst liegen die Fahrbahnen auf den ersten Metern übereinander.
    /// </summary>
    public const float MaxBend = 75f;

    /// <summary>Länge des Bogens je nach Abweichung (m); 0 = gerader Einlauf genügt.</summary>
    public static float BendLength(float deg) => deg < 20 ? 0 : MathF.Min(25 + 1.2f * deg, 110);

    /// <summary>Gerader Einlauf in den Prefab-Knoten (m); kürzer als an Anschlussstellen.</summary>
    public const float Lead = 8f;

    /// <summary>So viel Straße muss hinter dem Prefab-Knoten mindestens übrig bleiben (m).</summary>
    public const float MinTail = 6f;

    public sealed record Arm(RoadEntry Road, Vector2 Away);

    /// <summary>Straßen, deren Richtung wegen Knoten 0 eines Prefabs festliegt (nicht mehr umdrehen).</summary>
    public static readonly HashSet<RoadEntry> Fixed = [];

    /// <summary>Straßenende → bereits vorbereitete Kreuzung und Arm (für direkt verbundene Prefabs).</summary>
    public static readonly Dictionary<RoadEntry, (Plan Plan, int Arm)> PlacedEnds = [];

    /// <summary>Zu kurze Verbindungsstraßen zwischen zwei Kreuzungs-Prefabs; die Prefabs werden direkt verbunden.</summary>
    public static readonly HashSet<RoadEntry> Dropped = [];

    /// <summary>OSM-Knoten mit noch nicht gesetzter Kreuzung (dürfen kurze Arme „übernehmen“).</summary>
    public static readonly HashSet<long> PendingNodes = [];

    public static void Reset() { Fixed.Clear(); PlacedEnds.Clear(); Dropped.Clear(); PendingNodes.Clear(); }

    private static bool OtherEndPending(RoadEntry r, Vector3 anchor)
    {
        var other = StartsAt(r, anchor) ? r.End : r.Start;
        return other?.Kind == "node" && other.OsmNode is long id && PendingNodes.Contains(id);
    }

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
        public float[] Bend = [];
        public ushort Origin;
        public int SnapArm = -1;                // Arm, der direkt an ein anderes Prefab anschließt
        public (Plan Plan, int Arm) SnapTo;                  // ppd-Knoten, der Ursprung des Prefabs wird              // Bogenlänge je Arm (m)
        public ushort[]? ForceNodes;           // feste Zuordnung Arm → Knoten (Einbahn-Rollen)
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
            if (list.Count == 3 && oneway == 2)
            {
                int two = runs.FindIndex(x => !x.Oneway);
                int inc = Enumerable.Range(0, 3).FirstOrDefault(i => i != two && !list[i].AtStart, -1);   // endet hier = fährt hinein
                int outg = Enumerable.Range(0, 3).FirstOrDefault(i => i != two && list[i].AtStart, -1);
                bool twoWayNarrow = runs[two].Lanes is null or <= 2;
                if (inc >= 0 && outg >= 0 && twoWayNarrow
                    && runs[inc].Lanes is null or 1 && runs[outg].Lanes is null or 1)
                {
                    var fa = list.Select(e => new Arm(e.R, Away(e.R, e.AtStart))).ToArray();
                    if (fa.All(a => a.Away.LengthSquared() > 0.5f))
                    {
                        var anc = MapWriter.ToVector(list[0].AtStart ? list[0].R.Points[0] : list[0].R.Points[^1]);
                        plans.Add(new Plan { OsmNode = node, Anchor = anc, Def = SPLIT2,
                                             Arms = [fa[inc], fa[outg], fa[two]], ForceNodes = [1, 2, 0] });
                        continue;
                    }
                }
            }
            if (list.Count == 3 && oneway == 3 && runs.All(x => x.Lanes is null or 1))
            {
                // Gabelung: ein Stamm, zwei Äste
                int incoming = list.Count(e => !e.AtStart);
                if (incoming is 1 or 2)
                {
                    bool split = incoming == 1;
                    int stem = list.FindIndex(e => split ? !e.AtStart : e.AtStart);
                    var fa = list.Select(e => new Arm(e.R, Away(e.R, e.AtStart))).ToArray();
                    if (fa.Any(a => a.Away.LengthSquared() < 0.5f)) continue;
                    var ord = Enumerable.Range(0, 3).Where(i => i != stem).Append(stem).ToList();
                    var anc = MapWriter.ToVector(list[0].AtStart ? list[0].R.Points[0] : list[0].R.Points[^1]);
                    plans.Add(new Plan { OsmNode = node, Anchor = anc, Def = split ? FSPLIT : FJOIN,
                                         Arms = ord.Select(i => fa[i]).ToArray() });
                }
                continue;
            }
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
            if (best.A < 0 || bestDot > -MathF.Cos(60 * MathF.PI / 180))
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
        ushort[]? bestNodes = null; float bestRot = 0;
        foreach (var flip in new[] { false, true })
            foreach (var perm in sidePerms)
            {
                if (sideArms.Length > p.Def.Side.Length) continue;
                var nodes = new List<ushort> { p.Def.Through[flip ? 1 : 0], p.Def.Through[flip ? 0 : 1] };
                nodes.AddRange(perm.Take(sideArms.Length).Select(k => p.Def.Side[k]));
                if (p.ForceNodes is not null)
                {
                    if (flip || perm[0] != 0) continue;
                    nodes = p.ForceNodes.ToList();
                }
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
                if (worst * 180 / MathF.PI > MaxBend) continue;
                if (err < bestErr) { bestErr = err; bestNodes = nodes.ToArray(); bestRot = rot; }
            }
        if (bestNodes is null) { reason = "Winkel der Arme passen nicht zum Prefab"; return false; }
        p.NodeOf = bestNodes;
        // TruckLib: am Ursprungsknoten muss eine Straße mit ihrem ENDE anschließen. Ursprung
        // daher auf einen Arm legen, der am Prefab endet (oder sich umdrehen lässt).
        int originArm = Enumerable.Range(0, p.Arms.Length).FirstOrDefault(i => !StartsAt(p.Arms[i].Road, p.Anchor), -1);
        if (originArm < 0)
            originArm = Enumerable.Range(0, p.Arms.Length).FirstOrDefault(i =>
                !Fixed.Contains(p.Arms[i].Road) && p.Arms[i].Road.Runs.All(x => !x.Oneway), -1);
        if (originArm < 0) { reason = "alle Arme beginnen am Prefab und lassen sich nicht umdrehen"; return false; }
        p.Origin = p.NodeOf[originArm];
        p.Bend = new float[p.Arms.Length];
        for (int i = 0; i < p.Arms.Length; i++)
        {
            float e = MathF.Abs(Wrap(Ang(p.Arms[i].Away) - Ang(Out(p.NodeOf[i])) - bestRot)) * 180 / MathF.PI;
            p.Bend[i] = BendLength(e);
        }

        // Kurze Verbindung zu einer schon gesetzten Kreuzung: Prefabs direkt aneinandersetzen
        var localPos = Interchanges.CalibrateNodes(ppd, unit, Quaternion.Identity);
        var localCenter = p.Def == FSPLIT || p.Def == FJOIN || p.Def == SPLIT2
            ? (localPos[0] + localPos[1] + localPos[2]) / 3
            : (localPos[p.Def.Through[0]] + localPos[p.Def.Through[1]]) / 2;
        p.SnapArm = -1;
        for (int i = 0; i < p.Arms.Length && p.SnapArm < 0; i++)
        {
            var road = p.Arms[i].Road;
            if (!PlacedEnds.TryGetValue(road, out var q) || q.Plan == p || q.Plan.SnapArm >= 0 && q.Plan.SnapTo.Plan == p) continue;
            var lr = localPos[p.NodeOf[i]] - localCenter; lr.Y = 0;
            if (Interchanges.ArcLengthOf(road) >= lr.Length() + MinTail) continue;
            p.SnapArm = i; p.SnapTo = q;
            var qk = q.Plan.NodeOf[q.Arm];
            var qIn = q.Plan.Inward[qk];
            bestRot = Wrap(MathF.Atan2(qIn.Z, qIn.X) - Ang(Out(p.NodeOf[i])));
            p.Bend[i] = 0;
        }
        if (p.SnapArm >= 0 && p.SnapArm == originArm)
        {
            int alt = Enumerable.Range(0, p.Arms.Length).FirstOrDefault(i => i != p.SnapArm &&
                (!StartsAt(p.Arms[i].Road, p.Anchor) || (!Fixed.Contains(p.Arms[i].Road) && p.Arms[i].Road.Runs.All(x => !x.Oneway))), -1);
            if (alt < 0) { reason = "kein Ursprungsknoten neben der direkten Prefab-Verbindung"; return false; }
            originArm = alt; p.Origin = p.NodeOf[alt];
        }
        if (p.SnapArm >= 0)
            for (int i = 0; i < p.Arms.Length; i++)   // Bögen zur neuen Drehung neu bestimmen
                if (i != p.SnapArm)
                    p.Bend[i] = BendLength(MathF.Abs(Wrap(Ang(p.Arms[i].Away) - Ang(Out(p.NodeOf[i])) - bestRot)) * 180 / MathF.PI);

        // Drehung als Quaternion: lokalen Ausgang von Knoten 0 auf die Zielrichtung drehen
        var v0 = Out(p.NodeOf[0]);
        var target = new Vector2(MathF.Cos(Ang(v0) + bestRot), MathF.Sin(Ang(v0) + bestRot));
        p.Rotation = Interchanges.YawTo(ppd, unit, new Vector3(v0.X, 0, v0.Y), new Vector3(target.X, 0, target.Y));
        var placed = Interchanges.CalibrateNodes(ppd, unit, p.Rotation);
        // Kreuzungsmitte = Mitte der beiden durchgehenden Knoten
        var center = p.Def == FSPLIT || p.Def == FJOIN || p.Def == SPLIT2
            ? (placed[0] + placed[1] + placed[2]) / 3
            : (placed[p.Def.Through[0]] + placed[p.Def.Through[1]]) / 2;
        var shift = p.SnapArm >= 0
            ? p.SnapTo.Plan.NodePos[p.SnapTo.Plan.NodeOf[p.SnapTo.Arm]] - placed[p.NodeOf[p.SnapArm]]
            : p.Anchor - center;
        if (p.SnapArm >= 0 && Vector2.Distance(P(V(p.Anchor)), P(V(center + shift))) > 30)
        { reason = "direkte Prefab-Verbindung läge zu weit vom OSM-Knoten"; return false; }
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
            if (i == p.SnapArm) continue;
            float arc = (float)Interchanges.ArcLengthOf(p.Arms[i].Road);
            if (arc < r + MinTail && OtherEndPending(p.Arms[i].Road, p.Anchor) && arc > r + 1.5f)
            { p.Bend[i] = 0; continue; }   // Nachbar-Prefab schließt dann direkt an
            if (arc < r + MinTail)
            { reason = $"{p.Arms[i].Road.Id} zu kurz ({arc:0} m, nötig {r + MinTail:0} m)"; return false; }
            // Bogen notfalls verkürzen, damit vom Arm genug übrig bleibt
            p.Bend[i] = MathF.Min(p.Bend[i], MathF.Max(0, arc - r - MinTail));
        }
        // eine Straße kann mit beiden Enden an derselben Kreuzung hängen – dann nicht
        if (p.Arms.Select(a => a.Road).Distinct().Count() != p.Arms.Length)
        { reason = "Straße mit beiden Enden an der Kreuzung"; return false; }
        for (int i = 0; i < p.Arms.Length; i++)
        {
            var a = p.Arms[i];
            if (i == p.SnapArm) { Dropped.Add(a.Road); continue; }
            PlacedEnds[a.Road] = (p, i);
            var n = p.NodePos[p.NodeOf[i]];
            var inward = p.Inward[p.NodeOf[i]];
            if (p.NodeOf[i] == p.Origin)
            {
                if (StartsAt(a.Road, p.Anchor)) Connector.Reverse(a.Road);
                Fixed.Add(a.Road);
            }
            bool atStart = StartsAt(a.Road, p.Anchor);
            // erst den Teil im Prefab entfernen (bis zum Prefab-Knoten), sonst liefe die
            // Straße vom Prefab-Knoten zurück zur Kreuzungsmitte
            float rad = Vector2.Distance(new Vector2(n.X, n.Z), new Vector2(p.Anchor.X, p.Anchor.Z));
            Overlaps.CutEnd(a.Road, atStart, rad);
            float lead = MathF.Min(Lead, MathF.Max(2f, (float)Interchanges.ArcLengthOf(a.Road) - rad - 4f));
            if (atStart) Interchanges.SetHead(a.Road, n, -inward, lead, p.Bend[i]);
            else Interchanges.SetTail(a.Road, n, inward, lead, p.Bend[i]);
            if (atStart) a.Road.Start = new EndInfo { Kind = "prefab" };
            else a.Road.End = new EndInfo { Kind = "prefab" };
        }
        return true;
    }

    /// <summary>Index eines ppd-Knotens in Prefab.Nodes nach ChangeOrigin.</summary>
    public static ushort NodeIndex(Plan p, ushort ppdNode, int count) => (ushort)((ppdNode - p.Origin + count) % count);

    private static double[] V(Vector3 v) => [v.X, v.Y, v.Z];

    private static IEnumerable<int[]> Permutations(int n)
    {
        if (n == 1) { yield return [0]; yield break; }
        yield return [0, 1];
        yield return [1, 0];
    }

    public static int RotFixed;

    /// <summary>
    /// Nach dem Anhängen der Straßen die Knotendrehungen erneut setzen: entlang der Straße
    /// aus dem Prefab heraus (Straßenanfang) bzw. hinein (Straßenende). Attach/ChangeOrigin
    /// hinterließen sonst teils quer oder rückwärts stehende Knoten → gefaltete Straßenstücke.
    /// </summary>
    public static void FixNodeRotations(Plan p, Prefab pf)
    {
        if (Environment.GetEnvironmentVariable("OSM2ETS_NO_ROTFIX") is not null) return;
        for (int i = 0; i < p.Arms.Length; i++)
        {
            if (Dropped.Contains(p.Arms[i].Road)) continue;
            var want = p.NodePos[p.NodeOf[i]];
            var node = pf.Nodes.MinBy(nd => Vector3.DistanceSquared(nd.Position, want))!;
            var inward = p.Inward[p.NodeOf[i]];
            // Richtung aus der Lage des Nachbarpunkts der Straße prüfen (robust gegen Vorzeichen)
            var road = p.Arms[i].Road;
            bool start = StartsAt(road, node.Position);
            var nb = MapWriter.ToVector(start ? road.Points[1] : road.Points[^2]);
            var away = nb - node.Position; away.Y = 0;
            var dir = start ? -inward : inward;
            if (away.LengthSquared() > 1e-4f && Vector3.Dot(Vector3.Normalize(away), start ? dir : -dir) < 0) dir = -dir;
            var rot = MathEx.GetNodeRotation(dir);
            if (Vector3.Dot(Vector3.Transform(-Vector3.UnitZ, node.Rotation), Vector3.Transform(-Vector3.UnitZ, rot)) < 0.999f) RotFixed++;
            node.Rotation = rot;
        }
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
        if (p.Origin != 0) pf.ChangeOrigin(p.Origin);
        // ChangeOrigin ordnet die Knoten um und kann ihre Drehungen dabei neu setzen. Danach
        // passten Knotenrichtung und Straße an 36 Stellen nicht mehr (86–179° daneben): Die
        // Straße lief aus dem Prefab-Knoten quer oder rückwärts heraus und faltete sich zu
        // einem schmalen Keil. Deshalb die Drehung je Knoten (nach Lage gesucht) neu setzen.
        if (Environment.GetEnvironmentVariable("OSM2ETS_NO_ROTFIX") is null)
            for (int i = 0; i < p.Arms.Length; i++)
            {
                var want = p.NodePos[p.NodeOf[i]];
                var node = pf.Nodes.MinBy(nd => Vector3.DistanceSquared(nd.Position, want))!;
                var inward = p.Inward[p.NodeOf[i]];
                var rot = MathEx.GetNodeRotation(StartsAt(p.Arms[i].Road, node.Position) ? -inward : inward);
                if (Vector3.Dot(Vector3.Transform(-Vector3.UnitZ, node.Rotation), Vector3.Transform(-Vector3.UnitZ, rot)) < 0.999f) RotFixed++;
                node.Rotation = rot;
            }
        p.Placed = pf;
        PrefabFootprint.Register(pf, ppd);
        return pf;
    }
}
