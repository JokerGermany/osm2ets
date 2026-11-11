// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using TruckLib;
using TruckLib.HashFs;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Setzt an Autobahn-Anschlussstellen (Richtungsfahrbahn mit 2 Spuren, Rampe rechts)
/// SCS-Prefabs ein:
///   Ausfahrt: Aufweitung 2→3 (ger_hw2-hw2_exit_mirrored) + Teilung 3→2+1 (ger_hw2_exit_joint_mirrored)
///   Einfahrt: Einmündung 2+1→3 (ger_hw2_exit_joint) + Spurende 3→2 (ger_hw2-hw2_exit)
/// Knotenindizes und Spurzahlen stammen aus den .ppd-Dateien von ETS2 1.61.
/// </summary>
public static class Interchanges
{
    public sealed record PrefabDef(string Unit, string PpdPath, string Variant, string Look);

    public static readonly PrefabDef Widen = new("ch_4202r", "/prefab2/fork_temp/ger/ger_hw2-hw2_exit_mirrored_tmpl_v02.ppd", "default", "ger_1");
    /// <summary>Spurende 4 → 3 nach der Einfahrt 3 + 1 → 4 (ch_4202s „ger hw3-hw3 exit tmpl“,
    /// Grundspiel, prefab.mod_switzerland_reskin.sii; aus: OSM2ETS_NO_HW3NARROW=1).</summary>
    public static bool UseHw3Narrow = Environment.GetEnvironmentVariable("OSM2ETS_NO_HW3NARROW") != "1";

    public static readonly PrefabDef Split = new("ch_4202p", "/prefab2/fork_temp/ger/ger_hw2_exit_joint_mirrored_tmpl_v02.ppd", "default", "ger_1");
    public static readonly PrefabDef Join = new("ch_4200c", "/prefab2/fork_temp/ger/ger_hw2_exit_joint_tmpl_v02.ppd", "default", "ger_1");
    public static readonly PrefabDef Narrow = new("ch_4202q", "/prefab2/fork_temp/ger/ger_hw2-hw2_exit_tmpl_v02.ppd", "default", "ger_1");

    // Knotenrollen je Prefab (Index in der .ppd) – für alle Sätze gleich
    private const ushort WidenIn = 0, WidenOut = 1;
    private const ushort SplitIn = 0, SplitMain = 1, SplitRamp = 2;
    private const ushort JoinMain = 0, JoinOut = 1, JoinRamp = 2;
    private const ushort NarrowIn = 0, NarrowOut = 1;

    /// <summary>Prefab-Satz einer Anschlussstelle: Aufweitung + Teilung (Ausfahrt) bzw.
    /// Zusammenführung + Spurende (Einfahrt), Spurzahlen von Hauptfahrbahn und Rampe.</summary>
    public sealed record Kit(string Name, int MainLanes, int RampLanes, PrefabDef? Widen, PrefabDef? Split,
                             PrefabDef? Join, PrefabDef? Narrow, string ExtRole)
    {
        /// <summary>Weitere Aufweitung VOR <see cref="Widen"/> (Ausfahrt mit drei Prefabs).</summary>
        public PrefabDef? Widen0 { get; init; }
    }

    /// <summary>2 Spuren, einspurige Rampe (Grundspiel-Vorlagen).</summary>
    public static readonly Kit Hw2 = new("hw2", 2, 1, Widen, Split, Join, Narrow, "ext");

    /// <summary>
    /// 3 Spuren, zweispurige Ausfahrt (4 Spuren vor der Teilung, die rechte Hauptspur führt in
    /// beide Richtungen). Prefabs aus mod_germany: Aufweitung mod_ger_38
    /// (ger_hw3-hw3_exit_mirrored_tmpl, Variante sh_ger laut .pmd) und Teilung mod_ger_29
    /// (ger_hw3_exit_double). Variante/Look von mod_ger_29: .pmd lag nicht vor – angenommen
    /// "default"/"ger_1". Nur Ausfahrten; eine passende Einfahrt fehlt.
    /// </summary>
    public static readonly Kit Hw3Double = new("hw3 double", 3, 2,
        new("mod_ger_38", "/prefab2/fork_temp/ger/ger_hw3-hw3_exit_mirrored_tmpl.ppd", "sh_ger", "ger_1"),
        new("mod_ger_29", "/prefab2/fork_temp/ger/ger_hw3_exit_double.ppd",
            Environment.GetEnvironmentVariable("OSM2ETS_HW3DOUBLE_VARIANT") ?? "default", "ger_1"),
        null, null, "ext3");

    /// <summary>
    /// 2 Spuren, Gabelung in zwei zweispurige Äste (Autobahnende an zwei zweispurigen Rampen,
    /// A 70 am Dreieck Bayreuth/Kulmbach): Aufweitung 2 → 3 (ch_4202r) und Teilung 3 → 2 + 2
    /// (ch_42007 „ger hw2 exit double“, Grundspiel; Varianten laut .pmd: default, no_lines,
    /// lines_ch). Die rechte Spur führt in beide Äste.
    /// </summary>
    public static readonly Kit Split22 = new("hw2 double", 2, 2, Widen,
        new("ch_42007", "/prefab2/fork_temp/ger/ger_hw2_exit_double_v02.ppd",
            Environment.GetEnvironmentVariable("OSM2ETS_SPLIT22_VARIANT") ?? "default", "ger_1"),
        null, null, "ext");

    /// <summary>
    /// 3 Spuren + zweispurige Rampe → 4 Spuren (ger_2i037 „ger hw3 r2 merge double“, mod_germany;
    /// Varianten laut .pmd: default, no_lines, lines_ch). Ohne Spurende: Die vierspurige
    /// Fahrbahn danach (Rolle ext3, ger18) muss an einer Ausfahrt <see cref="Hw3Exit"/> enden.
    /// </summary>
    public static readonly Kit Hw3Merge2 = new("hw3 merge double", 3, 2, null, null,
        new("ger_2i037", "/prefab2/fork_temp/ger/ger_hw3_r2_merge_double.ppd", "default", "ger_1"), null, "ext3");

    /// <summary>
    /// 4 Spuren → 3 Spuren + einspurige Ausfahrt ohne Aufweitung (mod_ger_31
    /// „ger_hw3_exit_joint_mirrored_tmpl“, mod_germany; Varianten laut .pmd: sh_ger, sh_ger_wall,
    /// sh_wall, sh_no_lines). Nur am Ende einer vierspurigen Fahrbahn nach <see cref="Hw3Merge2"/>.
    /// </summary>
    public static readonly Kit Hw3Exit = new("hw3 exit", 3, 1, null,
        new("mod_ger_31", "/prefab2/fork_temp/ger/ger_hw3_exit_joint_mirrored_tmpl.ppd", "sh_ger", "ger_1"), null, null, "ext3");

    /// <summary>
    /// 3 Spuren + einspurige Rampe → 4 Spuren (ch_4202j „ger hw3 exit joint tmpl“, Grundspiel;
    /// Varianten laut .pmd: default, no_lines, wall). Ein Spurende 4 → 3 dazu ist nicht bekannt
    /// (ger_hw3-hw3_exit_tmpl_v02 ohne Token): Der Beschleunigungsstreifen läuft als vierspurige
    /// Straße (ext3, ger18) so lang wie in OSM und endet am Wechsel auf ger16 ohne Übergang.
    /// </summary>
    public static readonly Kit Hw3Join = new("hw3 join", 3, 1, null, null,
        new("ch_4202j", "/prefab2/fork_temp/ger/ger_hw3_exit_joint_tmpl_v02.ppd", "default", "ger_1"),
        UseHw3Narrow ? new("ch_4202s", "/prefab2/fork_temp/ger/ger_hw3-hw3_exit_tmpl_v02.ppd", "default", "ger_1") : null, "ext3");

    /// <summary>Einfahrt 3 + 1 → 4 (aus: OSM2ETS_NO_HW3JOIN=1).</summary>
    public static bool UseHw3Join = Environment.GetEnvironmentVariable("OSM2ETS_NO_HW3JOIN") != "1";

    private static void FindHw3Join(Dictionary<long, List<(RoadEntry R, bool AtStart)>> ends, List<Plan> plans,
                                    HashSet<long> handled, List<string> warnings)
    {
        foreach (var (node, list) in ends)
        {
            if (list.Count != 3 || handled.Contains(node)) continue;
            var in3 = list.Where(e => !e.AtStart && IsMotorwayLanes(e.R, 3)).ToList();
            var out3 = list.Where(e => e.AtStart && IsMotorwayN(e.R, true, 3) && AuxRun(e.R, true, 3) >= 0).ToList();
            var r1 = list.Where(e => !e.AtStart && IsRamp(e.R) && e.R.Runs.All(x => x.Lanes is null or 1)).ToList();
            if (in3.Count != 1 || out3.Count != 1 || r1.Count != 1) continue;
            var pl = MakePlan(node, "entry", Hw3Join, in3[0].R, out3[0].R, r1[0].R, warnings);
            if (pl is null) continue;
            plans.Add(pl); handled.Add(node);
            NarrowAtNode(in3[0].R, false, 3);
            if (Hw3Join.Narrow is not null) NarrowAtNode(out3[0].R, true, 3);     // Spurende 4 → 3 im Prefab
            else foreach (var aux in AuxRuns(out3[0].R, true, 3)) aux.Role = Hw3Join.ExtRole;   // vierspurig bis zum Wechsel                      // Beschleunigungsstreifen vierspurig
            OneLane.Add(r1[0].R);
        }
        // Gegenstück: Verzögerungsstreifen (4 Spuren am Knoten) → 3 Spuren + einspurige Ausfahrt
        foreach (var (node, list) in ends)
        {
            if (list.Count != 3 || handled.Contains(node)) continue;
            var in4 = list.Where(e => !e.AtStart && IsMotorwayN(e.R, false, 3) && AuxRun(e.R, false, 3) >= 0).ToList();
            var out3 = list.Where(e => e.AtStart && IsMotorwayLanes(e.R, 3)).ToList();
            var r1 = list.Where(e => e.AtStart && IsRamp(e.R) && !IsLinkLanes(e.R, true, 2)).ToList();   // zweispurige: hw3 double
            if (in4.Count != 1 || out3.Count != 1 || r1.Count != 1) continue;
            if (UseHw3Double && OneLaneStart.Contains(r1[0].R.Id))
            {
                // Ausfahrt wird nach wenigen Metern zweispurig (A 9 AS 40b/40a): hw3 double 4 → 3 + 2
                var pd = MakePlan(node, "exit", Hw3Double, in4[0].R, out3[0].R, r1[0].R, warnings);
                if (pd is null) continue;
                plans.Add(pd); handled.Add(node);
                NarrowAtNode(in4[0].R, false, 3);
                NarrowAtNode(out3[0].R, true, 3);
                r1[0].R.Runs[0].Lanes = 2;
                TwoLane.Add(r1[0].R);
                continue;
            }
            var pl = MakePlan(node, "exit", Hw3Exit, in4[0].R, out3[0].R, r1[0].R, warnings);
            if (pl is null) continue;
            plans.Add(pl); handled.Add(node);
            NarrowAtNode(out3[0].R, true, 3);
            foreach (var aux in AuxRuns(in4[0].R, false, 3)) aux.Role = Hw3Exit.ExtRole;   // Verzögerungsstreifen vierspurig
            OneLane.Add(r1[0].R);
        }
    }

    /// <summary>Vierspurige Strecke zwischen Einmündung 3 + 2 und Ausfahrt 4 → 3 + 1 (aus: OSM2ETS_NO_LANE4=1).</summary>
    public static bool UseLane4 = Environment.GetEnvironmentVariable("OSM2ETS_NO_LANE4") != "1";

    /// <summary>Rampe mit n Spuren; am Anfang (atStart) darf ein kurzer Lauf mit n+1 Spuren liegen.</summary>
    internal static bool IsLinkLanes(RoadEntry r, bool atStart, int n)
    {
        if (!IsRamp(r)) return false;
        for (int i = 0; i < r.Runs.Count; i++)
            if (!InAux(r, i, n) && r.Runs[i].Lanes != n) return false;
        return HasCore(r, n);
    }

    /// <summary>Autobahn mit n Spuren; am Anfang und am Ende darf je ein kurzer Lauf mit n+1 Spuren liegen.</summary>
    internal static bool IsMotorwayLanes(RoadEntry r, int n)
    {
        if (!r.Runs.All(x => x.Oneway && x.Highway == "motorway")) return false;
        for (int i = 0; i < r.Runs.Count; i++)
            if (!InAux(r, i, n) && r.Runs[i].Lanes != n) return false;
        return HasCore(r, n);
    }

    /// <summary>
    /// Vierspurige Strecken: Einmündung 3 + 2 → 4 am Anfang und Ausfahrt 4 → 3 + 1 am Ende derselben
    /// Fahrbahn. Beide Knoten bekommen ihr Prefab, die Fahrbahn dazwischen die Rolle ext3.
    /// </summary>
    private static void FindLane4(Dictionary<long, List<(RoadEntry R, bool AtStart)>> ends, List<Plan> plans,
                                  HashSet<long> handled, List<string> warnings)
    {
        var entries = new Dictionary<RoadEntry, (long Node, RoadEntry In, RoadEntry Ramp)>();
        var exits = new Dictionary<RoadEntry, (long Node, RoadEntry Out, RoadEntry Ramp)>();
        foreach (var (node, list) in ends)
        {
            if (list.Count != 3) continue;
            var in3 = list.Where(e => !e.AtStart && IsMotorwayLanes(e.R, 3)).ToList();
            var out4 = list.Where(e => e.AtStart && IsMotorwayLanes(e.R, 4)).ToList();
            var r2 = list.Where(e => !e.AtStart && IsLinkLanes(e.R, false, 2)).ToList();
            if (in3.Count == 1 && out4.Count == 1 && r2.Count == 1) entries[out4[0].R] = (node, in3[0].R, r2[0].R);
            var in4 = list.Where(e => !e.AtStart && IsMotorwayLanes(e.R, 4)).ToList();
            var out3 = list.Where(e => e.AtStart && IsMotorwayLanes(e.R, 3)).ToList();
            var r1 = list.Where(e => e.AtStart && IsRamp(e.R) && e.R.Runs.All(x => x.Lanes is null or 1)).ToList();
            if (in4.Count == 1 && out3.Count == 1 && r1.Count == 1) exits[in4[0].R] = (node, out3[0].R, r1[0].R);
        }
        foreach (var (road, en) in entries)
        {
            if (!exits.TryGetValue(road, out var ex)) continue;
            var pe = MakePlan(en.Node, "entry", Hw3Merge2, en.In, road, en.Ramp, warnings);
            var px = MakePlan(ex.Node, "exit", Hw3Exit, road, ex.Out, ex.Ramp, warnings);
            if (pe is null || px is null) continue;
            plans.Add(pe); plans.Add(px);
            handled.Add(en.Node); handled.Add(ex.Node);
            NarrowAtNode(en.In, false, 3); NarrowAtNode(ex.Out, true, 3);
            NarrowAtNode(road, true, 4); NarrowAtNode(road, false, 4);
            foreach (var x in road.Runs) x.Role = Hw3Merge2.ExtRole;
            TwoLane.Add(en.Ramp); OneLane.Add(ex.Ramp);
        }
    }

    private static Plan? MakePlan(long node, string kind, Kit kit, RoadEntry mainIn, RoadEntry mainOut, RoadEntry ramp, List<string> warnings)
    {
        var dir = Direction(mainIn, mainOut);
        var right = new Vector2(-dir.Z, dir.X);
        var rampAway = Away(ramp, kind == "exit");
        if (Vector2.Dot(rampAway, right) <= 0.05f) { warnings.Add($"Knoten {node}: Rampe links – noch nicht unterstützt"); return null; }
        return new Plan
        {
            Kind = kind, K = kit, OsmNode = node, MainIn = mainIn, MainOut = mainOut, Ramp = ramp,
            Anchor = MapWriter.ToVector(mainIn.Points[^1]), Direction = dir,
        };
    }

    /// <summary>
    /// Spurende 2 → 1 mitten in einer Rampe (ch_4202l „ger hw2-hw1 merge“, Grundspiel; Varianten laut
    /// .pmd: default, no_lines). Die Rampe wird am Wechsel der Spurzahl geteilt (Plan-Art "drop").
    /// </summary>
    public static readonly Kit LaneDrop = new("hw2-hw1 drop", 2, 0, null, null, null,
        new("ch_4202l", "/prefab2/fork_temp/ger/ger_hw2-hw1_merge_v02.ppd", "default", "ger_1"), "ext");

    /// <summary>Zusammenführung zweier Rampen (2 + 1 → 2 mit Beschleunigungsstreifen) und Spurende
    /// 2 → 1 in Rampen (aus: OSM2ETS_NO_LINKJOIN=1).</summary>
    public static bool UseLinkJoin = Environment.GetEnvironmentVariable("OSM2ETS_NO_LINKJOIN") != "1";

    private static long synthNode = -1_000_000;

    /// <summary>Von <see cref="PrepareLaneDrops"/> geteilte Rampen, abgeholt von <see cref="Find"/>.</summary>
    private static readonly List<(long Node, RoadEntry A, RoadEntry B)> PendingDrops = [];

    /// <summary>Vor der Spurzahl-Vereinheitlichung (LaneSmoothing) aufrufen – sie würde den
    /// Wechsel 2 → 1 sonst einebnen.</summary>
    public static int PrepareLaneDrops(List<RoadEntry> roads)
    {
        PendingDrops.Clear();
        if (UseLinkJoin) PendingDrops.AddRange(SplitLaneDrops(roads));
        RecordLanes(roads);
        return PendingDrops.Count;
    }

    /// <summary>
    /// Rampen, deren Spurzahl einmal von 2 auf 1 fällt (beide Teile ≥ 80 m), am Wechsel teilen.
    /// Der neue Knoten bekommt eine künstliche (negative) Nummer; dort steht später das Spurende.
    /// </summary>
    internal static List<(long Node, RoadEntry A, RoadEntry B)> SplitLaneDrops(List<RoadEntry> roads)
    {
        var res = new List<(long, RoadEntry, RoadEntry)>();
        foreach (var r in roads.ToList())
        {
            if (!IsRamp(r) || r.Runs.Count < 2) continue;
            int k = r.Runs.FindIndex(x => x.Lanes == 1);
            if (k <= 0) continue;
            if (r.Runs.Take(k).Any(x => x.Lanes is null or < 2) || r.Runs.Skip(k).Any(x => x.Lanes != 1)) continue;
            if (r.Runs.Any(x => x.Bridge || x.Tunnel)) continue;
            int b = r.Runs[k].From;
            double la = 0, lb = 0;
            for (int i = 0; i < b; i++) la += Dist(r.Points[i], r.Points[i + 1]);
            for (int i = b; i < r.Points.Count - 1; i++) lb += Dist(r.Points[i], r.Points[i + 1]);
            if (la < 80 || lb < 80) continue;
            long id = synthNode--;
            var tail = new RoadEntry
            {
                Id = r.Id + "d", Chain = r.Chain, Piece = r.Piece, Role = r.Role,
                Start = new EndInfo { Kind = "node", OsmNode = id }, End = r.End,
                Points = r.Points.Skip(b).ToList(),
                Runs = r.Runs.Skip(k).Select(x => { x.From -= b; x.To -= b; return x; }).ToList(),
                LengthM = lb,
            };
            r.Points = r.Points.Take(b + 1).ToList();
            r.Runs = r.Runs.Take(k).ToList();
            r.End = new EndInfo { Kind = "node", OsmNode = id };
            r.LengthM = la;
            roads.Add(tail);
            res.Add((id, r, tail));
        }
        return res;
    }

    /// <summary>Gabelung in zwei zweispurige Äste (aus: OSM2ETS_NO_SPLIT22=1).</summary>
    public static bool UseSplit22 = Environment.GetEnvironmentVariable("OSM2ETS_NO_SPLIT22") != "1";

    /// <summary>Seitlicher Abstand zusätzlicher Spurpunkte unter dem Seitenstreifen (0 = keine).</summary>
    public static float ShoulderOf(TruckLib.Token model)
    {
        foreach (var (k, off) in new[] { (Hw3Double, PrefabFootprint.ShoulderOffset3), (Hw3Merge2, PrefabFootprint.ShoulderOffset3), (Hw3Exit, PrefabFootprint.ShoulderOffset3), (Hw3Join, PrefabFootprint.ShoulderOffset3), (Split22, PrefabFootprint.ShoulderOffset), (LaneDrop, PrefabFootprint.ShoulderOffset), (Hw2, PrefabFootprint.ShoulderOffset) })
            foreach (var d in new[] { k.Widen0, k.Widen, k.Split, k.Join, k.Narrow })
                if (d is not null && (TruckLib.Token)d.Unit == model) return off;
        return 0f;
    }

    /// <summary>Zweispurige Ausfahrt von 3 Spuren (aus: OSM2ETS_NO_HW3DOUBLE=1).</summary>
    public static bool UseHw3Double = Environment.GetEnvironmentVariable("OSM2ETS_NO_HW3DOUBLE") != "1";

    /// <summary>Anschluss-Prefabs weglassen, wenn im Prefab-Bereich eine Brücke/ein Tunnel liegt
    /// (OSM2ETS_PREFAB_NOBRIDGE=1). Standard: aus – sonst fallen z. B. in Bayreuth alle vier weg.</summary>
    public static bool SkipAtStructures = Environment.GetEnvironmentVariable("OSM2ETS_PREFAB_NOBRIDGE") is not null;

    public const float RampTrim = 25f;     // Rampe so weit vom Knoten kürzen (m)
    public const float Slack = 5f;
    /// <summary>Abstand zwischen Bauwerksende und äußerem Prefab (m), wenn eine Zusatzspur-Straße
    /// das Bauwerk überbrückt.</summary>
    public const float ExtMargin = 4f;
    /// <summary>Längste Zusatzspur-Straße zwischen den beiden Prefabs einer Anschlussstelle (m).</summary>
    public const float MaxExt = 160f;
    /// <summary>Endstücke der Zusatzspur-Straße bis <see cref="ExtMargin"/> + 1,5 m zwischen Prefab und
    /// Brücke gehören zur Brücke (aus: OSM2ETS_PREFAB_KEEPSTUBS).</summary>
    public static bool AbsorbStubs = Environment.GetEnvironmentVariable("OSM2ETS_PREFAB_KEEPSTUBS") is null;
    /// <summary>Zusatzspur-Straße über Bauwerke (aus: OSM2ETS_PREFAB_NOEXT).</summary>
    public static bool UseExt = Environment.GetEnvironmentVariable("OSM2ETS_PREFAB_NOEXT") is null;
    /// <summary>Weiteste Verschiebung des inneren Prefabs weg von einem Bauwerk (m), siehe
    /// <see cref="Plan.Shift"/> (aus: OSM2ETS_PREFAB_NOSHIFT).</summary>
    public const float MaxShift = 30f;
    public static bool UseShift = Environment.GetEnvironmentVariable("OSM2ETS_PREFAB_NOSHIFT") is null;
    /// <summary>Rolle der Abschnitte einer Zusatzspur-Straße (Zuordnung: Straßentyp "hw2 ext one lane").</summary>
    public const string ExtRole = "ext";

    public sealed class Plan
    {
        public required string Kind;        // "exit" | "entry"
        public Kit K = Hw2;
        /// <summary>So weit wurde die Einmündung stromauf gerückt (siehe MoveMergeUpstream).</summary>
        public float Moved;
        /// <summary>Zusatzspur-Straße wegen Kurve (nicht wegen Bauwerk).</summary>
        public bool ExtForCurve;
        public required long OsmNode;
        public required RoadEntry MainIn, MainOut, Ramp;
        public required Vector3 Anchor;     // Lage des OSM-Knotens
        public required Vector3 Direction;  // Fahrtrichtung der Hauptfahrbahn (x/z)
        public float Grade;                  // Steigung der Hauptfahrbahn über die Prefab-Länge (m/m)
        public bool Blocked;                 // Brücke/Tunnel im Prefab-Bereich
        /// <summary>Liegt ein Bauwerk (Brücke/Tunnel) unter dem äußeren Prefab (Aufweitung bzw.
        /// Spurende), rückt dieses über das Bauwerk hinaus; dazwischen läuft eine Straße mit
        /// Zusatzspur (wie in der SCS-Karte). Länge dieser Straße (m), 0 = Prefabs direkt verbunden.</summary>
        public float Ext;
        /// <summary>Einfahrt direkt hinter dem Spurende-Prefab der Rampe stromauf gerückt.</summary>
        public bool AfterRampPrefab;
        /// <summary>Einfahrt direkt am Spurende-Prefab der Rampe, die Rampenstraße entfällt.</summary>
        public bool DirectRamp;
        /// <summary>Liegt ein Bauwerk unter dem inneren Prefab (Teilung bzw. Zusammenführung), rückt
        /// dieses um Shift Meter vom Bauwerk weg auf die andere Seite des OSM-Knotens (Ausfahrt:
        /// stromab, Einfahrt: stromauf); das Bauwerk liegt dann unter der Zusatzspur-Straße.</summary>
        public float Shift;
        public RoadEntry? ExtRoad;
        public Vector3 FarAnchor, FarDirection;   // äußeres Prefab: Lage des Außenknotens (Achse), Richtung
        public float FarGrade;
    }

    /// <summary>Lädt .ppd-Dateien aus einem entpackten Ordner oder den .scs-Archiven des Spiels.</summary>
    public sealed class PpdSource : IDisposable
    {
        private readonly string? dir;
        private readonly List<IHashFsReader> archives = [];
        private readonly Dictionary<string, PrefabDescriptor> cache = [];

        public PpdSource(string? prefabDir, string? gameDir)
        {
            dir = prefabDir;
            if (gameDir is not null)
                foreach (var a in Directory.EnumerateFiles(gameDir, "*.scs").OrderBy(x => x, StringComparer.Ordinal))
                {
                    try { archives.Add(HashFsReader.Open(a)); } catch { /* nicht lesbar */ }
                }
        }

        public PrefabDescriptor Get(string path)
        {
            if (cache.TryGetValue(path, out var p)) return p;
            if (dir is not null)
            {
                var f = Path.Combine(dir, path.TrimStart('/'));
                if (File.Exists(f)) return cache[path] = PrefabDescriptor.Open(f);
            }
            foreach (var a in archives)
                if (a.TryGetEntry(path, out _) == EntryType.File)
                    return cache[path] = PrefabDescriptor.Open(path, a);
            throw new FileNotFoundException($"Prefab-Beschreibung nicht gefunden: {path}");
        }

        public void Dispose() { foreach (var a in archives) a.Dispose(); }
    }

    // ------------------------------------------------------------------ Planung

    public static List<Plan> Find(List<RoadEntry> roads, List<string> warnings)
    {
        AllRoads = roads;
        OneLane.Clear(); TwoLane.Clear();
        var drops = PendingDrops.Where(d => roads.Contains(d.A) && roads.Contains(d.B)).ToList();
        PendingDrops.Clear();
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
        var handled = new HashSet<long>();
        if (UseLane4) FindLane4(ends, plans, handled, warnings);
        if (UseHw3Join) FindHw3Join(ends, plans, handled, warnings);
        if (UseLinkJoin) FindLink3(ends, plans, handled, warnings);
        foreach (var (node, a, b) in drops)
        {
            plans.Add(new Plan
            {
                Kind = "drop", K = LaneDrop, OsmNode = node, MainIn = a, MainOut = b, Ramp = null!,
                Anchor = MapWriter.ToVector(a.Points[^1]), Direction = Direction(a, b),
            });
            handled.Add(node);
            TwoLane.Add(a);
        }
        if (UseLinkJoin)
            foreach (var (node, list) in ends)
            {
                // zwei Rampen laufen zusammen: zweispurige + einspurige → zweispurig (Beschleunigungsstreifen)
                if (list.Count != 3 || handled.Contains(node)) continue;
                if (!list.All(e => IsRamp(e.R))) continue;
                var in2 = list.Where(e => !e.AtStart && IsLinkLanes(e.R, false, 2)).ToList();
                var in1 = list.Where(e => !e.AtStart && e.R.Runs.All(x => x.Lanes is null or 1)).ToList();
                var out2 = list.Where(e => e.AtStart && IsLinkLanes(e.R, true, 2)).ToList();
                if (in2.Count != 1 || in1.Count != 1 || out2.Count != 1) continue;
                var pl = MakePlan(node, "entry", Hw2, in2[0].R, out2[0].R, in1[0].R, warnings);
                if (pl is null) continue;
                plans.Add(pl); handled.Add(node);
                NarrowAtNode(out2[0].R, true, 2);
                TwoLane.Add(in2[0].R); TwoLane.Add(out2[0].R); OneLane.Add(in1[0].R);
            }
        if (UseLinkJoin)
            foreach (var (node, list) in ends)
            {
                // zweispurige Rampe weitet sich auf 3 Spuren und teilt sich in 2 + 1 (A 9 AS 40b/40a)
                if (list.Count != 3 || handled.Contains(node)) continue;
                if (!list.All(e => IsRamp(e.R))) continue;
                var in2 = list.Where(e => !e.AtStart && Link2AtEnd.Contains(e.R.Id)).ToList();
                var out2 = list.Where(e => e.AtStart && Link2AtStart.Contains(e.R.Id)).ToList();
                var r1 = list.Where(e => e.AtStart && !Link2AtStart.Contains(e.R.Id) && e.R.Runs.All(x => x.Lanes is null or 1)).ToList();
                if (in2.Count != 1 || out2.Count != 1 || r1.Count != 1) continue;
                var pl = MakePlan(node, "exit", Hw2, in2[0].R, out2[0].R, r1[0].R, warnings);
                if (pl is null) continue;
                plans.Add(pl); handled.Add(node);
                foreach (var x in in2[0].R.Runs) x.Lanes = 2;
                NarrowAtNode(out2[0].R, true, 2);
                TwoLane.Add(in2[0].R); TwoLane.Add(out2[0].R); OneLane.Add(r1[0].R);
            }
        foreach (var (node, list) in ends)
        {
            if (list.Count != 3 || handled.Contains(node)) continue;
            var kit = Hw2;
            var mains = list.Where(e => IsMotorway2(e.R, e.AtStart)).ToList();
            if (UseHw3Double && mains.Count == 0)
            {
                // 3-spurige Autobahn mit zweispuriger Ausfahrt
                var m3 = list.Where(e => IsMotorwayN(e.R, e.AtStart, 3)).ToList();
                var r2 = list.Where(e => e.AtStart && IsLinkLanes(e.R, true, 2)).ToList();
                if (m3.Count == 2 && r2.Count == 1) { mains = m3; kit = Hw3Double; }
            }
            var ramps = list.Where(e => IsRamp(e.R)).ToList();
            // Autobahnbeginn/-ende: zwei Anschlussäste und eine Autobahn. Der Ast, der
            // geradeaus in die Autobahn übergeht, ist dann die Hauptfahrbahn.
            if (UseLinkMain && mains.Count == 1 && ramps.Count == 2)
            {
                var m = mains[0];
                var mAway = Away(m.R, m.AtStart);
                var cand = ramps.Where(e => e.AtStart != m.AtStart && (e.R.Runs.All(x => x.Lanes is null) || IsLinkLanes(e.R, e.AtStart, 2)))
                                .Select(e => (e, Dot: -Vector2.Dot(Away(e.R, e.AtStart), mAway)))
                                .OrderByDescending(t => t.Dot).ToList();
                if (cand.Count > 0 && cand[0].Dot > 0.97f && (cand.Count == 1 || cand[1].Dot < cand[0].Dot - 0.005f))
                {
                    mains.Add(cand[0].e);
                    ramps.Remove(cand[0].e);
                }
            }
            if (mains.Count != 2 || ramps.Count != 1) continue;
            // Ausfahrt, bei der Rampe und weiterführender Ast zweispurige Rampen sind: Gabelung 2 + 2
            if (UseSplit22 && kit == Hw2 && ramps[0].AtStart && IsLinkLanes(ramps[0].R, true, 2)
                && mains.FirstOrDefault(e => e.AtStart).R is { } mo && IsLinkLanes(mo, true, 2))
                kit = Split22;
            if (kit.Join is null && !ramps[0].AtStart) continue;
            var mainIn = mains.FirstOrDefault(e => !e.AtStart).R;
            var mainOut = mains.FirstOrDefault(e => e.AtStart).R;
            if (mainIn is null || mainOut is null || mainIn == mainOut) continue;
            var ramp = ramps[0];
            var anchor = MapWriter.ToVector(mainIn.Points[^1]);
            var dir = Direction(mainIn, mainOut);
            var rampDir = ramp.AtStart ? Away(ramp.R, true) : -Away(ramp.R, false);
            // Rampe muss rechts liegen (x/z: rechts von d = (-d.z, d.x))
            var right = new Vector2(-dir.Z, dir.X);
            var rampAway = Away(ramp.R, ramp.AtStart);
            if (Vector2.Dot(rampAway, right) <= 0.05f) { warnings.Add($"Knoten {node}: Rampe links – noch nicht unterstützt"); continue; }
            plans.Add(new Plan
            {
                Kind = ramp.AtStart ? "exit" : "entry", K = kit,
                OsmNode = node, MainIn = mainIn, MainOut = mainOut, Ramp = ramp.R,
                Anchor = anchor, Direction = dir,
            });
            foreach (var m in mains) NarrowAtNode(m.R, m.AtStart, kit.MainLanes);
            if (kit.RampLanes == 1) OneLane.Add(ramp.R);
            else
            {
                // zweispuriger Rampenknoten (bzw. Hauptausgang als Rampe): Rampe muss zweispurig sein
                TwoLane.Add(ramp.R);
                if (IsRamp(mainOut)) TwoLane.Add(mainOut);
            }
        }
        // Spurenden zuerst vorbereiten: Einfahrten dahinter prüfen dann die schon gekürzte Rampe
        return plans.OrderBy(p => p.Kind == "drop" ? 0 : 1).ToList();
    }

    /// <summary>Autobahnbeginn mit Anschlussast als Hauptfahrbahn zulassen (OSM2ETS_NO_LINKMAIN=1 schaltet ab).</summary>
    public static bool UseLinkMain = Environment.GetEnvironmentVariable("OSM2ETS_NO_LINKMAIN") != "1";

    /// <summary>Höchstlänge eines 3-spurigen Laufs am Knoten, der als Zusatzspur gilt (m).</summary>
    public const float MaxAuxLane = 650f;

    /// <summary>
    /// 2-spurige Autobahn. Ein 3-spuriger Lauf direkt am Knoten (bis MaxAuxLane lang,
    /// dahinter 2 Spuren) zählt als Beschleunigungs-/Verzögerungsstreifen: Den enthält das Prefab.
    /// </summary>
    internal static bool IsMotorway2(RoadEntry r, bool atStart)
    {
        if (!r.Runs.All(x => x.Oneway && x.Highway == "motorway")) return false;
        // Zusatzspur am Knoten und (seit LaneSmoothing.KeepAux) auch am anderen Ende erlaubt
        for (int i = 0; i < r.Runs.Count; i++)
            if (!InAux(r, i, 2) && r.Runs[i].Lanes is not (null or 2)) return false;
        return HasCore(r, 2);
    }

    /// <summary>Autobahn mit genau n Spuren (ein Lauf mit n+1 Spuren am Knoten zählt als Zusatzspur).</summary>
    internal static bool IsMotorwayN(RoadEntry r, bool atStart, int n)
    {
        if (!r.Runs.All(x => x.Oneway && x.Highway == "motorway")) return false;
        for (int i = 0; i < r.Runs.Count; i++)
            if (!InAux(r, i, n) && r.Runs[i].Lanes != n) return false;
        return HasCore(r, n);
    }

    /// <summary>Index des 3-spurigen Laufs am Knotenende, der als Zusatzspur gilt, sonst -1.</summary>
    private static int AuxRun(RoadEntry r, bool atStart, int n = 2) =>
        AuxCount(r, atStart, n) > 0 ? (atStart ? 0 : r.Runs.Count - 1) : -1;

    /// <summary>Rampen mit kurzem einspurigem Anfang, vor der Spurglättung erfasst.</summary>
    private static readonly HashSet<string> OneLaneStart = [];

    /// <summary>Zweispurige Rampen (am Ende bzw. Anfang darf eine Zusatzspur liegen), vor der Glättung erfasst.</summary>
    private static readonly HashSet<string> Link2AtEnd = [], Link2AtStart = [];

    /// <summary>Spurbilder der Rampen vor der Spurglättung festhalten. Nicht leeren: ein zweiter
    /// Durchlauf sieht schon geglättete Spurzahlen (Schlüssel: Straßen-ID).</summary>
    private static void RecordLanes(List<RoadEntry> roads)
    {
        foreach (var r in roads)
        {
            if (!IsRamp(r)) continue;
            if (ShortOneLaneStart(r)) OneLaneStart.Add(r.Id);
            // am Ende bzw. Anfang zweispurig (ein 3-spuriger Lauf direkt am Knoten zählt als Zusatzspur);
            // ein kurzer einspuriger Anfang einer Ausfahrt zählt mit
            int ka = AuxCount(r, true, 2), kb = AuxCount(r, false, 2);
            var head = r.Runs[Math.Min(ka, r.Runs.Count - 1)];
            var tail = r.Runs[Math.Max(r.Runs.Count - 1 - kb, 0)];
            if (head.Lanes == 2 || (OneLaneStart.Contains(r.Id) && r.Runs.Count > 1 && r.Runs[1].Lanes == 2)) Link2AtStart.Add(r.Id);
            if (tail.Lanes == 2) Link2AtEnd.Add(r.Id);
            if (r.Runs.All(x => x.Lanes == 3)) Link3.Add(r.Id);
        }
    }

    /// <summary>Durchgehend 3-spurige Rampen (Verteilerfahrbahn), vor der Glättung erfasst.</summary>
    private static readonly HashSet<string> Link3 = [];

    /// <summary>
    /// 3-spurige Verteilerfahrbahn: 2 + 1 Rampen → 3 Spuren (ch_4200c) und am Ende 3 → 2 + 1
    /// (ch_4202p); dazwischen Rolle ext (ger17). Beide Enden müssen passen.
    /// </summary>
    public static readonly Kit LinkJoin3 = new("hw2 join to 3", 2, 1, null, null, Join, null, "ext");
    public static readonly Kit LinkSplit3 = new("hw2 split from 3", 2, 1, null, Split, null, null, "ext");

    private static void FindLink3(Dictionary<long, List<(RoadEntry R, bool AtStart)>> ends, List<Plan> plans,
                                  HashSet<long> handled, List<string> warnings)
    {
        var seen = new HashSet<RoadEntry>();
        foreach (var (node, list) in ends)
            foreach (var (c, atStart) in list)
            {
                if (!Link3.Contains(c.Id) || !seen.Add(c)) continue;
                if (c.Start?.OsmNode is not long sn || c.End?.OsmNode is not long en) continue;
                if (handled.Contains(sn) || handled.Contains(en) || !ends.TryGetValue(sn, out var ls) || !ends.TryGetValue(en, out var le)) continue;
                if (ls.Count != 3 || le.Count != 3 || !ls.All(e => IsRamp(e.R)) || !le.All(e => IsRamp(e.R))) continue;
                var in2 = ls.Where(e => !e.AtStart && Link2AtEnd.Contains(e.R.Id)).ToList();
                var in1 = ls.Where(e => !e.AtStart && !Link2AtEnd.Contains(e.R.Id) && e.R.Runs.All(x => x.Lanes is null or 1)).ToList();
                var out2 = le.Where(e => e.AtStart && Link2AtStart.Contains(e.R.Id)).ToList();
                var out1 = le.Where(e => e.AtStart && !Link2AtStart.Contains(e.R.Id) && e.R.Runs.All(x => x.Lanes is null or 1)).ToList();
                if (in2.Count != 1 || in1.Count != 1 || out2.Count != 1 || out1.Count != 1) continue;
                var pj = MakePlan(sn, "entry", LinkJoin3, in2[0].R, c, in1[0].R, warnings);
                var ps = MakePlan(en, "exit", LinkSplit3, c, out2[0].R, out1[0].R, warnings);
                if (pj is null || ps is null) continue;
                plans.Add(pj); plans.Add(ps); handled.Add(sn); handled.Add(en);
                foreach (var x in c.Runs) { x.Lanes = 3; x.Role = LinkJoin3.ExtRole; }
                foreach (var x in in2[0].R.Runs) x.Lanes = 2;
                NarrowAtNode(out2[0].R, true, 2);
                TwoLane.Add(in2[0].R); TwoLane.Add(out2[0].R); OneLane.Add(in1[0].R); OneLane.Add(out1[0].R);
            }
    }

    /// <summary>Höchstlänge eines einspurigen Anfangs, nach dem eine Ausfahrt zweispurig wird (m).</summary>
    public const float MaxOneLaneStart = 100f;

    /// <summary>Rampe beginnt mit einem kurzen einspurigen Lauf, danach mindestens 2 Spuren.</summary>
    private static bool ShortOneLaneStart(RoadEntry r)
    {
        if (r.Runs.Count < 2 || r.Runs[0].Lanes != 1 || r.Runs[0].Bridge || r.Runs[0].Tunnel) return false;
        if (!r.Runs.Skip(1).All(x => x.Lanes >= 2)) return false;
        double len = 0;
        for (int i = r.Runs[0].From; i < r.Runs[0].To && i + 1 < r.Points.Count; i++)
            len += Vector2.Distance(P(r.Points[i]), P(r.Points[i + 1]));
        return len <= MaxOneLaneStart;
    }

    /// <summary>Mindestens ein Lauf ist keine Zusatzspur (sonst wäre die ganze Straße „Zusatzspur“).</summary>
    private static bool HasCore(RoadEntry r, int n) => Enumerable.Range(0, r.Runs.Count).Any(i => !InAux(r, i, n));

    /// <summary>Anzahl der Läufe am Ende (atStart: Anfang) mit n+1 Spuren, zusammen höchstens
    /// MaxAuxLane lang, ohne Brücke/Tunnel – eine Zusatzspur kann über mehrere OSM-Wege gehen.
    /// 0, wenn keine Zusatzspur oder die ganze Straße so wäre.</summary>
    internal static int AuxCount(RoadEntry r, bool atStart, int n = 2)
    {
        int step = atStart ? 1 : -1, i = atStart ? 0 : r.Runs.Count - 1, c = 0;
        double len = 0;
        for (; i >= 0 && i < r.Runs.Count && r.Runs[i].Lanes == n + 1 && !r.Runs[i].Bridge && !r.Runs[i].Tunnel; i += step, c++)
            for (int j = r.Runs[i].From; j < r.Runs[i].To && j + 1 < r.Points.Count; j++)
                len += Vector2.Distance(P(r.Points[j]), P(r.Points[j + 1]));
        return c == 0 || c == r.Runs.Count || len > MaxAuxLane ? 0 : c;
    }

    private static bool InAux(RoadEntry r, int i, int n) =>
        i < AuxCount(r, true, n) || i >= r.Runs.Count - AuxCount(r, false, n);

    /// <summary>Läufe der Zusatzspur am Ende bzw. Anfang.</summary>
    internal static IEnumerable<RunEntry> AuxRuns(RoadEntry r, bool atStart, int n)
    {
        int c = AuxCount(r, atStart, n);
        return atStart ? r.Runs.Take(c) : r.Runs.Skip(r.Runs.Count - c);
    }

    internal static void NarrowAtNode(RoadEntry r, bool atStart, int n = 2)
    {
        foreach (var x in AuxRuns(r, atStart, n).ToList()) x.Lanes = n;
    }

    /// <summary>
    /// Rampen werden zweispurig gebaut (Rolle link2), wenn alle Läufe mindestens 2 Spuren haben,
    /// sie an keinem Prefab mit einspurigem Rampenknoten hängen und jede an einem Knoten
    /// anschließende Rampe ebenso zweispurig ist – ein Wechsel des Straßentyps ohne Übergangs-
    /// Prefab gäbe eine Stufe. Spurzahlen bleiben unverändert (Kreuzungs-Prefabs nutzen sie).
    /// Nach den Anschluss-Prefabs aufrufen. Gibt die Zahl der zweispurigen Rampen zurück.
    /// </summary>
    /// <summary>Rolle der Läufe zweispuriger Rampen (Zuordnung "Autobahnrampe zweispurig").</summary>
    public const string TwoLaneLinkRole = "link2";
    /// <summary>Rampen an Prefabs mit einspurigem Rampenknoten.</summary>
    public static readonly HashSet<RoadEntry> OneLane = [];
    /// <summary>Rampen an zweispurigen Prefab-Knoten: zweispurig auch neben einspurigen Nachbarn.</summary>
    public static readonly HashSet<RoadEntry> TwoLane = [];

    public static int NormalizeLinkLanes(List<RoadEntry> roads)
    {
        // Zusatzspur-Straßen zwischen Anschluss-Prefabs behalten ihre Rolle (ext/ext3)
        static bool Link(RoadEntry r) => r.Runs.All(x => x.Highway is "motorway_link" or "trunk_link")
                                         && !r.Runs.Any(x => x.Role is ExtRole or "ext3");
        var links = roads.Where(Link).ToList();
        var two = links.Where(r => r.Runs.All(x => x.Lanes >= 2) && !OneLane.Contains(r)).ToHashSet();
        // Ebenso einspurig, wenn eine anschließende Rampe einspurig ist (Wechsel am Knoten = Stufe)
        var byNode = new Dictionary<long, List<RoadEntry>>();
        foreach (var r in links)
            foreach (var e in new[] { r.Start, r.End })
                if (e?.Kind == "node" && e.OsmNode is long id)
                    (byNode.TryGetValue(id, out var l) ? l : byNode[id] = []).Add(r);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var l in byNode.Values)
                if (l.Any(r => !two.Contains(r)))
                    foreach (var r in l) changed |= two.Remove(r);
        }
        two.UnionWith(TwoLane.Where(r => links.Contains(r) && r.Runs.All(x => x.Lanes >= 2)));
        two.ExceptWith(OneLane);
        foreach (var r in two)
            foreach (var x in r.Runs) x.Role = TwoLaneLinkRole;
        return two.Count;
    }

    /// <summary>Einbahn-Rampe; ein Stück Zufahrt (service) am Ende zählt mit, z. B. zu einem Rastplatz.</summary>
    private static bool IsRamp(RoadEntry r) =>
        r.Runs.All(x => x.Oneway && x.Highway is "motorway_link" or "trunk_link" or "service")
        && r.Runs.Any(x => x.Highway is "motorway_link" or "trunk_link");

    private static Vector3 Direction(RoadEntry mainIn, RoadEntry mainOut)
    {
        // Richtung über ~60 m vor dem Knoten
        var p = mainIn.Points;
        var end = MapWriter.ToVector(p[^1]);
        Vector3 start = end;
        double acc = 0;
        for (int i = p.Count - 2; i >= 0; i--)
        {
            var q = MapWriter.ToVector(p[i]);
            acc += Vector3.Distance(q, start);
            start = q;
            if (acc > 60) break;
        }
        var d = end - start; d.Y = 0;
        return Vector3.Normalize(d);
    }

    private static Vector2 Away(RoadEntry r, bool atStart)
    {
        var p = r.Points;
        // über ~30 m mitteln, damit kurze Knicke nicht stören
        Vector2 a = atStart ? P(p[0]) : P(p[^1]);
        int i = atStart ? 1 : p.Count - 2;
        Vector2 b = a;
        while (i >= 0 && i < p.Count)
        {
            b = P(p[i]);
            if (Vector2.Distance(a, b) > 30) break;
            i += atStart ? 1 : -1;
        }
        var v = b - a;
        return v.LengthSquared() > 0 ? Vector2.Normalize(v) : v;
    }

    private static Vector2 P(double[] p) => new((float)p[0], (float)p[2]);

    // ------------------------------------------------------------------ Geometrie kürzen

    /// <summary>Kürzt eine Straße am Ende um mindestens <paramref name="dist"/> Meter.</summary>
    public static bool TrimEnd(RoadEntry r, float dist)
    {
        double acc = 0;
        int k = r.Points.Count - 1;
        while (k > 0 && acc < dist)
        {
            acc += Dist(r.Points[k - 1], r.Points[k]);
            k--;
        }
        if (acc < dist || k < 1) return false;
        r.Points = r.Points.Take(k + 1).ToList();
        r.Runs = r.Runs.Where(x => x.From < k).Select(x => { x.To = Math.Min(x.To, k); return x; }).ToList();
        return r.Points.Count >= 2;
    }

    /// <summary>Kürzt eine Straße am Anfang um mindestens <paramref name="dist"/> Meter.</summary>
    /// <summary>Wie TrimStart, aber genau bei dist geschnitten (Zwischenpunkt), nicht am nächsten
    /// Punkt dahinter – hinter dem Spurende einer Rampe blieben sonst bis 36 m Anschlussstück, und
    /// die Einfahrt danach lag so weit vom Spurende entfernt (A 9 z −2530).</summary>
    public static bool TrimStartExact(RoadEntry r, float dist)
    {
        double acc = 0;
        int k = 0;
        while (k < r.Points.Count - 1 && acc + Dist(r.Points[k], r.Points[k + 1]) < dist)
        {
            acc += Dist(r.Points[k], r.Points[k + 1]);
            k++;
        }
        if (k > r.Points.Count - 2) return false;
        double seg = Dist(r.Points[k], r.Points[k + 1]), rest = dist - acc;
        if (seg - rest < MapWriter.MinSegment + 0.5) return TrimStart(r, dist);
        var a = r.Points[k]; var b = r.Points[k + 1]; double t = rest / seg;
        var cut = new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t };
        r.Points = new List<double[]> { cut }.Concat(r.Points.Skip(k + 1)).ToList();
        r.Runs = r.Runs.Where(x => x.To > k).Select(x => { x.From = Math.Max(0, x.From - k); x.To -= k; return x; }).ToList();
        return r.Points.Count >= 2;
    }

    public static bool TrimStart(RoadEntry r, float dist)
    {
        double acc = 0;
        int k = 0;
        while (k < r.Points.Count - 1 && acc < dist)
        {
            acc += Dist(r.Points[k], r.Points[k + 1]);
            k++;
        }
        if (acc < dist || k > r.Points.Count - 2) return false;
        r.Points = r.Points.Skip(k).ToList();
        r.Runs = r.Runs.Where(x => x.To > k).Select(x => { x.From = Math.Max(0, x.From - k); x.To -= k; return x; }).ToList();
        return r.Points.Count >= 2;
    }

    /// <summary>Prefab-Richtung als Sehne zwischen Anker und dem OSM-Punkt am fernen Prefab-Ende –
    /// so liegen in Kurven beide Enden auf der Straße (aus: OSM2ETS_PREFAB_TANGENT).</summary>
    public static bool UseChord = Environment.GetEnvironmentVariable("OSM2ETS_PREFAB_TANGENT") is null;

    /// <summary>Seitlicher Versatz der ETS2-Linie je Lauf (aus der Zuordnung, siehe LaneCentering).</summary>
    public static Func<RunEntry, float> OffsetOf = _ => 0f;

    /// <summary>
    /// Lage der zweispurigen Prefab-Achse im Abstand dist vom Knoten: Straßenpunkt, ohne den
    /// Versatz des Straßenmodells, also auf der OSM-Linie.
    /// </summary>
    /// <summary>
    /// Prefab-Achse auf der geschriebenen Straßenlinie (OSM-Linie + Modellversatz des Laufs) statt
    /// auf der OSM-Linie. Sonst lagen die Prefab-Enden bis ~2 m näher an der Gegenfahrbahn als die
    /// anschließenden Straßen, und Prefabs beider Richtungen überschnitten sich (A 9 bei z −450).
    /// Aus: OSM2ETS_AXIS_OSM=1.
    /// </summary>
    public static bool AxisOnModelLine = Environment.GetEnvironmentVariable("OSM2ETS_AXIS_OSM") != "1";

    private static Vector3 AxisAt(RoadEntry r, bool fromStart, float dist, RunEntry? lateralRun = null)
    {
        // OSM-Lage vor dem Mittig-Setzen (sonst steckt der Modellversatz schon in den Punkten)
        var (srcPts, srcRuns) = LaneCentering.Original.TryGetValue(r, out var o) ? o : (r.Points, r.Runs);
        bool raw = !ReferenceEquals(srcPts, r.Points);
        var pts = fromStart ? srcPts : Enumerable.Reverse(srcPts).ToList();
        int n = pts.Count;
        double acc = 0;
        for (int i = 0; i < n - 1; i++)
        {
            double s = Math.Sqrt(Math.Pow(pts[i + 1][0] - pts[i][0], 2) + Math.Pow(pts[i + 1][2] - pts[i][2], 2));
            if ((acc + s >= dist && s > 0) || i == n - 2)
            {
                float t = s > 0 ? (float)Math.Clamp((dist - acc) / s, 0, 1) : 0;
                var a = MapWriter.ToVector(pts[i]); var b = MapWriter.ToVector(pts[i + 1]);
                var q = Vector3.Lerp(a, b, t);
                int seg = fromStart ? i : n - 2 - i;
                var run = srcRuns.FirstOrDefault(x => x.From <= seg && seg < x.To) ?? srcRuns[^1];
                var d = fromStart ? b - a : a - b; d.Y = 0;            // Fahrtrichtung
                if (d.LengthSquared() < 1e-6f) return q;
                d = Vector3.Normalize(d);
                var right = new Vector3(-d.Z, 0, d.X);
                // Nur auf die OSM-Linie zurück. Die durchgehenden Spuren lägen bei lanes=3 (Ver-
                // zögerungsspur) eigentlich 2,25 m weiter innen – mit den 4,5 m breiten ETS2-
                // Spuren überschnitten sich dann aber die Gegenfahrbahnen (A 70: 5 m statt 9 m).
                var lr = lateralRun ?? run;
                float lat = raw ? (AxisOnModelLine ? OffsetOf(lr) : 0) : -OffsetOf(run) + (lateralRun is null ? 0 : OffsetOf(lr));
                if (Environment.GetEnvironmentVariable("OSM2ETS_IPROBE") is not null)
                    Console.Error.WriteLine($"AXIS {r.Id} seg {seg} lanes {run.Lanes} off {OffsetOf(run)} q {q} lat {lat}");
                return q + right * lat;
            }
            acc += s;
        }
        return MapWriter.ToVector(pts[^1]);
    }

    private static Vector3 Chord(Vector3 from, Vector3 to, Vector3 fallback)
    {
        var d = to - from; d.Y = 0;
        return d.LengthSquared() > 1 ? Vector3.Normalize(d) : fallback;
    }

    private static Vector3 PointAlong(List<double[]> pts, float dist)
    {
        double acc = 0;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double s = Math.Sqrt(Math.Pow(pts[i + 1][0] - pts[i][0], 2) + Math.Pow(pts[i + 1][2] - pts[i][2], 2));
            if (acc + s >= dist && s > 0)
            {
                float t = (float)((dist - acc) / s);
                return Vector3.Lerp(MapWriter.ToVector(pts[i]), MapWriter.ToVector(pts[i + 1]), t);
            }
            acc += s;
        }
        return MapWriter.ToVector(pts[^1]);
    }

    /// <summary>Höhe der Straße im Abstand dist (Draufsicht) vom Ende bzw. Anfang.</summary>
    private static float HeightFromEnd(RoadEntry r, float dist) => HeightAlong(Enumerable.Reverse(r.Points).ToList(), dist);
    private static float HeightFromStart(RoadEntry r, float dist) => HeightAlong(r.Points, dist);

    private static float HeightAlong(List<double[]> pts, float dist)
    {
        double acc = 0;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double s = Math.Sqrt(Math.Pow(pts[i + 1][0] - pts[i][0], 2) + Math.Pow(pts[i + 1][2] - pts[i][2], 2));
            if (acc + s >= dist && s > 0)
            {
                double t = (dist - acc) / s;
                return (float)(pts[i][1] + t * (pts[i + 1][1] - pts[i][1]));
            }
            acc += s;
        }
        return (float)pts[^1][1];
    }

    private static double ArcLength(RoadEntry r)
    {
        double acc = 0;
        for (int i = 0; i < r.Points.Count - 1; i++) acc += Dist(r.Points[i], r.Points[i + 1]);
        return acc;
    }

    private static double Dist(double[] a, double[] b) =>
        Math.Sqrt(Math.Pow(b[0] - a[0], 2) + Math.Pow(b[1] - a[1], 2) + Math.Pow(b[2] - a[2], 2));

    // ------------------------------------------------------------------ Prefabs setzen

    /// <summary>Lage aller Prefab-Knoten bei Einsatz an der Stelle 0 ohne Drehung (empirisch über TruckLib).</summary>
    private static Vector3[] Calibrate(PrefabDescriptor ppd, Token unit, Quaternion rot)
    {
        var scratch = new Map();
        var pf = Prefab.Add(scratch, Vector3.Zero, unit, ppd, rot);
        return pf.Nodes.Select(n => n.Position).ToArray();
    }

    internal static Vector3[] CalibrateNodes(PrefabDescriptor ppd, Token unit, Quaternion rot) => Calibrate(ppd, unit, rot);
    internal static Vector3[] WorldDirectionsOf(PrefabDescriptor ppd, Vector3[] placed) => WorldDirections(ppd, placed);
    internal static double ArcLengthOf(RoadEntry r) => ArcLength(r);

    /// <summary>Gierdrehung, die den lokalen Vektor v (nach TruckLib-Einsatz) auf d dreht.</summary>
    internal static Quaternion YawTo(PrefabDescriptor ppd, Token unit, Vector3 v, Vector3 d)
    {
        v.Y = 0; d.Y = 0; v = Vector3.Normalize(v); d = Vector3.Normalize(d);
        float av = MathF.Atan2(v.X, v.Z), ad = MathF.Atan2(d.X, d.Z);
        var local = Calibrate(ppd, unit, Quaternion.Identity);
        var a = local[1] - local[0]; a.Y = 0;
        foreach (var sign in new[] { 1f, -1f })
        {
            var q = Quaternion.CreateFromYawPitchRoll(sign * (ad - av), 0, 0);
            // Prüfung am echten Einsatz: Knotenverbindung 0→1 muss sich gleich drehen wie v
            var placed = Calibrate(ppd, unit, q);
            var b = placed[1] - placed[0]; b.Y = 0;
            float turnPlaced = MathF.Atan2(b.X, b.Z) - MathF.Atan2(a.X, a.Z);
            float want = ad - av;
            if (MathF.Abs(MathF.IEEERemainder(turnPlaced - want, 2 * MathF.PI)) < 0.01f) return q;
        }
        throw new InvalidOperationException("Prefab-Drehung nicht bestimmbar");
    }

    /// <summary>Drehung, die die lokale Fahrtrichtung (von Knoten a nach b) auf d dreht.</summary>
    private static Quaternion Orientation(PrefabDescriptor ppd, Token unit, ushort a, ushort b, Vector3 d)
    {
        var local = Calibrate(ppd, unit, Quaternion.Identity);
        var v = local[b] - local[a]; v.Y = 0; v = Vector3.Normalize(v);
        float av = MathF.Atan2(v.X, v.Z), ad = MathF.Atan2(d.X, d.Z);
        foreach (var sign in new[] { 1f, -1f })
        {
            var q = Quaternion.CreateFromYawPitchRoll(sign * (ad - av), 0, 0);
            var t = Vector3.Transform(v, q);
            if (Vector3.Dot(t, d) > 0.999f) return q;
        }
        throw new InvalidOperationException("Prefab-Drehung nicht bestimmbar");
    }

    /// <summary>Setzt ein Prefab so, dass Knoten <paramref name="anchorNode"/> auf <paramref name="target"/> liegt.</summary>
    private static Prefab Place(Map map, PpdSource src, PrefabDef def, ushort fromNode, ushort toNode,
                                Vector3 dir, ushort anchorNode, Vector3 target, float grade = 0)
    {
        var ppd = src.Get(def.PpdPath);
        var rot = Orientation(ppd, def.Unit, fromNode, toNode, dir);
        // Ausrichtung nach der Knotenrichtung, nicht nach der Verbindungslinie der Knoten:
        // bei Prefabs mit seitlichem Versatz (Aufweitung/Teilung, 4,5 m auf 40–59 m) wäre das
        // Prefab sonst um 4–6° verdreht und seine Enden lägen bis zu 3 m neben der Straße.
        if (Environment.GetEnvironmentVariable("OSM2ETS_PREFAB_CHORD") is null)
        {
            var d0 = Vector3.Normalize(new Vector3(dir.X, 0, dir.Z));
            float Err(Quaternion q)
            {
                var w = WorldDirections(ppd, Calibrate(ppd, def.Unit, q))[fromNode];
                // Knotenrichtung zeigt ins Prefab; am Eingang (fromNode) = Fahrtrichtung
                if (Vector3.Dot(w, d0) < 0) w = -w;
                return MathF.Atan2(w.X * d0.Z - w.Z * d0.X, Vector3.Dot(w, d0));
            }
            for (int it = 0; it < 3; it++)
            {
                float e = Err(rot);
                if (MathF.Abs(e) < 1e-4f) break;
                var qa = Quaternion.Normalize(Quaternion.Concatenate(rot, Quaternion.CreateFromAxisAngle(Vector3.UnitY, e)));
                var qb = Quaternion.Normalize(Quaternion.Concatenate(rot, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -e)));
                rot = MathF.Abs(Err(qa)) < MathF.Abs(Err(qb)) ? qa : qb;
            }
        }
        if (MathF.Abs(grade) > 1e-4f)
        {
            // Neigung um die waagerechte Querachse; Vorzeichen am echten Einsatz prüfen
            var axis = Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitY));
            float ang = MathF.Atan(grade);
            foreach (var sgn in new[] { 1f, -1f })
            {
                var q = Quaternion.Normalize(Quaternion.Concatenate(rot, Quaternion.CreateFromAxisAngle(axis, sgn * ang)));
                var pl = Calibrate(ppd, def.Unit, q);
                float rise = pl[toNode].Y - pl[fromNode].Y;
                var run = pl[toNode] - pl[fromNode]; run.Y = 0;
                if (MathF.Abs(rise / run.Length() - grade) < 0.2f * MathF.Abs(grade) + 1e-3f) { rot = q; break; }
            }
        }
        var rotated = Calibrate(ppd, def.Unit, rot);
        var pos = target - rotated[anchorNode] + rotated[0];
        var pf = Prefab.Add(map, pos, def.Unit, ppd, rot);
        pf.Variant = def.Variant;
        pf.Look = def.Look;
        pf.ViewDistance = 1400; // TruckLib KdopItem.ViewDistanceExtremelyFar
        InwardDirections[pf] = WorldDirections(ppd, rotated);
        PrefabFootprint.Register(pf, ppd);
        return pf;
    }

    /// <summary>Je gesetztem Prefab: Richtung jedes Knotens in das Prefab hinein (Welt, x/z).</summary>
    public static readonly Dictionary<Prefab, Vector3[]> InwardDirections = [];

    /// <summary>
    /// Überträgt die Knotenrichtungen der .ppd in Weltkoordinaten. Die Drehung wird aus
    /// den Knotenlagen (ppd gegenüber gesetztem Prefab) bestimmt – so hängt das Ergebnis
    /// nicht von TruckLibs interner Grunddrehung ab.
    /// </summary>
    private static Vector3[] WorldDirections(PrefabDescriptor ppd, Vector3[] placedLocal)
    {
        var a = new Vector2(ppd.Nodes[1].Position.X - ppd.Nodes[0].Position.X, ppd.Nodes[1].Position.Z - ppd.Nodes[0].Position.Z);
        var b = new Vector2(placedLocal[1].X - placedLocal[0].X, placedLocal[1].Z - placedLocal[0].Z);
        float ang = MathF.Atan2(b.Y, b.X) - MathF.Atan2(a.Y, a.X);
        float c = MathF.Cos(ang), s = MathF.Sin(ang);
        return ppd.Nodes.Select(n =>
        {
            var d = new Vector2(n.Direction.X, n.Direction.Z);
            var w = new Vector2(c * d.X - s * d.Y, s * d.X + c * d.Y);
            return Vector3.Normalize(new Vector3(w.X, 0, w.Y));
        }).ToArray();
    }

    /// <summary>Länge eines Prefabs entlang der Fahrtrichtung.</summary>
    public static float Length(PpdSource src, PrefabDef def, ushort a, ushort b)
    {
        var local = Calibrate(src.Get(def.PpdPath), def.Unit, Quaternion.Identity);
        var v = local[b] - local[a]; v.Y = 0;
        return v.Length();
    }

    /// <summary>Mindestabstand der Achsen gegenüberliegender Anschluss-Prefabs (m).
    /// Gemessen an den .ppd-Spuren (ETS2 1.61): die Zusatzspur liegt außen, der innere Rand
    /// bleibt 4,5 m neben der Achse der zweispurigen Fahrbahn – zusammen 9 m (Berührung).</summary>
    public const float OppositeMinGap = 9f;

    /// <summary>
    /// Ausfahrt und Einfahrt der Gegenrichtung liegen oft auf gleicher Höhe nebeneinander;
    /// ihre aufgeweiteten Prefabs überlappen dann. Beide Anker seitlich auseinanderrücken.
    /// </summary>
    public static int SeparateOpposite(List<Plan> plans)
    {
        int n = 0;
        for (int i = 0; i < plans.Count; i++)
        for (int j = i + 1; j < plans.Count; j++)
        {
            var a = plans[i]; var b = plans[j];
            if (a.Kind == "drop" || b.Kind == "drop") continue;
            var da = new Vector2(a.Direction.X, a.Direction.Z); var db = new Vector2(b.Direction.X, b.Direction.Z);
            if (da.LengthSquared() < 1e-6f || db.LengthSquared() < 1e-6f) continue;
            da = Vector2.Normalize(da); db = Vector2.Normalize(db);
            if (Vector2.Dot(da, db) > -0.8f) continue;
            var d = new Vector2(b.Anchor.X - a.Anchor.X, b.Anchor.Z - a.Anchor.Z);
            if (d.Length() > 160) continue;
            var nrm = new Vector2(-da.Y, da.X);
            float lat = Vector2.Dot(d, nrm);
            float need = OppositeMinGap - MathF.Abs(lat);
            if (need <= 0) continue;
            float sgn = lat >= 0 ? 1 : -1;
            var shift = nrm * (sgn * need / 2);
            b.Anchor += new Vector3(shift.X, 0, shift.Y);
            a.Anchor -= new Vector3(shift.X, 0, shift.Y);
            n++;
        }
        return n;
    }

    private static void DropInside(RoadEntry ramp, bool atStart, RoadEntry main, Vector3 node)
    {
        var mp = main.Points.Select(P).ToList();
        float Dist(Vector2 q)
        {
            float best = float.MaxValue;
            for (int i = 0; i < mp.Count - 1; i++)
            {
                var ab = mp[i + 1] - mp[i]; float l2 = ab.LengthSquared();
                float t = l2 > 0 ? Math.Clamp(Vector2.Dot(q - mp[i], ab) / l2, 0, 1) : 0;
                best = MathF.Min(best, Vector2.Distance(mp[i] + ab * t, q));
            }
            return best;
        }
        float need = Dist(new Vector2(node.X, node.Z)) + 1f;
        var pts = ramp.Points;
        int drop = 0;
        while (pts.Count - drop > 2)
        {
            var q = P(atStart ? pts[drop] : pts[^(1 + drop)]);
            if (Dist(q) >= need) break;
            drop++;
        }
        if (drop == 0) return;
        if (atStart)
        {
            pts.RemoveRange(0, drop);
            // Läufe, die ganz in den entfernten Punkten liegen, fallen weg (früher blieben sie als
            // Abschnitt 0–1 stehen und überlappten den nächsten Lauf)
            ramp.Runs = ramp.Runs.Where(x => x.To - drop > 0).Select(x => { x.From = Math.Max(0, x.From - drop); x.To -= drop; return x; }).ToList();
            if (ramp.Runs.Count > 0) ramp.Runs[0].From = 0;
        }
        else
        {
            pts.RemoveRange(pts.Count - drop, drop);
            int last = pts.Count - 1;
            ramp.Runs = ramp.Runs.Where(x => x.From < last).Select(x => { x.To = Math.Min(x.To, last); return x; }).ToList();
            if (ramp.Runs.Count > 0) ramp.Runs[^1].To = last;
        }
        Dropped += drop;
    }
    public static int Dropped;

    private static Vector3 Flat(Vector3 v, Vector3 fallback)
    {
        v.Y = 0;
        if (v.LengthSquared() < 1e-6f) return fallback;
        v = Vector3.Normalize(v);
        return Vector3.Dot(v, fallback) < 0 ? -v : v;
    }

    public sealed record Placed(Plan Plan, Prefab First, ushort FirstIn, Prefab Last, ushort LastOut, Prefab RampPrefab, ushort RampNode)
    {
        /// <summary>Bei Zusatzspur-Straße: deren Anfangs- und Endknoten an den beiden Prefabs.</summary>
        public Prefab? ExtFrom; public ushort ExtFromNode;
        public Prefab? ExtTo; public ushort ExtToNode;
    }

    /// <summary>Kürzt die Straßen für einen Plan; false = Plan nicht umsetzbar (Straßen zu kurz).</summary>
    public static bool Prepare(Plan p, PpdSource src)
    {
        // erst prüfen, dann kürzen – sonst bliebe bei Abbruch eine Lücke
        // Alle Abstände (lenNear, d, far …) zählen entlang der Hauptfahrbahn ab dem OSM-Knoten; d ist
        // die Verschiebung des inneren Prefabs auf die andere Seite des Knotens (meist 0).
        if (p.Kind == "drop")
        {
            float len = Length(src, p.K.Narrow!, NarrowIn, NarrowOut);
            if (ArcLength(p.MainOut) < len + Slack + 20 || ArcLength(p.MainIn) < Slack + 20) return false;
            p.Grade = (HeightFromStart(p.MainOut, len) - p.Anchor.Y) / len;
            if (UseChord) p.Direction = Chord(p.Anchor, AxisAt(p.MainOut, true, len), p.Direction);
            return TrimStartExact(p.MainOut, len + Slack);
        }
        if (p.Kind == "exit")
        {
            if (UseParallelSplit) MoveSplitDownstream(p, src);
            float lenNear = Length(src, p.K.Split!, SplitIn, SplitMain), lenFar = p.K.Widen is null ? 0f : Length(src, p.K.Widen, WidenIn, WidenOut);
            if (p.K.Widen0 is not null) lenFar += Length(src, p.K.Widen0, WidenIn, WidenOut);
            float len = lenNear + lenFar;
            if (SkipAtStructures && HasStructure(Enumerable.Reverse(p.MainIn.Points).ToList(), p.MainIn.Runs, true, len + Slack)) { p.Blocked = true; return false; }
            // Bauwerke stromauf vom Knoten (dort liegen die Prefabs) und stromab (Platz zum Verschieben)
            var spans = StructureSpans(Enumerable.Reverse(p.MainIn.Points).ToList(), p.MainIn.Runs, true, len + Slack + MaxExt + MaxShift);
            var behind = StructureSpans(p.MainOut.Points, p.MainOut.Runs, false, MaxShift + ExtMargin + Slack);
            float d = ShiftFor(spans, behind, lenNear, lenFar, (float)ArcLength(p.MainIn), (float)ArcLength(p.MainOut),
                               (float)ArcLength(p.Ramp), out float ext);
            p.Shift = d;
            if (ext == 0 && d == 0 && p.K.Widen is not null && p.K.Widen0 is null)
            {
                ext = CurveExt(Enumerable.Reverse(p.MainIn.Points).ToList(), p.Anchor, lenNear + lenFar);
                p.ExtForCurve = ext > 0;
            }
            p.Ext = ext;
            // drei Prefabs: ohne Zusatzspur-Straße und Verschiebung (sonst nicht umsetzbar)
            if ((p.K.Widen0 is not null || p.K.Widen is null) && (d > 0 || ext > 0)) return false;
            float far = len + p.Ext - d;      // so weit reichen die Prefabs stromauf
            if (ArcLength(p.MainIn) < far + Slack + 20 || ArcLength(p.Ramp) < RampTrim + d + 20) return false;
            // Teilung stromab gerückt: ihr Hauptausgang liegt d hinter dem Knoten auf der OSM-Linie
            if (d > 0) p.Anchor = AxisAt(p.MainOut, true, d);
            if (p.Ext > 0)
            {
                p.Grade = (p.Anchor.Y - HeightFromEnd(p.MainIn, lenNear - d)) / lenNear;
                if (UseChord) p.Direction = Chord(AxisAt(p.MainIn, false, lenNear - d), p.Anchor, p.Direction);
                // äußeres Prefab mit seinem Außenknoten (zweispurig) auf der Achse – der dreispurige
                // Innenknoten liegt wie bei direkt verbundenen Prefabs seitlich versetzt
                p.FarAnchor = AxisAt(p.MainIn, false, far);
                p.FarDirection = Chord(p.FarAnchor, AxisAt(p.MainIn, false, lenNear + p.Ext - d), p.Direction);
                p.FarGrade = (HeightFromEnd(p.MainIn, lenNear + p.Ext - d) - p.FarAnchor.Y) / lenFar;
                p.ExtRoad = ExtRoad(p, p.MainIn, false, lenNear + p.Ext - d, lenNear - d, spans);
            }
            else
            {
                p.Grade = (p.Anchor.Y - HeightFromEnd(p.MainIn, len)) / len;
                if (UseChord) p.Direction = Chord(AxisAt(p.MainIn, false, len), p.Anchor, p.Direction);
            }
            if (UseBalance && (p.Ext > 0 || BalanceAll)) Balance(p, src);
            return TrimEnd(p.MainIn, far + Slack) && TrimStart(p.Ramp, RampTrim + d) && (d == 0 || TrimStart(p.MainOut, d));
        }
        else
        {
            if (UseParallelMerge) MoveMergeUpstream(p, src);
            float lenNear = Length(src, p.K.Join!, JoinMain, JoinOut), lenFar = p.K.Narrow is null ? 0f : Length(src, p.K.Narrow, NarrowIn, NarrowOut);
            float len = lenNear + lenFar;
            if (SkipAtStructures && HasStructure(p.MainOut.Points, p.MainOut.Runs, false, len + Slack)) { p.Blocked = true; return false; }
            // Bauwerke stromab vom Knoten (dort liegen die Prefabs) und stromauf (Platz zum Verschieben)
            var spans = StructureSpans(p.MainOut.Points, p.MainOut.Runs, false, len + Slack + MaxExt + MaxShift);
            var behind = StructureSpans(Enumerable.Reverse(p.MainIn.Points).ToList(), p.MainIn.Runs, true, MaxShift + ExtMargin + Slack);
            float d = ShiftFor(spans, behind, lenNear, lenFar, (float)ArcLength(p.MainOut), (float)ArcLength(p.MainIn),
                               (float)ArcLength(p.Ramp), out float ext);
            p.Shift = d;
            if (ext == 0 && d == 0 && p.K.Narrow is not null)
            {
                ext = CurveExt(p.MainOut.Points, p.Anchor, lenNear + lenFar);
                p.ExtForCurve = ext > 0;
            }
            // Einmündung stromauf gerückt: die Verengung bleibt, wo OSM die Fahrbahn wieder schmaler
            // macht – dazwischen Zusatzspur-Straße auf der OSM-Linie des breiteren Abschnitts (sonst
            // lag die schmale Fahrbahn mit dem Versatz der breiten neben der Gegenfahrbahn, A 9 z −2660)
            if (p.Moved > 0 && p.AfterRampPrefab && p.K.Narrow is not null && d == 0 && ext < p.Moved)
            {
                // nicht bis an das nächste Prefab/Ende der Fahrbahn danach (A 9 bei z −500: Zusatzspur
                // über zwei Einmündungen)
                float room = (float)ArcLength(p.MainOut) - len - Slack - 60;
                float e2 = MathF.Min(MathF.Min(p.Moved, MaxExt), room);
                if (e2 > ext) { ext = e2; p.ExtForCurve = false; }
            }
            p.Ext = ext;
            if (p.K.Narrow is null && (d > 0 || ext > 0)) return false;
            float far = len + p.Ext - d;      // so weit reichen die Prefabs stromab
            if (ArcLength(p.MainOut) < far + Slack + 20 || ArcLength(p.Ramp) < RampTrim + d + 20) return false;
            // Zusammenführung stromauf gerückt: ihr Haupteingang liegt d vor dem Knoten auf der OSM-Linie
            if (d > 0) p.Anchor = AxisAt(p.MainIn, false, d);
            if (p.Ext > 0)
            {
                p.Grade = (HeightFromStart(p.MainOut, lenNear - d) - p.Anchor.Y) / lenNear;
                if (UseChord) p.Direction = Chord(p.Anchor, AxisAt(p.MainOut, true, lenNear - d), p.Direction);
                p.FarAnchor = AxisAt(p.MainOut, true, far);
                p.FarDirection = Chord(AxisAt(p.MainOut, true, lenNear + p.Ext - d), p.FarAnchor, p.Direction);
                p.FarGrade = (p.FarAnchor.Y - HeightFromStart(p.MainOut, lenNear + p.Ext - d)) / lenFar;
                p.ExtRoad = ExtRoad(p, p.MainOut, true, lenNear - d, lenNear + p.Ext - d, spans);
            }
            else
            {
                p.Grade = (HeightFromStart(p.MainOut, len) - p.Anchor.Y) / len;
                if (UseChord) p.Direction = Chord(p.Anchor, AxisAt(p.MainOut, true, len), p.Direction);
            }
            return TrimStart(p.MainOut, far + Slack) && TrimEnd(p.Ramp, RampTrim + d) && (d == 0 || TrimEnd(p.MainIn, d));
        }
    }

    /// <summary>Einmündungen dort, wo die Rampe wirklich an die Hauptfahrbahn kommt
    /// (aus: OSM2ETS_NO_PARALLEL_MERGE=1).</summary>
    public static bool UseParallelMerge = Environment.GetEnvironmentVariable("OSM2ETS_NO_PARALLEL_MERGE") != "1";
    public const float MaxParallelShift = 200f;
    public static int ParallelMoved;

    /// <summary>
    /// In OSM laufen Rampe und Hauptfahrbahn oft als getrennte Wege nebeneinander her, solange eine
    /// durchgezogene Linie sie trennt; der gemeinsame Knoten liegt erst am Ende. Das Prefab braucht
    /// die Rampe aber in seinem seitlichen Abstand (Rampenknoten, bei ch_4200c rund 15 m). Der
    /// Einmündungspunkt wird deshalb stromauf an die Stelle gerückt, an der die Rampe diesen Abstand
    /// hat: Hauptfahrbahn dort geteilt, ihr Rest bis zum OSM-Knoten wird Anfang der Fahrbahn danach.
    /// Die Rampenpunkte daneben entfernt später DropInside.
    /// </summary>
    private static void MoveMergeUpstream(Plan p, PpdSource src)
    {
        var ppd = src.Get(p.K.Join!.PpdPath);
        var loc = Calibrate(ppd, p.K.Join.Unit, Quaternion.Identity);
        var fwd = loc[JoinOut] - loc[JoinMain]; fwd.Y = 0; fwd = Vector3.Normalize(fwd);
        var rv = loc[JoinRamp] - loc[JoinMain]; rv.Y = 0;
        float latS = fwd.X * rv.Z - fwd.Z * rv.X;
        float lat = MathF.Abs(latS), lon = Vector3.Dot(rv, fwd);
        float total = (float)ArcLength(p.MainIn);
        float found = -1;
        for (float D = 2; D <= MathF.Min(MaxParallelShift, total - 60); D += 2)
        {
            var q = AxisAt(p.MainIn, false, D);
            if (Environment.GetEnvironmentVariable("OSM2ETS_IPROBE") is not null && D % 10 == 0) Console.Error.WriteLine($"PAR {p.OsmNode} D {D} dist {RampDistance(p.Ramp, new Vector2(q.X, q.Z)):0.0} lat {lat:0.0} lon {lon:0.0} total {total:0}");
            if (RampDistance(p.Ramp, new Vector2(q.X, q.Z)) >= lat) { found = D; break; }
        }
        // Wie bei Ausfahrten: lieber die Einfahrt verschieben als die Rampe biegen – weiter stromauf,
        // wo die OSM-Rampe am Rampenknoten mindestens so steil auf die Hauptfahrbahn zuläuft wie der
        // Rampenarm; sonst die Stelle mit dem kleinsten Bogen
        if (found >= 0 && UseBendFreeExit && p.Ramp.Start?.Kind != "prefab")
        {
            var dirs = WorldDirections(ppd, loc);
            var arm = -dirs[JoinRamp]; arm.Y = 0;
            if (arm.LengthSquared() > 1e-6f)
            {
                arm = Vector3.Normalize(arm);
                float armDiv = MathF.Acos(Math.Clamp(Vector3.Dot(arm, fwd), -1, 1)) * 180 / MathF.PI;
                if (armDiv > 90) armDiv = 180 - armDiv;
                float smooth = -1, bestCost = float.MaxValue, bestD = -1;
                for (float D = found; D <= MathF.Min(MaxParallelShift, total - 60); D += 2)
                {
                    var q0 = AxisAt(p.MainIn, false, D + 2); var q1 = AxisAt(p.MainIn, false, MathF.Max(0, D - 2));
                    var md = new Vector2(q1.X - q0.X, q1.Z - q0.Z);
                    if (md.LengthSquared() < 1e-6f) continue;
                    md = Vector2.Normalize(md);
                    var right = new Vector2(-md.Y, md.X);
                    var q = AxisAt(p.MainIn, false, D);
                    var node = new Vector2(q.X, q.Z) + right * latS + md * lon;
                    if (RampArcAt(p.Ramp, node) < MinRampBefore) break;
                    float dist = RampDistance(p.Ramp, node);
                    var rt = RampTangent(p.Ramp, node);
                    float div = MathF.Acos(Math.Clamp(MathF.Abs(Vector2.Dot(rt, md)), 0, 1)) * 180 / MathF.PI;
                    if (dist <= BendFreeTol && div >= armDiv - BendFreeAngle) { smooth = D; break; }
                    float cost = MathF.Max(0, armDiv - div) + 2f * MathF.Max(0, dist - BendFreeTol);
                    if (dist < 10f && cost < bestCost) { bestCost = cost; bestD = D; }
                }
                if (smooth < 0) smooth = bestD;
                if (Environment.GetEnvironmentVariable("OSM2ETS_IPROBE") is not null)
                    Console.Error.WriteLine($"MERGETRY {p.OsmNode} arm {armDiv:0.0} found {found} smooth {smooth}");
                if (smooth >= 0) found = smooth;
            }
        }
        float d = found - lon - 2;     // Prefab-Hauptknoten so, dass der Rampenknoten dort liegt
        if (found < 0 || d < 20) return;
        // vor dem Rampenknoten muss genug Rampe bleiben (sonst z. B. Spurende-Prefab und Einfahrt
        // fast aneinander, die Rampe dazwischen geknickt)
        var qf = AxisAt(p.MainIn, false, found);
        if (Environment.GetEnvironmentVariable("OSM2ETS_IPROBE") is not null) Console.Error.WriteLine($"PARF {p.OsmNode} found {found} d {d} arc {RampArcAt(p.Ramp, new Vector2(qf.X, qf.Z)):0} start {p.Ramp.Start?.Kind} ramp {p.Ramp.Id}");
        float needArc = p.Ramp.Start?.Kind == "prefab" ? MinRampBefore * 3 : MinRampBefore;
        if (RampArcAt(p.Ramp, new Vector2(qf.X, qf.Z)) < needArc)
        {
            // Rampe beginnt kurz davor an einem Prefab (Spurende auf der Rampe): dann nur so weit
            // rücken, wie vor dem Rampenknoten MinRampBefore bleiben und die Rampe mindestens den
            // halben Prefab-Abstand hat – der Rest des Abstands wird ein flacher Bogen der Rampe.
            // In OSM ist die Rampe getrennt, solange eine Sperrfläche sie trennt; das Prefab gehört
            // davor, nicht an den OSM-Knoten (sonst liegt die Rampe auf der Autobahn, A 9 z −2530).
            float best = -1;
            if (p.Ramp.Start?.Kind == "prefab")
            {
                // direkt hinter dem Spurende-Prefab der Rampe beginnt die Einfädelung (Sperrfläche
                // in OSM bis zum Knoten): so weit stromauf wie möglich, nur ein kurzes Rampenstück
                // (PrefabGap) zwischen den Prefabs
                for (float D = found + 120; D >= 20 + lon + 2; D -= 2)
                {
                    if (D > total - 60) continue;
                    // geschätzte Lage des Rampenknotens: lon vor und lat neben der Achse, auf der Seite der Rampe
                    var q2 = AxisAt(p.MainIn, false, D);
                    var q3 = AxisAt(p.MainIn, false, MathF.Max(D - 5, 0));
                    var dv = new Vector2(q3.X - q2.X, q3.Z - q2.Z);
                    if (dv.LengthSquared() < 1e-6f) continue;
                    dv = Vector2.Normalize(dv);
                    var rs = P(p.Ramp.Points[0]) - new Vector2(q2.X, q2.Z);
                    var nrm = new Vector2(-dv.Y, dv.X);
                    if (Vector2.Dot(nrm, rs) < 0) nrm = -nrm;
                    var node = new Vector2(q2.X, q2.Z) + dv * lon + nrm * lat;
                    if (RampArcAt(p.Ramp, node) < PrefabGap) continue;
                    // kein Knick zwischen Spurende und Einfahrt: die Verbindung vom Ende des
                    // Spurende-Prefabs zum Rampenknoten darf von dessen Richtung und von der Richtung
                    // der Einfahrt (parallel zur Achse) nur wenig abweichen – sonst lieber etwas
                    // weiter stromab (flacher S-Bogen statt V)
                    var r0 = P(p.Ramp.Points[0]);
                    var t0 = P(p.Ramp.Points[1]) - r0;
                    var ch = node - r0;
                    if (t0.LengthSquared() < 1e-6f || ch.LengthSquared() < 1e-6f) continue;
                    t0 = Vector2.Normalize(t0); var chn = Vector2.Normalize(ch); var tn = dv;   // Fahrtrichtung (dv zeigt stromab)
                    float a0 = MathF.Acos(Math.Clamp(Vector2.Dot(t0, chn), -1, 1)) * 180 / MathF.PI;
                    float a1 = MathF.Acos(Math.Clamp(Vector2.Dot(chn, tn), -1, 1)) * 180 / MathF.PI;
                    if (a0 + a1 > JoinTurnPerM * ch.Length()) continue;
                    best = D; break;
                }
            }
            else
            for (float D = found; D >= 20 + lon + 2; D -= 2)
            {
                var q2 = AxisAt(p.MainIn, false, D);
                var v2 = new Vector2(q2.X, q2.Z);
                if (RampDistance(p.Ramp, v2) < lat * ShortRampLat) break;
                if (RampArcAt(p.Ramp, v2) >= MinRampBefore) { best = D; break; }
            }
            if (best < 0) return;
            p.AfterRampPrefab = p.Ramp.Start?.Kind == "prefab";
            found = best; d = found - lon - 2;
            qf = AxisAt(p.MainIn, false, found);
        }
        // Hauptfahrbahn bei d teilen
        var pts = p.MainIn.Points; double acc = 0; int k = pts.Count - 1;
        while (k > 0 && acc + Dist(pts[k - 1], pts[k]) < d) { acc += Dist(pts[k - 1], pts[k]); k--; }
        if (k < 1) return;
        float rest = (float)(d - acc), seg = (float)Dist(pts[k - 1], pts[k]);
        var a = pts[k - 1]; var b = pts[k];
        double t = seg > 0 ? 1 - rest / seg : 0;
        var cut = new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t };
        var tail = new List<double[]> { cut };
        tail.AddRange(pts.Skip(k));                       // bis zum OSM-Knoten (= Anfang MainOut)
        p.MainIn.Points = pts.Take(k).Append(cut).ToList();
        int last = p.MainIn.Points.Count - 1;
        p.MainIn.Runs = p.MainIn.Runs.Where(x => x.From < last).Select(x => { x.To = Math.Min(x.To, last); return x; }).ToList();
        p.MainIn.Runs[^1].To = last;
        int add = tail.Count - 1;
        var outPts = tail.Concat(p.MainOut.Points.Skip(1)).ToList();
        foreach (var x in p.MainOut.Runs) { x.From += add; x.To += add; }
        p.MainOut.Runs[0].From = 0;
        p.MainOut.Points = outPts;
        // gespeicherte OSM-Lage genauso teilen. Verworfen rechnete AxisAt aus den mittig gesetzten
        // Punkten zurück – die laufen am alten OSM-Knoten (Anschlussstelle) auf die OSM-Linie aus,
        // und die Zusatzspur-Straße darüber bekam eine S-Kurve von ±1,5 m (A 9 z −2600)
        if (LaneCentering.Original.TryGetValue(p.MainIn, out var oIn) && LaneCentering.Original.TryGetValue(p.MainOut, out var oOut)
            && oIn.Points.Count == pts.Count && oOut.Points.Count == outPts.Count - add)
        {
            var oa = oIn.Points[k - 1]; var ob = oIn.Points[k];
            var oc = new[] { oa[0] + (ob[0] - oa[0]) * t, oa[1] + (ob[1] - oa[1]) * t, oa[2] + (ob[2] - oa[2]) * t };
            var oInPts = oIn.Points.Take(k).Append(oc).ToList();
            var oOutPts = new List<double[]> { oc }; oOutPts.AddRange(oIn.Points.Skip(k)); oOutPts.AddRange(oOut.Points.Skip(1));
            LaneCentering.Original[p.MainIn] = (oInPts, p.MainIn.Runs.Select(x => x.Span(x.From, x.To)).ToList());
            LaneCentering.Original[p.MainOut] = (oOutPts, p.MainOut.Runs.Select(x => x.Span(x.From, x.To)).ToList());
        }
        else
        {
            LaneCentering.Original.Remove(p.MainIn);
            LaneCentering.Original.Remove(p.MainOut);
        }
        p.Anchor = MapWriter.ToVector(cut);
        p.Direction = Direction(p.MainIn, p.MainOut);
        p.Moved = d;
        ParallelMoved++;
    }

    /// <summary>Teilung so drehen, dass ankommende Fahrbahn, weiterführende Fahrbahn und Rampe
    /// gleich viel von ihrer OSM-Richtung abweichen (aus: OSM2ETS_NO_BALANCE=1).</summary>
    public static bool UseBalance = Environment.GetEnvironmentVariable("OSM2ETS_NO_BALANCE") != "1";
    public const float MaxBalanceDeg = 5f;
    public static bool BalanceAll = Environment.GetEnvironmentVariable("OSM2ETS_BALANCE_ALL") == "1";
    public const float BalanceReach = 40f;     // Richtung der Straßen über so viele Meter ab dem Prefab
    public static int Balanced;

    private static float Ang(Vector3 v) => MathF.Atan2(v.Z, v.X);
    private static float Wrap(float a) { while (a > MathF.PI) a -= 2 * MathF.PI; while (a < -MathF.PI) a += 2 * MathF.PI; return a; }

    /// <summary>
    /// Die Teilung lag nach der Sehne der ankommenden Fahrbahn; zweigt die OSM-Rampe flacher oder
    /// steiler ab als die Prefab-Rampe, musste allein die Rampe einen starken Bogen machen (Dreieck,
    /// ch_42007). Gedreht wird um den Hauptausgang (OSM-Knoten), um das Mittel der Richtungsfehler
    /// von ankommender Fahrbahn (0, sie bestimmt die Sehne), weiterführender Fahrbahn und Rampe,
    /// höchstens <see cref="MaxBalanceDeg"/>.
    /// </summary>
    private static void Balance(Plan p, PpdSource src)
    {
        var ppd = src.Get(p.K.Split!.PpdPath);
        var loc = Calibrate(ppd, p.K.Split.Unit, Quaternion.Identity);
        var inward = WorldDirections(ppd, loc);
        var axis = loc[SplitMain] - loc[SplitIn];
        float fMain = Wrap(Ang(-inward[SplitMain]) - Ang(axis)), fRamp = Wrap(Ang(-inward[SplitRamp]) - Ang(axis));
        var mo = AxisAt(p.MainOut, true, BalanceReach) - p.Anchor;
        // ab der Stelle der Rampe neben dem Hauptausgang (die Rampe beginnt nach einer verschobenen
        // Teilung weit stromauf)
        var rp = PointAlongRamp(p.Ramp, RampArcAt(p.Ramp, new Vector2(p.Anchor.X, p.Anchor.Z)) + BalanceReach) - p.Anchor;
        if (mo.LengthSquared() < 1 || rp.LengthSquared() < 1) return;
        float th = Ang(p.Direction);
        float eM = Wrap(Ang(mo) - (th + fMain)), eR = Wrap(Ang(rp) - (th + fRamp));
        float delta = Math.Clamp((0 + eM + eR) / 3, -MaxBalanceDeg * MathF.PI / 180, MaxBalanceDeg * MathF.PI / 180);
        if (MathF.Abs(delta) < 0.2f * MathF.PI / 180) return;
        float c = MathF.Cos(delta), sn = MathF.Sin(delta);
        var d = p.Direction;
        p.Direction = Vector3.Normalize(new Vector3(d.X * c - d.Z * sn, d.Y, d.X * sn + d.Z * c));
        Balanced++;
        if (Environment.GetEnvironmentVariable("OSM2ETS_IPROBE") is not null)
            Console.Error.WriteLine($"BALANCE {p.OsmNode} {p.K.Name} eM {eM * 180 / MathF.PI:0.0} eR {eR * 180 / MathF.PI:0.0} -> {delta * 180 / MathF.PI:0.0}°");
    }

    /// <summary>Punkt der Rampe in dist Metern Bogenlänge ab ihrem Anfang.</summary>
    private static Vector3 PointAlongRamp(RoadEntry r, float dist)
    {
        double acc = 0;
        for (int i = 0; i < r.Points.Count - 1; i++)
        {
            double s = Dist(r.Points[i], r.Points[i + 1]);
            if (acc + s >= dist) return Vector3.Lerp(MapWriter.ToVector(r.Points[i]), MapWriter.ToVector(r.Points[i + 1]), (float)((dist - acc) / Math.Max(s, 1e-6)));
            acc += s;
        }
        return MapWriter.ToVector(r.Points[^1]);
    }

    /// <summary>Ausfahrten dort, wo die Rampe sich wirklich von der Hauptfahrbahn löst
    /// (aus: OSM2ETS_NO_PARALLEL_SPLIT=1).</summary>
    public static bool UseParallelSplit = Environment.GetEnvironmentVariable("OSM2ETS_NO_PARALLEL_SPLIT") != "1";
    public static int SplitMoved;

    /// <summary>
    /// Gegenstück zu <see cref="MoveMergeUpstream"/> für Ausfahrten: In OSM trennt sich die Rampe
    /// am Knoten oft noch ganz flach von der Hauptfahrbahn; der Rampenknoten des Prefabs liegt aber
    /// gleich seitlich versetzt (ch_4202p rund 15 m). Die Rampe lag dann bis zu 17 m neben ihrer
    /// OSM-Lage, etwa über einem Weg daneben. Die Teilung wird deshalb stromab an die Stelle
    /// gerückt, an der die Rampe diesen Abstand hat: Hauptfahrbahn dort geteilt, ihr Anfang bis
    /// dahin wird Ende der Fahrbahn davor.
    /// </summary>
    internal static List<RoadEntry> AllRoads = [];
    public const float BlockRadius = 12f;
    public const float BlockReach = 40f;    // so weit läuft die Rampe in Prefab-Richtung (Einlauf 20 m + Bogen)   // Rampe (ger2 etwa 4 m bis Rand) + Weg + Luft

    /// <summary>Liegt am unverschobenen Platz des Rampenknotens eine fremde Straße (≤ BlockRadius)?</summary>
    public static bool UseSplitShiftAlways = Environment.GetEnvironmentVariable("OSM2ETS_SPLIT_ONLY_BLOCKED") != "1";
    private static bool RampNodeBlocked(Plan p, float lat, float lon)
    {
        var dir = Direction(p.MainIn, p.MainOut);
        var right = new Vector3(-dir.Z, 0, dir.X);
        var a0 = p.Anchor + right * lat + dir * lon;
        var own = new HashSet<RoadEntry> { p.MainIn, p.MainOut, p.Ramp };
        var nodes = new[] { p.MainIn, p.MainOut, p.Ramp }.SelectMany(r => new[] { r.Start, r.End })
                        .Where(e => e?.Kind == "node" && e.OsmNode is long).Select(e => e!.OsmNode!.Value).ToHashSet();
        // Rampenknoten und der Weg der Rampe danach (gerade weiter in Fahrtrichtung, bis BlockReach)
        for (float sAlong = 0; sAlong <= BlockReach; sAlong += 5)
        {
            var a = a0 + dir * sAlong;
            var q = new Vector2(a.X, a.Z);
            foreach (var r in AllRoads)
            {
                if (own.Contains(r)) continue;
                if (new[] { r.Start, r.End }.Any(e => e?.Kind == "node" && e.OsmNode is long id && nodes.Contains(id))) continue;
                for (int i = 0; i < r.Points.Count - 1; i++)
                {
                    var u = P(r.Points[i]); var ab = P(r.Points[i + 1]) - u; float l2 = ab.LengthSquared();
                    float t = l2 > 0 ? Math.Clamp(Vector2.Dot(q - u, ab) / l2, 0, 1) : 0;
                    if (Vector2.Distance(q, u + ab * t) <= BlockRadius) return true;
                }
            }
        }
        return false;
    }

    private static void MoveSplitDownstream(Plan p, PpdSource src)
    {
        var ppd = src.Get(p.K.Split!.PpdPath);
        var loc = Calibrate(ppd, p.K.Split.Unit, Quaternion.Identity);
        var fwd = loc[SplitMain] - loc[SplitIn]; fwd.Y = 0; fwd = Vector3.Normalize(fwd);
        var rv = loc[SplitRamp] - loc[SplitMain]; rv.Y = 0;
        float latS = fwd.X * rv.Z - fwd.Z * rv.X;   // > 0: rechts (rechts = (-d.z, d.x))
        float lat = MathF.Abs(latS), lon = Vector3.Dot(rv, fwd);
        float total = (float)ArcLength(p.MainOut);
        float found = -1;
        for (float D = 2; D <= MathF.Min(MaxParallelShift, total - 60); D += 2)
        {
            var q = AxisAt(p.MainOut, true, D);
            if (RampDistance(p.Ramp, new Vector2(q.X, q.Z)) >= lat) { found = D; break; }
        }
        // Ohne Bogen: weiter stromab, wo die OSM-Rampe am Rampenknoten mindestens so steil von der
        // Hauptfahrbahn wegläuft wie der Rampenarm des Prefabs (sonst läuft die Rampe erst im Arm-
        // winkel hinaus und biegt dann zurück)
        float armDiv = 0, foundSmooth = -1;
        if (found >= 0 && UseBendFreeExit)
        {
            var dirs = WorldDirections(ppd, loc);
            var arm = -dirs[SplitRamp]; arm.Y = 0;
            if (arm.LengthSquared() > 1e-6f)
            {
                arm = Vector3.Normalize(arm);
                armDiv = MathF.Acos(Math.Clamp(Vector3.Dot(arm, fwd), -1, 1)) * 180 / MathF.PI;
                float bestCost = float.MaxValue, bestD = -1;
                if (armDiv > 90) armDiv = 180 - armDiv;
                for (float D = found; D <= MathF.Min(MaxParallelShift, total - 60); D += 2)
                {
                    var q0 = AxisAt(p.MainOut, true, MathF.Max(0, D - 2)); var q1 = AxisAt(p.MainOut, true, D + 2);
                    var md = new Vector2(q1.X - q0.X, q1.Z - q0.Z);
                    if (md.LengthSquared() < 1e-6f) continue;
                    md = Vector2.Normalize(md);
                    var right = new Vector2(-md.Y, md.X);
                    var q = AxisAt(p.MainOut, true, D);
                    var node = new Vector2(q.X, q.Z) + right * latS + md * lon;
                    float dist = RampDistance(p.Ramp, node);
                    var rt = RampTangent(p.Ramp, node);
                    float div = MathF.Acos(Math.Clamp(MathF.Abs(Vector2.Dot(rt, md)), 0, 1)) * 180 / MathF.PI;
                    if (dist <= BendFreeTol && div >= armDiv - BendFreeAngle) { foundSmooth = D; break; }
                    // sonst die Stelle mit dem kleinsten Bogen merken (Winkel- und Lagefehler)
                    float cost = MathF.Max(0, armDiv - div) + 2f * MathF.Max(0, dist - BendFreeTol);
                    if (dist < 10f && cost < bestCost) { bestCost = cost; bestD = D; }
                }
                if (foundSmooth < 0 && bestD >= 0) foundSmooth = bestD;
                if (foundSmooth >= 0) found = foundSmooth;
            }
        }
        float d = found - lon - 2;     // Prefab-Hauptausgang so, dass der Rampenknoten dort liegt
        if (Environment.GetEnvironmentVariable("OSM2ETS_IPROBE") is not null) Console.Error.WriteLine($"SPLITTRY arm {armDiv:0.0} smooth {foundSmooth:0} {p.OsmNode} {p.OsmNode} {p.K.Name} found {found:0} d {d:0} lat {lat:0.0} blocked {RampNodeBlocked(p, latS, lon)}");
        if (found < 0 || d < 20) return;
        // Nur wo es nötig ist: am alten Platz läge der Rampenknoten auf einer fremden Straße
        // (Weg, andere Straße). Sonst bleibt die Teilung am OSM-Knoten – das Verschieben ändert
        // die Lage zu Nachbarn (Gelände, Brücken) und brachte an anderen Stellen neue Wände.
        // Inzwischen allgemein: sonst liegt der Rampenknoten am OSM-Knoten seitlich um die volle
        // Prefab-Breite neben der OSM-Rampe, und die Rampe muss in einer S-Kurve zurück
        // (aus: OSM2ETS_SPLIT_ONLY_BLOCKED=1)
        if (!UseSplitShiftAlways && !RampNodeBlocked(p, latS, lon)) return;
        // hinter dem Rampenknoten muss genug Rampe bleiben
        var qf = AxisAt(p.MainOut, true, found);
        float after = (float)ArcLength(p.Ramp) - RampArcAt(p.Ramp, new Vector2(qf.X, qf.Z));
        if (after < (p.Ramp.End?.Kind == "prefab" ? MinRampBefore * 3 : MinRampBefore)) return;
        // Hauptfahrbahn danach bei d teilen
        var pts = p.MainOut.Points; double acc = 0; int k = 0;
        while (k < pts.Count - 1 && acc + Dist(pts[k], pts[k + 1]) < d) { acc += Dist(pts[k], pts[k + 1]); k++; }
        if (k >= pts.Count - 1) return;
        float rest = (float)(d - acc), seg = (float)Dist(pts[k], pts[k + 1]);
        var a = pts[k]; var b = pts[k + 1];
        double t = seg > 0 ? rest / seg : 0;
        var cut = new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t };
        var head = pts.Take(k + 1).Skip(1).Append(cut).ToList();      // vom OSM-Knoten bis zum Schnitt
        int add = head.Count;
        int nIn = p.MainIn.Points.Count - 1;
        p.MainIn.Points = p.MainIn.Points.Concat(head).ToList();
        p.MainIn.Runs[^1].To = p.MainIn.Points.Count - 1;
        var outPts = new List<double[]> { cut };
        outPts.AddRange(pts.Skip(k + 1));
        int drop = k;   // Abschnitte 0..k-1 und das Stück von k bis zum Schnitt fallen weg
        p.MainOut.Runs = p.MainOut.Runs.Where(x => x.To > k).Select(x => { x.From = Math.Max(x.From - k, 0); x.To -= k; return x; }).ToList();
        p.MainOut.Runs[0].From = 0;
        p.MainOut.Points = outPts;
        p.MainOut.Runs[^1].To = outPts.Count - 1;
        LaneCentering.Original.Remove(p.MainIn);
        LaneCentering.Original.Remove(p.MainOut);
        p.Anchor = MapWriter.ToVector(cut);
        p.Direction = Direction(p.MainIn, p.MainOut);
        p.Moved = d;
        SplitMoved++;
        if (Environment.GetEnvironmentVariable("OSM2ETS_IPROBE") is not null) Console.Error.WriteLine($"SPLITMOVE {p.OsmNode} {p.K.Name} d {d:0} lat {lat:0.0} lon {lon:0.0}");
    }

    /// <summary>Größte seitliche Abweichung der geraden Prefab-Kette von der Straße, ab der eine
    /// Zusatzspur-Straße zwischen die beiden Prefabs kommt (m).</summary>
    public const float MaxChainSag = 2f;
    /// <summary>Länge dieser Zusatzspur-Straße (m).</summary>
    public const float CurveExtLength = 30f;
    public static int CurveExts;

    /// <summary>
    /// Zwei direkt verbundene Anschluss-Prefabs sind zusammen gerade (rund 100 m). In einer Kurve
    /// lag ihr Ende bis 17 m neben der Straße, die dann in einem S-Bogen zurück musste (Rampen
    /// südlich des Dreiecks). Weicht die Gerade mehr als <see cref="MaxChainSag"/> ab, kommt eine
    /// Zusatzspur-Straße dazwischen; jedes Prefab folgt dann der Sehne seines eigenen Stücks.
    /// pts: Straßenpunkte ab dem Knoten in Richtung der Prefabs.
    /// </summary>
    private static float CurveExt(List<double[]> pts, Vector3 anchor, float len)
    {
        if (!UseCurveExt) return 0;
        var end = PointAlong(pts, len);
        var a = new Vector2(anchor.X, anchor.Z); var e = new Vector2(end.X, end.Z);
        var ch = e - a; float cl = ch.Length();
        if (cl < 1) return 0;
        var u = ch / cl; float sag = 0;
        for (float s = 5; s < len; s += 5)
        {
            var q = PointAlong(pts, s); var w = new Vector2(q.X, q.Z) - a;
            sag = MathF.Max(sag, MathF.Abs(w.X * u.Y - w.Y * u.X));
        }
        if (sag <= MaxChainSag) return 0;
        CurveExts++;
        return CurveExtLength;
    }

    /// <summary>Zusatzspur-Straße in Kurven (aus: OSM2ETS_NO_CURVE_EXT=1).</summary>
    public static bool UseCurveExt = Environment.GetEnvironmentVariable("OSM2ETS_NO_CURVE_EXT") != "1";

    /// <summary>So viel Rampe muss vor dem verschobenen Rampenknoten bleiben (m).</summary>
    public const float MinRampBefore = 60f;
    /// <summary>Kleinster Rampenabstand (Anteil des Prefab-Abstands), bis zu dem eine Einmündung vor
    /// einem Rampen-Prefab noch stromauf gerückt wird.</summary>
    public const float ShortRampLat = 0.5f;
    /// <summary>Rampenstück zwischen Spurende-Prefab der Rampe und Einfahrt (m), zusätzlich zum
    /// Anschlussstück (Slack), das hinter dem Spurende-Prefab ohnehin bleibt.</summary>
    public const float PrefabGap = 1f;
    /// <summary>Einfahrt direkt an das Spurende-Prefab der Rampe hängen (aus: OSM2ETS_NO_DIRECT_JOIN=1).</summary>
    public static bool UseDirectJoin = Environment.GetEnvironmentVariable("OSM2ETS_NO_DIRECT_JOIN") != "1";
    public static int DirectJoins;
    private static readonly Dictionary<RoadEntry, (Prefab Pf, Plan Plan)> DropPlaced = [];
    public const float ShiftReach = 300f;

    /// <summary>Straßenende um delta verschieben, zum Anfang hin (über reach m) weich auslaufend.</summary>
    private static void TaperShift(RoadEntry r, Vector3 delta, float reach)
    {
        int n = r.Points.Count;
        var s = new double[n];
        for (int i = n - 2; i >= 0; i--) s[i] = s[i + 1] + Dist(r.Points[i], r.Points[i + 1]);   // Abstand zum Ende
        double L = Math.Min(reach, s[0]);
        for (int i = 0; i < n; i++)
        {
            if (s[i] >= L) continue;
            double t = 1 - s[i] / L, w = t * t * (3 - 2 * t);
            r.Points[i][0] += delta.X * w; r.Points[i][2] += delta.Z * w;
        }
    }
    /// <summary>Neu gesetzte Spurende-Prefabs (alt → neu), damit MapWriter seine Liste anpasst.</summary>
    public static readonly Dictionary<Prefab, Prefab> Replaced = [];
    /// <summary>Richtungsänderung (Grad je Meter) an beiden Enden zusammen, die das Rampenstück
    /// zwischen Spurende-Prefab und Einfahrt als flacher Bogen aufnehmen darf.</summary>
    public static float JoinTurnPerM = float.TryParse(Environment.GetEnvironmentVariable("OSM2ETS_JOIN_TURN"), System.Globalization.CultureInfo.InvariantCulture, out var jt) ? jt : 0.4f;

    /// <summary>Bogenlänge vom Rampenanfang bis zum Punkt, der q am nächsten liegt.</summary>
    private static float RampArcAt(RoadEntry r, Vector2 q)
    {
        float best = float.MaxValue, at = 0, acc = 0;
        for (int i = 0; i < r.Points.Count - 1; i++)
        {
            var a = P(r.Points[i]); var ab = P(r.Points[i + 1]) - a;
            float l2 = ab.LengthSquared(), l = MathF.Sqrt(l2);
            float t = l2 > 0 ? Math.Clamp(Vector2.Dot(q - a, ab) / l2, 0, 1) : 0;
            float dd = Vector2.Distance(a + ab * t, q);
            if (dd < best) { best = dd; at = acc + t * l; }
            acc += l;
        }
        return at;
    }

    public static bool UseBendFreeExit = Environment.GetEnvironmentVariable("OSM2ETS_NO_BENDFREE_EXIT") is null;
    public const float BendFreeTol = 3f, BendFreeAngle = 3f;

    /// <summary>Richtung der Rampe am Punkt, der q am nächsten liegt.</summary>
    private static Vector2 RampTangent(RoadEntry r, Vector2 q)
    {
        float best = float.MaxValue; var dir = Vector2.UnitX;
        for (int i = 0; i < r.Points.Count - 1; i++)
        {
            var a = P(r.Points[i]); var ab = P(r.Points[i + 1]) - a; float l2 = ab.LengthSquared();
            if (l2 < 1e-6f) continue;
            float t = Math.Clamp(Vector2.Dot(q - a, ab) / l2, 0, 1);
            float dd = Vector2.Distance(q, a + ab * t);
            if (dd < best) { best = dd; dir = Vector2.Normalize(ab); }
        }
        return dir;
    }

    private static float RampDistance(RoadEntry r, Vector2 q)
    {
        float best = float.MaxValue;
        for (int i = 0; i < r.Points.Count - 1; i++)
        {
            var a = P(r.Points[i]); var ab = P(r.Points[i + 1]) - a;
            float l2 = ab.LengthSquared();
            float t = l2 > 0 ? Math.Clamp(Vector2.Dot(q - a, ab) / l2, 0, 1) : 0;
            best = MathF.Min(best, Vector2.Distance(a + ab * t, q));
        }
        return best;
    }

    /// <summary>
    /// Verschiebung des inneren Prefabs (siehe <see cref="Plan.Shift"/>) und Länge der Zusatzspur-
    /// Straße. spans: Bauwerke ab dem Knoten auf der Seite der Prefabs, behind: auf der anderen Seite.
    /// 0, wenn kein Bauwerk unter dem inneren Prefab läge. Sonst so weit, dass es
    /// <see cref="ExtMargin"/> vor dem ersten Bauwerk endet – aber nur, wenn das höchstens
    /// <see cref="MaxShift"/> ist, auf der anderen Seite des Knotens dort kein Bauwerk liegt, die
    /// Straßen lang genug bleiben und das äußere Prefab jenseits des Bauwerks Platz findet.
    /// Andernfalls bleibt es wie bisher (Prefab über dem Bauwerk, ohne Brückenkörper).
    /// </summary>
    internal static float ShiftFor(List<(float A, float B)> spans, List<(float A, float B)> behind, float lenNear, float lenFar,
                                  float lenMain, float lenOther, float lenRamp, out float ext)
    {
        ext = ExtFor(spans, lenNear, lenFar);
        if (!UseShift || !UseExt) return 0;
        var under = spans.Where(s => s.A < lenNear + ExtMargin).ToList();
        if (under.Count == 0) return 0;
        float d = lenNear + ExtMargin - under.Min(s => s.A) + 0.5f;
        if (d > MaxShift || d > lenNear - 5) return 0;
        if (behind.Any(s => s.A < d + ExtMargin)) return 0;
        float e = ExtFor(spans.Select(s => (s.A + d, s.B + d)).ToList(), lenNear, lenFar);
        if (e <= 0) return 0;
        if (lenMain < lenNear + lenFar + e - d + Slack + 20 || lenOther < d + Slack + 20 || lenRamp < RampTrim + d + 20) return 0;
        ext = e;
        return d;
    }

    /// <summary>
    /// Länge der Zusatzspur-Straße: 0, wenn kein Bauwerk unter dem äußeren Prefab läge (oder eines
    /// schon unter dem inneren – das bleibt wie bisher). Sonst so lang, dass das äußere Prefab
    /// <see cref="ExtMargin"/> hinter dem letzten betroffenen Bauwerk beginnt.
    /// </summary>
    private static float ExtFor(List<(float A, float B)> spans, float lenNear, float lenFar)
    {
        if (!UseExt || spans.Count == 0) return 0;
        if (spans.Any(s => s.A < lenNear + ExtMargin)) return 0;
        float ext = 0;
        for (int it = 0; it < 10; it++)
        {
            float z0 = lenNear + ext - ExtMargin, z1 = lenNear + ext + lenFar + Slack + ExtMargin;
            var hit = spans.Where(s => s.B > z0 && s.A < z1).ToList();
            if (hit.Count == 0) break;
            ext = hit.Max(s => s.B) + ExtMargin - lenNear;
        }
        return ext > MaxExt ? 0 : ext;
    }

    /// <summary>Bauwerke (Brücken/Tunnel) als Abstände [A, B] vom Knoten aus (pts beginnt am Knoten).</summary>
    private static List<(float A, float B)> StructureSpans(List<double[]> pts, List<RunEntry> runs, bool reversed, float reach)
    {
        var res = new List<(float A, float B)>();
        int n = pts.Count;
        double acc = 0;
        for (int i = 0; i < n - 1 && acc < reach; i++)
        {
            int seg = reversed ? n - 2 - i : i;
            var run = runs.FirstOrDefault(x => x.From <= seg && seg < x.To);
            double s = Math.Sqrt(Math.Pow(pts[i + 1][0] - pts[i][0], 2) + Math.Pow(pts[i + 1][2] - pts[i][2], 2));
            if (run is not null && (run.Bridge || run.Tunnel))
            {
                if (res.Count > 0 && MathF.Abs(res[^1].B - (float)acc) < 0.01f) res[^1] = (res[^1].A, (float)(acc + s));
                else res.Add(((float)acc, (float)(acc + s)));
            }
            acc += s;
        }
        return res;
    }

    /// <summary>Lauf der Straße im Abstand dist (Draufsicht) vom Anfang bzw. Ende – auf der aktuellen
    /// Lage wie <see cref="StructureSpans"/> (die gespeicherte Originallage teilt sich die Lauf-Objekte
    /// mit der aktuellen; deren Grenzen hat ein anderer Plan dann womöglich schon verschoben).</summary>
    private static RunEntry RunAt(RoadEntry r, bool fromStart, float dist)
    {
        var pts = r.Points; var runs = r.Runs;
        int n = pts.Count;
        double acc = 0;
        for (int i = 0; i < n - 1; i++)
        {
            int a = fromStart ? i : n - 1 - i, b = fromStart ? i + 1 : n - 2 - i;
            double s = Math.Sqrt(Math.Pow(pts[b][0] - pts[a][0], 2) + Math.Pow(pts[b][2] - pts[a][2], 2));
            if (acc + s >= dist || i == n - 2)
            {
                int seg = fromStart ? i : n - 2 - i;
                return runs.FirstOrDefault(x => x.From <= seg && seg < x.To) ?? runs[^1];
            }
            acc += s;
        }
        return runs[^1];
    }

    /// <summary>
    /// Zusatzspur-Straße aus der Lage der Hauptfahrbahn zwischen den Abständen d0 und d1 vom Knoten
    /// (Reihenfolge = Fahrtrichtung). Punkte an jedem Bauwerksende, dazwischen höchstens 20 m.
    /// Die Enden werden nach dem Setzen der Prefabs genau auf deren Knoten gelegt (<see cref="SnapExt"/>).
    /// </summary>
    internal static RoadEntry ExtRoad(Plan p, RoadEntry main, bool fromStart, float d0, float d1, List<(float A, float B)> spans)
    {
        float lo = MathF.Min(d0, d1), hi = MathF.Max(d0, d1);
        var ds = new List<float> { lo, hi };
        foreach (var (a, b) in spans) { if (a > lo + 2 && a < hi - 2) ds.Add(a); if (b > lo + 2 && b < hi - 2) ds.Add(b); }
        ds.Sort();
        var dense = new List<float> { ds[0] };
        for (int k = 1; k < ds.Count; k++)
        {
            float a = dense[^1], b = ds[k];
            int steps = (int)MathF.Ceiling((b - a) / 20f);
            for (int s = 1; s <= steps; s++) dense.Add(a + (b - a) * s / steps);
        }
        dense = dense.Distinct().ToList();
        if (d0 > d1) dense.Reverse();   // Ausfahrt: vom fernen Prefab zum Knoten hin
        // ein seitlicher Versatz für die ganze Zusatzspur-Straße (eine Straße, ein Querschnitt): sonst
        // springt die Linie am OSM-Spurwechsel um den Unterschied der Modellversätze (S-Kurve, A 9 z −2650)
        var lat = RunAt(main, fromStart, dense[^1] > dense[0] ? (dense[^1] + dense[^2]) / 2 : (dense[0] + dense[1]) / 2);
        var pts = dense.Select(d => { var q = AxisAt(main, fromStart, d, lat); return new double[] { q.X, q.Y, q.Z }; }).ToList();
        var runs = new List<RunEntry>();
        for (int k = 0; k < dense.Count - 1; k++)
        {
            float mid = (dense[k] + dense[k + 1]) / 2;
            var src = RunAt(main, fromStart, mid);
            bool structure = spans.Any(s => mid > s.A && mid < s.B);
            // Kurzes Endstück zwischen Prefab und Brücke (ExtMargin): gehört zur Brücke. Als eigene
            // Straße stand sein Straßengelände als schmale Zunge neben der Unterführung, daneben eine
            // Mulde (an der verschobenen Zusammenführung vor der A-70-Brücke, v89).
            if (!structure && AbsorbStubs && (k == 0 || k == dense.Count - 2))
            {
                float s0 = MathF.Min(dense[k], dense[k + 1]), s1 = MathF.Max(dense[k], dense[k + 1]);
                if (s1 - s0 <= ExtMargin + 1.5f)
                    foreach (var sp in spans)
                    {
                        if (MathF.Abs(sp.A - s1) > 0.5f && MathF.Abs(sp.B - s0) > 0.5f) continue;
                        var sr = RunAt(main, fromStart, (sp.A + sp.B) / 2);
                        if (!sr.Bridge || sr.Tunnel) continue;
                        src = sr;
                        structure = true;
                        break;
                    }
            }
            var run = src.Span(k, k + 1);
            run.Role = p.K.ExtRole;
            run.Bridge = structure && src.Bridge;
            run.Tunnel = structure && src.Tunnel;
            if (runs.Count > 0 && runs[^1].Bridge == run.Bridge && runs[^1].Tunnel == run.Tunnel && runs[^1].Layer == run.Layer)
                runs[^1].To = k + 1;
            else runs.Add(run);
        }
        return new RoadEntry
        {
            Id = $"{main.Id}x{p.OsmNode}",
            Chain = main.Chain, Piece = main.Piece, Role = main.Role,
            Start = new EndInfo { Kind = "prefab" }, End = new EndInfo { Kind = "prefab" },
            Points = pts, Runs = runs,
        };
    }

    /// <summary>
    /// Legt die Enden der Zusatzspur-Straße genau auf die Prefab-Knoten. Die dreispurigen
    /// Innenknoten liegen seitlich neben der Achse, aus der die Punkte stammen: den Versatz
    /// beider Enden entlang der Straße linear auf alle Punkte verteilen (Lage quer, Höhe bleibt).
    /// </summary>
    private static void SnapExt(RoadEntry r, Vector3 from, Vector3 to)
    {
        var d0 = new Vector2(from.X - (float)r.Points[0][0], from.Z - (float)r.Points[0][2]);
        var d1 = new Vector2(to.X - (float)r.Points[^1][0], to.Z - (float)r.Points[^1][2]);
        var cum = new float[r.Points.Count];
        for (int i = 1; i < cum.Length; i++) cum[i] = cum[i - 1] + Vector2.Distance(P(r.Points[i - 1]), P(r.Points[i]));
        for (int i = 1; i < cum.Length - 1; i++)
        {
            var d = Vector2.Lerp(d0, d1, cum[^1] > 0 ? cum[i] / cum[^1] : 0);
            r.Points[i] = [r.Points[i][0] + d.X, r.Points[i][1], r.Points[i][2] + d.Y];
        }
        r.Points[0] = V(from);
        r.Points[^1] = V(to);
        // Punkte unmittelbar an den Enden (unter 3 m) entfallen – Läufe dabei mitführen
        while (r.Points.Count > 2 && Vector2.Distance(P(r.Points[0]), P(r.Points[1])) < 3f) RemovePoint(r, 1);
        while (r.Points.Count > 2 && Vector2.Distance(P(r.Points[^1]), P(r.Points[^2])) < 3f) RemovePoint(r, r.Points.Count - 2);
    }

    private static void RemovePoint(RoadEntry r, int k)
    {
        // Abschnitt k−1..k entfällt (Punkt k fällt weg); Läufe ab k rücken um eins
        r.Points.RemoveAt(k);
        foreach (var x in r.Runs)
        {
            if (x.From >= k) x.From--;
            if (x.To >= k) x.To--;
        }
        r.Runs = r.Runs.Where(x => x.To > x.From).ToList();
        r.Runs[0].From = 0;
        r.Runs[^1].To = r.Points.Count - 1;
    }

    /// <summary>Liegt innerhalb von reach Metern ab dem Knoten (pts beginnt am Knoten) eine Brücke/ein Tunnel?
    /// Ein Prefab ist kein Bauwerk – darüber/darunter läge sonst massives Gelände.</summary>
    private static bool HasStructure(List<double[]> pts, List<RunEntry> runs, bool reversed, float reach)
    {
        int n = pts.Count;
        double acc = 0;
        for (int i = 0; i < n - 1 && acc < reach; i++)
        {
            int seg = reversed ? n - 2 - i : i;   // Segmentindex in der Originalreihenfolge
            var run = runs.FirstOrDefault(x => x.From <= seg && seg < x.To);
            if (run is not null && (run.Bridge || run.Tunnel)) return true;
            acc += Math.Sqrt(Math.Pow(pts[i + 1][0] - pts[i][0], 2) + Math.Pow(pts[i + 1][2] - pts[i][2], 2));
        }
        return false;
    }

    /// <summary>Setzt die Prefabs eines Plans (Straßen müssen schon gebaut sein).</summary>
    public static Placed Build(Map map, Plan p, PpdSource src)
    {
        if (p.Kind == "drop")
        {
            var pf = Place(map, src, p.K.Narrow!, NarrowIn, NarrowOut, p.Direction, NarrowIn, p.Anchor, p.Grade);
            DropPlaced[p.MainOut] = (pf, p);
            return new Placed(p, pf, NarrowIn, pf, NarrowOut, pf, NarrowIn);
        }
        if (p.Kind == "exit")
        {
            // Teilung so, dass ihr Hauptausgang auf dem OSM-Knoten liegt; Aufweitung davor
            var split = Place(map, src, p.K.Split!, SplitIn, SplitMain, p.Direction, SplitMain, p.Anchor, p.Grade);
            if (p.K.Widen is null) return new Placed(p, split, SplitIn, split, SplitMain, split, SplitRamp);
            if (p.ExtRoad is not null)
            {
                // Aufweitung jenseits des Bauwerks; die Zusatzspur-Straße verbindet beide
                var w = Place(map, src, p.K.Widen!, WidenIn, WidenOut, p.FarDirection, WidenIn, p.FarAnchor, p.FarGrade);
                return new Placed(p, w, WidenIn, split, SplitMain, split, SplitRamp) { ExtFrom = w, ExtFromNode = WidenOut, ExtTo = split, ExtToNode = SplitIn };
            }
            // Richtung an der Fuge aus dem gesetzten Nachbar-Prefab übernehmen (sonst Keil in der Fuge)
            var dSplit = Flat(InwardDirections[split][SplitIn], p.Direction);
            var widen = Place(map, src, p.K.Widen, WidenIn, WidenOut, dSplit, WidenOut, split.Nodes[SplitIn].Position, p.Grade);
            widen.Attach(WidenOut, split, SplitIn);
            if (p.K.Widen0 is { } w0def)
            {
                var dW = Flat(InwardDirections[widen][WidenIn], p.Direction);
                var widen0 = Place(map, src, w0def, WidenIn, WidenOut, dW, WidenOut, widen.Nodes[WidenIn].Position, p.Grade);
                widen0.Attach(WidenOut, widen, WidenIn);
                return new Placed(p, widen0, WidenIn, split, SplitMain, split, SplitRamp);
            }
            return new Placed(p, widen, WidenIn, split, SplitMain, split, SplitRamp);
        }
        else
        {
            Prefab join;
            if (UseDirectJoin && p.AfterRampPrefab && DropPlaced.TryGetValue(p.Ramp, out var dp))
            {
                // Einfahrt direkt an das Spurende-Prefab der Rampe: die Einfahrt bleibt an der
                // Hauptfahrbahn ausgerichtet (gerade Autobahn bleibt gerade); stattdessen wird das
                // Spurende-Prefab neu gesetzt – sein Ausgang auf den Rampenknoten, in Richtung des
                // Rampenarms – und die Rampe davor zu seinem Eingang geführt
                join = Place(map, src, p.K.Join!, JoinMain, JoinOut, p.Direction, JoinMain, p.Anchor, p.Grade);
                var arm = InwardDirections[join][JoinRamp];
                var old = dp.Pf;
                var nd = Place(map, src, dp.Plan.K.Narrow!, NarrowIn, NarrowOut, arm, NarrowOut, join.Nodes[JoinRamp].Position, p.Grade);   // Neigung wie die Einfahrt (gemeinsamer Knoten)
                // Rampe davor sanft auf den neuen Eingang verlegen: Versatz über ihre ganze Länge
                // (höchstens ShiftReach m, z. B. ab der Brücke) verteilt, nicht erst in den letzten 40 m
                TaperShift(dp.Plan.MainIn, nd.Nodes[NarrowIn].Position - old.Nodes[NarrowIn].Position, ShiftReach);
                map.Delete(old);
                join.Attach(JoinRamp, nd, NarrowOut);
                SetTail(dp.Plan.MainIn, nd.Nodes[NarrowIn].Position, InwardDirections[nd][NarrowIn], Lead, RampBlend);
                nd.Nodes[NarrowIn].Rotation = MathEx.GetNodeRotation(Sloped(InwardDirections[nd][NarrowIn], p.Grade));
                Replaced[old] = nd;
                p.DirectRamp = true;
                DirectJoins++;
            }
            else join = Place(map, src, p.K.Join!, JoinMain, JoinOut, p.Direction, JoinMain, p.Anchor, p.Grade);
            if (p.K.Narrow is null) return new Placed(p, join, JoinMain, join, JoinOut, join, JoinRamp);
            if (p.ExtRoad is not null)
            {
                var nw = Place(map, src, p.K.Narrow!, NarrowIn, NarrowOut, p.FarDirection, NarrowOut, p.FarAnchor, p.FarGrade);
                return new Placed(p, join, JoinMain, nw, NarrowOut, join, JoinRamp) { ExtFrom = join, ExtFromNode = JoinOut, ExtTo = nw, ExtToNode = NarrowIn };
            }
            var dJoin = Flat(-InwardDirections[join][JoinOut], p.Direction);
            var narrow = Place(map, src, p.K.Narrow!, NarrowIn, NarrowOut, dJoin, NarrowIn, join.Nodes[JoinOut].Position, p.Grade);
            join.Attach(JoinOut, narrow, NarrowIn);
            return new Placed(p, join, JoinMain, narrow, NarrowOut, join, JoinRamp);
        }
    }

    // ------------------------------------------------------------------ tangentialer Anschluss

    public const float Lead = 20f;

    /// <summary>
    /// Hauptfahrbahn an Anschluss-Prefabs: so weit (m) vom Prefab-Knoten wird sie als weiche
    /// Kurve aus der Prefab-Richtung auf die OSM-Linie zurückgeführt (statt nach einem
    /// Straßenstück abzuknicken). OSM2ETS_MAIN_BLEND=0 schaltet ab.
    /// </summary>
    /// <summary>Dasselbe für Rampen (OSM2ETS_RAMP_BLEND=<m>, 0 = aus).</summary>
    public static float RampBlend = float.TryParse(Environment.GetEnvironmentVariable("OSM2ETS_RAMP_BLEND"), System.Globalization.CultureInfo.InvariantCulture, out var rb) ? rb : 40f;

    public static float MainBlend = float.TryParse(Environment.GetEnvironmentVariable("OSM2ETS_MAIN_BLEND"), System.Globalization.CultureInfo.InvariantCulture, out var mb) ? mb : 40f;
    /// <summary>Einlauf der Rampe in <see cref="Hw3Merge2"/>: Der Rampenknoten steht 15° schräg; mit
    /// 20 m geradem Einlauf schwenkte die Rampe 5 m nach außen (A 9 bei Bayreuth: auf den Wirtschaftsweg
    /// daneben).</summary>
    public const float ShortRampLead = 6f;   // so lang läuft die Straße gerade in den Prefab-Knoten ein

    /// <summary>Lässt eine Straße genau am Prefab-Knoten enden und in dessen Richtung einlaufen.</summary>
    public static void SetTail(RoadEntry r, Vector3 node, Vector3 dirInto, float leadLen = Lead, float curve = 0)
    {
        var lead = node - dirInto * leadLen;
        int k = r.Points.Count - 1;
        float cut = MathF.Max(leadLen + 10, curve);
        while (k > 0 && Vector2.Distance(P(r.Points[k]), new Vector2(node.X, node.Z)) < cut) k--;
        // Punkte, die schon jenseits des Einlaufpunkts liegen, ebenfalls weg (sonst Rücklauf)
        while (k > 0 && Vector2.Dot(P(r.Points[k]) - new Vector2(lead.X, lead.Z), new Vector2(dirInto.X, dirInto.Z)) > -2) k--;
        // Reichte die Straße über den Knoten hinaus (Einmündung stromauf gerückt), greift der
        // Abstand erst jetzt
        while (k > 0 && Vector2.Distance(P(r.Points[k]), new Vector2(node.X, node.Z)) < cut) k--;
        var pts = r.Points.Take(k + 1).ToList();
        if (curve > 0 && pts.Count < 2 && leadLen > ShortRampLead)
        {
            // kurzes Reststück: kein Platz für einen Bogen – kürzerer gerader Einlauf, damit es am
            // Anfangsknoten (Übergang zur Straße davor) nicht abknickt
            leadLen = ShortRampLead;
            lead = node - dirInto * leadLen;
        }
        lead.Y = Between(node, pts[^1], leadLen);
        if (curve > 0 && pts.Count >= 2)
        {
            // Bogen vom letzten behaltenen Punkt (mit dessen Richtung) zum Einlaufpunkt
            var p3 = MapWriter.ToVector(pts[^1]); var p2 = MapWriter.ToVector(pts[^2]);
            var tIn = p3 - p2; tIn.Y = 0; tIn = Vector3.Normalize(tIn);
            foreach (var q in Bezier(p3, tIn, lead, dirInto))
            {
                // nur Punkte vor dem Einlauf und mit Abstand zum Vorgänger (keine Schleifen)
                var dq = q - lead; dq.Y = 0;
                if (Vector3.Dot(dq, dirInto) > -3) continue;
                if (Vector2.Distance(P(pts[^1]), new Vector2(q.X, q.Z)) < 4) continue;
                pts.Add(V(q));
            }
        }
        if (Vector2.Distance(P(pts[^1]), new Vector2(lead.X, lead.Z)) > 2) pts.Add(V(lead));
        pts.Add(V(node));
        int segs = pts.Count - 1;
        // Die neuen Abschnitte ab Punkt k (Einlauf ins Prefab) ersetzen die alten ab k – sie bekommen
        // deren Attribute. Früher erbte sie der Lauf, der bei k ENDETE: endete dort eine Brücke,
        // galt der Einlauf samt Dammstrecke als Brücke (BT 46 an der A 9: kein Straßengelände,
        // Fahrbahn 2,5 m über dem Gelände).
        var tail = r.Runs.FirstOrDefault(x => x.From <= k && k < x.To) ?? r.Runs[^1];
        var runs = r.Runs.Where(x => x.From < k).Select(x => { x.To = Math.Min(x.To, k); return x; }).ToList();
        if (runs.Count > 0 && ReferenceEquals(runs[^1], tail)) runs[^1].To = segs;
        else runs.Add(tail.Span(k, segs));
        if (runs[0].From > 0) runs[0].From = 0;
        r.Points = pts;
        r.Runs = runs.Where(x => x.From < x.To).ToList();
    }

    /// <summary>Lässt eine Straße genau am Prefab-Knoten beginnen und in dessen Richtung auslaufen.</summary>
    public static void SetHead(RoadEntry r, Vector3 node, Vector3 dirOut, float leadLen = Lead, float curve = 0)
    {
        var lead = node + dirOut * leadLen;
        int k = 0;
        float cut = MathF.Max(leadLen + 10, curve);
        while (k < r.Points.Count - 1 && Vector2.Distance(P(r.Points[k]), new Vector2(node.X, node.Z)) < cut) k++;
        while (k < r.Points.Count - 1 && Vector2.Dot(P(r.Points[k]) - new Vector2(lead.X, lead.Z), new Vector2(dirOut.X, dirOut.Z)) < 2) k++;
        while (k < r.Points.Count - 1 && Vector2.Distance(P(r.Points[k]), new Vector2(node.X, node.Z)) < cut) k++;
        var rest = r.Points.Skip(k).ToList();
        if (curve > 0 && rest.Count < 2 && leadLen > ShortRampLead)
        {
            leadLen = ShortRampLead;            // kurzes Reststück, siehe SetTail
            lead = node + dirOut * leadLen;
        }
        var pts = new List<double[]> { V(node) };
        lead.Y = Between(node, rest[0], leadLen);
        if (Vector2.Distance(P(rest[0]), new Vector2(lead.X, lead.Z)) > 2) pts.Add(V(lead));
        if (curve > 0 && rest.Count >= 2)
        {
            var p3 = MapWriter.ToVector(rest[0]); var p4 = MapWriter.ToVector(rest[1]);
            var tOut = p4 - p3; tOut.Y = 0; tOut = Vector3.Normalize(tOut);
            foreach (var q in Bezier(lead, dirOut, p3, tOut))
            {
                var dq = q - lead; dq.Y = 0;
                if (Vector3.Dot(dq, dirOut) < 3) continue;
                if (Vector2.Distance(P(pts[^1]), new Vector2(q.X, q.Z)) < 4) continue;
                if (Vector2.Distance(P(rest[0]), new Vector2(q.X, q.Z)) < 4) continue;
                pts.Add(V(q));
            }
        }
        int added = pts.Count;
        pts.AddRange(rest);
        int shift = added - k;
        // neue Abschnitte vor dem alten Punkt k ersetzen die alten bis k – Attribute des Laufs,
        // der den letzten weggefallenen Abschnitt (k − 1) enthielt (siehe SetTail)
        var head = k > 0 ? r.Runs.FirstOrDefault(x => x.From <= k - 1 && k - 1 < x.To) ?? r.Runs[0] : r.Runs[0];
        var runs = r.Runs.Where(x => x.To > k).Select(x => { x.From = Math.Max(x.From, k) + shift; x.To += shift; return x; }).ToList();
        if (runs.Count > 0 && ReferenceEquals(runs[0], head)) runs[0].From = 0;
        else runs.Insert(0, head.Span(0, added));
        runs[0].From = 0;
        runs[^1].To = pts.Count - 1;
        r.Points = pts;
        r.Runs = runs.Where(x => x.From < x.To).ToList();
    }

    private static double[] V(Vector3 v) => [v.X, v.Y, v.Z];

    /// <summary>Innere Punkte eines kubischen Bogens von a (Richtung ta) nach b (Richtung tb), etwa alle 10 m.</summary>
    private static IEnumerable<Vector3> Bezier(Vector3 a, Vector3 ta, Vector3 b, Vector3 tb)
    {
        float d = Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));
        if (d < 15) yield break;
        var c1 = a + ta * (d / 3); var c2 = b - tb * (d / 3);
        int n = Math.Max(2, (int)(d / 10));
        for (int i = 1; i < n; i++)
        {
            float t = i / (float)n, u = 1 - t;
            var q = u * u * u * a + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * b;
            q.Y = a.Y + (b.Y - a.Y) * t;
            yield return q;
        }
    }

    /// <summary>Höhe des Einlaufpunkts: linear zwischen Prefab-Knoten und erstem behaltenen Punkt.</summary>
    private static float Between(Vector3 node, double[] keep, float leadLen)
    {
        float d = Vector2.Distance(new Vector2(node.X, node.Z), P(keep));
        return d <= leadLen ? node.Y : node.Y + ((float)keep[1] - node.Y) * leadLen / d;
    }

    /// <summary>Fahrtrichtung an einem Prefab-Knoten, so orientiert, dass sie zu <paramref name="hint"/> passt.</summary>
    public static Vector3 NodeDirection(INode n, Vector3 hint)
    {
        var d = Vector3.Transform(-Vector3.UnitZ, n.Rotation); d.Y = 0;
        d = Vector3.Normalize(d);
        return Vector3.Dot(d, hint) < 0 ? -d : d;
    }

    private static Vector3 Sloped(Vector3 horizontal, float grade) =>
        Vector3.Normalize(new Vector3(horizontal.X, grade, horizontal.Z));

    /// <summary>Passt die Straßen an die gesetzten Prefabs an (vor dem Bau der Straßen).</summary>
    /// <summary>Knotendrehungen ohne Rollen: nur Gier und Längsneigung.</summary>
    /// <summary>Steigung des Prefabs pf im Plan (mit Zusatzspur-Straße haben beide eigene).</summary>
    private static float GradeOf(Placed pl, Prefab pf)
    {
        var p = pl.Plan;
        if (p.ExtRoad is null) return p.Grade;
        bool far = p.Kind == "exit" ? ReferenceEquals(pf, pl.First) : ReferenceEquals(pf, pl.Last);
        return far ? p.FarGrade : p.Grade;
    }

    private static void FixExtRotations(Placed pl)
    {
        if (pl.ExtFrom is null || pl.ExtTo is null) return;
        pl.ExtFrom.Nodes[pl.ExtFromNode].Rotation = MathEx.GetNodeRotation(Sloped(-InwardDirections[pl.ExtFrom][pl.ExtFromNode], GradeOf(pl, pl.ExtFrom)));
        pl.ExtTo.Nodes[pl.ExtToNode].Rotation = MathEx.GetNodeRotation(Sloped(InwardDirections[pl.ExtTo][pl.ExtToNode], GradeOf(pl, pl.ExtTo)));
    }

    public static void FixRotations(Placed pl)
    {
        var p = pl.Plan;
        var rampIn = InwardDirections[pl.RampPrefab][pl.RampNode];
        pl.First.Nodes[pl.FirstIn].Rotation = MathEx.GetNodeRotation(Sloped(InwardDirections[pl.First][pl.FirstIn], GradeOf(pl, pl.First)));
        pl.Last.Nodes[pl.LastOut].Rotation = MathEx.GetNodeRotation(Sloped(-InwardDirections[pl.Last][pl.LastOut], GradeOf(pl, pl.Last)));
        if (p.Kind != "drop") pl.RampPrefab.Nodes[pl.RampNode].Rotation = MathEx.GetNodeRotation(p.Kind == "exit" ? -rampIn : rampIn);
        FixExtRotations(pl);
        if (Environment.GetEnvironmentVariable("OSM2ETS_IPROBE") is not null)
            foreach (var n in new[] { pl.First.Nodes[pl.FirstIn], pl.Last.Nodes[pl.LastOut], pl.RampPrefab.Nodes[pl.RampNode] })
                Console.Error.WriteLine($"   rot {n.Position} right {Vector3.Transform(Vector3.UnitX, n.Rotation)} up {Vector3.Transform(Vector3.UnitY, n.Rotation)}");
    }

    public static void Adjust(Placed pl)
    {
        var p = pl.Plan;
        var inNode = pl.First.Nodes[pl.FirstIn];
        var outNode = pl.Last.Nodes[pl.LastOut];
        var rampNode = pl.RampPrefab.Nodes[pl.RampNode];
        // ppd-Richtungen zeigen in das Prefab hinein
        SetTail(p.MainIn, inNode.Position, InwardDirections[pl.First][pl.FirstIn], Lead, MainBlend);
        SetHead(p.MainOut, outNode.Position, -InwardDirections[pl.Last][pl.LastOut], Lead, MainBlend);
        var rampIn = InwardDirections[pl.RampPrefab][pl.RampNode];
        // Das Prefab zweigt schneller ab als die OSM-Rampe: Rampenpunkte, die noch näher an der
        // Hauptfahrbahn liegen als der Rampenknoten, entfernen (sonst schwenkt die Rampe zurück
        // und kreuzt die Hauptfahrbahn)
        if (p.Kind == "drop") return;
        if (!p.DirectRamp)
        {
            DropInside(p.Ramp, p.Kind == "exit", p.Kind == "exit" ? p.MainOut : p.MainIn, rampNode.Position);
            if (p.Kind == "exit") SetHead(p.Ramp, rampNode.Position, -rampIn, Lead, RampBlend);
            else SetTail(p.Ramp, rampNode.Position, rampIn, p.K == Hw3Merge2 ? ShortRampLead : Lead, RampBlend);
        }
        if (p.ExtRoad is not null && pl.ExtFrom is not null && pl.ExtTo is not null)
        {
            SnapExt(p.ExtRoad, pl.ExtFrom.Nodes[pl.ExtFromNode].Position, pl.ExtTo.Nodes[pl.ExtToNode].Position);
            // schräg stehende Einfahrt (direkt am Spurende-Prefab): Zusatzspur-Straße tangential aus
            // ihrem Ausgang herausführen, sonst Knick und versetzter Querschnitt an der Fuge
            if (p.DirectRamp)
                SetHead(p.ExtRoad, pl.ExtFrom.Nodes[pl.ExtFromNode].Position, -InwardDirections[pl.ExtFrom][pl.ExtFromNode], Lead, MainBlend);
        }

        // Knotendrehungen auf die Fahrtrichtung setzen (aus der ppd-Geometrie). TruckLib
        // liefert für schräge Knoten nach dem Drehen eines Prefabs eine gespiegelte
        // Richtung; die Straßenkurve würde sonst am Knoten abknicken.
        inNode.Rotation = MathEx.GetNodeRotation(Sloped(InwardDirections[pl.First][pl.FirstIn], GradeOf(pl, pl.First)));
        outNode.Rotation = MathEx.GetNodeRotation(Sloped(-InwardDirections[pl.Last][pl.LastOut], GradeOf(pl, pl.Last)));
        if (!p.DirectRamp) rampNode.Rotation = MathEx.GetNodeRotation(p.Kind == "exit" ? -rampIn : rampIn);
        FixExtRotations(pl);
    }
}
