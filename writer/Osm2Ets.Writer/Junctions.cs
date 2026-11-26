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
    /// <summary>Knoten ohne Prefab wegen 2+1-Arm (Lage).</summary>
    public static readonly Dictionary<long, Vector3> Skip21 = [];
    /// <summary>Weggelassene freie Rechtsabbieger (vom Aufrufer aus der Straßenliste zu entfernen).</summary>
    public static readonly HashSet<RoadEntry> BypassDrops = [];
    /// <summary>So weit (m) dürfen beide Enden eines freien Rechtsabbiegers von der Kreuzung entfernt sein.</summary>
    public const float BypassReach = 90f;
    public static readonly Def SPLIT2 = new(new("ch_42029", "/prefab2/fork_temp/ger/ger_r1_split_short.ppd", "default", "ger_1"), [1, 2], [0]);

    // Kreuzung mit Abbiegespur an allen vier Armen (ch_42010 „ger r1 turn x r1 turn tmpl“,
    // prefab.mod_switzerland_reskin.sii; Varianten laut .pmd: default, no_lines; Looks ger_1…ger_5):
    // jeder Arm 2+1 mit zwei Spuren zur Kreuzung hin (ger24 vom Prefab weg), Knoten 0/2 und 1/3 gegenüber.
    public static readonly Def XTURN4 = new(new("ch_42010", "/prefab2/cross_temp/ger/ger_r1_turn_x_r1_turn_tmpl.ppd", "default", "ger_1"), [0, 2], [1, 3]);
    public static bool UseTurnCross = Environment.GetEnvironmentVariable("OSM2ETS_NO_TURNCROSS") != "1";
    // Einmündung mit Linksabbiegespur (ch_14001 „ger r1 turn x r1 t tmpl“, Looks ger_1…ger_5, Varianten
    // default/no_lines): Knoten 0 = Arm mit Abbiegespur (2 hinein, 1 hinaus), Knoten 2 = gegenüberliegender
    // Arm 1+1, Knoten 1 = Nebenstraße 1+1 links der Fahrtrichtung auf Arm 0. Lkw dürfen links abbiegen.
    public static readonly Def TTURN = new(new("ch_14001", "/prefab2/cross_temp/ger/ger_r1_turn_x_r1_t_tmpl.ppd", "default", "ger_1"), [0, 2], [1]);
    public static bool UseTurnT = Environment.GetEnvironmentVariable("OSM2ETS_NO_TURNT") != "1";
    // Gegenüberliegender Arm der Einmündung mit Abbiegespur (TTURN, Knoten 2): Fahrstreifen 4,5 m neben der
    // Mittellinie – gebaut für „ger road 1 plus 1 tmpl“ (ger26, 1+1 mit Sperrfläche in der Mitte). Übergang
    // zur normalen Landstraße: mod_ger_99 „ger fork road 1 x 1 plus 1 tmpl“ (60 m, Varianten clean/no_lines):
    // Knoten 0 = Sperrflächen-Seite (Spuren ±4,5 m), Knoten 1 = Landstraße (±2,25 m).
    // Tankstellen (Kategorie mod_germany/gas_parking, Varianten ger/ger_asp, Looks ger_1…5):
    // gas_small_road_01_ger – Landstraße 1+1 läuft gerade durch (Knoten 0/1 bei x ±69,05, z −13,75,
    // Spuren ±2,25 m), die Anlage liegt links der Fahrt von Knoten 1 nach Knoten 0 (bis 31,5 m hinter
    // der Straßenmitte), 2 Tankpunkte.
    public static readonly Def GASR = new(new("mod_ger_84", "/prefab2/gas_parking/gas_small_road_01_ger.ppd", "ger", "ger_1"), [0, 1], []);
    // Ersatz mit nur einer Zufahrt (Kategorie dlc_balt, Looks default…, Variante default): Knoten 0 =
    // Zufahrt (Hin- und Rückspur ±2,25 m), Knoten 1 ohne Spuren; Anlage 61 × 55 m, links der Einfahrt
    // bis 43 m, rechts 12 m. OSM2ETS_NO_FUEL_BALT=1 schaltet ihn ab.
    public static readonly Def GASB = new(new("dlc_balt_149", "/prefab2/gas_station/gas_universal_03_balt.ppd", "default", "default"), [0, 1], []);
    public static bool UseFuel = Environment.GetEnvironmentVariable("OSM2ETS_NO_FUEL") is null;
    public static bool UseFuelBalt = Environment.GetEnvironmentVariable("OSM2ETS_NO_FUEL_BALT") is null;
    /// <summary>Tankstellen (Umriss, Art) aus dem Zwischenformat, gesetzt vom Aufrufer.</summary>
    public static List<AreaEntry> Stations = [];
    private static readonly List<(long Node, RoadEntry A0, RoadEntry A1)> PendingGasR = [];
    private static readonly List<(long Node, RoadEntry Access)> PendingGasB = [];
    private static long synthGas = -1_500_000;
    private const float GasHalf = 69.05f, GasTail = 25f, GasStraight = 1.5f, GasAccess = 20f, GasNeedPrefab = 45f, GasNeedChange = 26f, GasNeedFree = 20f;
    public static readonly Def HATCH11 = new(new("mod_ger_99", "/prefab2/fork_temp/ger/ger_fork_road_1_x_1_plus_1_tmpl.ppd", "clean", "ger_1"), [0, 1], []);
    private const float TTurnPlainArm = 18.1f;       // Abstand Knoten 2 ↔ Prefab-Mitte (ch_14001)
    private const float HatchTransition = 60f;       // Länge von mod_ger_99
    // Sperrflächen-Straße zwischen Einmündung und Übergang: lang genug, dass sie den Richtungsunterschied
    // zwischen dem geraden Einmündungsarm und dem (nach der Straße ausgerichteten) Übergang sanft ausgleicht
    private const float HatchGap = 20f;
    /// <summary>Straßenlänge ab der Einmündung, die Sperrfläche + Übergang brauchen.</summary>
    public static float HatchNeed => TTurnPlainArm + HatchGap + HatchTransition + MinTail;
    // Universal-Gabelungen (prefab.mod_universal_roads.sii, Looks asph_new_*/asp_rough_*, für „un 2-1“
    // gebaut; Spurraster 4,5 m wie die ger-Prefabs). Nur zum Test (OSM2ETS_UN_SLIP=1): Rechtsabbieger an
    // 2+1-Straßen anschließen statt weglassen.
    // Ausfädeln aus der zweispurigen Richtung: Knoten 0 = zuführender Arm (2 hinein), 1 = weiterführend, 2 = Abzweig.
    public static readonly Def UNDIV = new(new("un_1412a", "/prefab2/fork_temp/un/un_fork_1-2_divide_0-1_45m.ppd", "default", "asph_new_01"), [0, 1], [2]);
    // Einfädeln in die einspurige Richtung: Knoten 2 = zuführender Arm (1 hinein), 0 = weiterführend, 1 = Einfädler.
    public static readonly Def UNMRG = new(new("un_1412d", "/prefab2/fork_temp/un/un_fork_1-2_merge_0-1_45m.ppd", "default", "asph_new_01"), [2, 0], [1]);
    // Flache Abzweigung/Einmündung einer einspurigen Einbahnstraße an einer Landstraße 1+1 (Grundspiel,
    // base.scs; Kategorie dlc_at, Looks ger_1…5, Variante lines_de mit deutscher Markierung; 15°, ~44 m):
    // at_r1_r1_joint_mirrored: Knoten 0 = zuführend, 1 = weiterführend, 2 = Abzweig (rechts).
    public static readonly Def ATDIV = new(new("at_23006", "/prefab2/fork_temp/at/at_r1_r1_joint_mirrored.ppd", "lines_de", "ger_1"), [0, 1], [2]);
    // at_r1_r1_joint: Knoten 2 = zuführend, 0 = weiterführend, 1 = Einfädler (von rechts).
    public static readonly Def ATMRG = new(new("at_2i02j", "/prefab2/fork_temp/at/at_r1_r1_joint.ppd", "lines_de", "ger_1"), [2, 0], [1]);
    public static bool UseSlipJoints = Environment.GetEnvironmentVariable("OSM2ETS_NO_SLIPJOINT") != "1";
    public static bool UseSlipDrop = Environment.GetEnvironmentVariable("OSM2ETS_NO_SLIPDROP") != "1";
    public static bool UseUnSlip = Environment.GetEnvironmentVariable("OSM2ETS_UN_SLIP") == "1";
    private const float UnForkLen = 40, UnForkShort = 10;
    private static bool PlainArm(RunEntry x) => !x.Oneway && (x.Lanes is null or <= 2)
        && (x.LanesForward is null or 1) && (x.LanesBackward is null or 1);

    /// <summary>Zweirichtungs-Arm mit zwei (oder mehr) Spuren zum Knoten hin und einer weg.</summary>
    private static bool TurnArm(RunEntry x, bool atStart)
    {
        int? toward = atStart ? x.LanesBackward : x.LanesForward;
        int? away = atStart ? x.LanesForward : x.LanesBackward;
        return !x.Oneway && toward >= 2 && away == 1;
    }

    // Übergang Landstraße 1+1 ↔ 2+1 (ch_42001 „ger fork road 1 x 2 plus 1 tmpl“, 60 m): Knoten 0 = 2+1
    // (zwei Spuren laufen aus dem Prefab heraus), Knoten 1 = Landstraße. Variante/Look ungeprüft (keine
    // .pmd vorhanden) – "default"/"ger_1" wie bei den übrigen Gabelungen.
    public static readonly Def FORK21 = new(new("ch_42001", "/prefab2/fork_temp/ger/ger_fork_road_1_x_2_plus_1_tmpl_v02.ppd", "default", "ger_1"), [0, 1], []);
    public static bool UseFork21 = Environment.GetEnvironmentVariable("OSM2ETS_NO_FORK21") != "1";
    /// <summary>So weit (m) vom Teilungsknoten wird die 2+1-Straße geteilt; davor liegt die Landstraße.</summary>
    public const double Fork21Near = 45;
    /// <summary>So viel 2+1-Straße (m) muss hinter dem Übergang mindestens bleiben.</summary>
    public const double Fork21Far = 40;
    // Spurende 2+1 → Landstraße (mod_ger_101 „ger fork road 1 x 1 plus 2 tmpl“, 60 m, Variante clean):
    // Knoten 0 = 2+1 (zwei Spuren laufen ins Prefab hinein), Knoten 1 = Landstraße.
    public static readonly Def DROP21 = new(new("mod_ger_101", "/prefab2/fork_temp/ger/ger_fork_road_1_x_1_plus_2_tmpl.ppd", "clean", "ger_1"), [0, 1], []);
    public static bool UseLaneChange21 = Environment.GetEnvironmentVariable("OSM2ETS_NO_LANECHANGE21") != "1";
    private static readonly List<(long Node, RoadEntry Wide, RoadEntry Narrow)> PendingFork21 = [];

    /// <summary>Zweirichtungsabschnitt: 1 = Landstraße 1+1, 2 = 2+1/3+1, 0 = anderes.</summary>
    private static int Class21(RunEntry x)
    {
        if (x.Oneway || IsMinorWay(x)) return 0;
        int f = x.LanesForward ?? 0, b = x.LanesBackward ?? 0;
        if ((f >= 2 && b == 1) || (b >= 2 && f == 1)) return 2;
        if (x.LanesForward is null or 1 && x.LanesBackward is null or 1 && x.Lanes is null or <= 2) return 1;
        return 0;
    }

    /// <summary>Fügt an künstlichen Teilungsknoten wieder zusammen, was nach dem Entfernen von
    /// Einfädelspuren auf beiden Seiten gleich ist (sonst bliebe dort ein Stoß ohne Übergang, an dem
    /// ein kurzes Reststück abknickt).</summary>
    private static void JoinPlainSplits21(List<RoadEntry> roads)
    {
        bool again = true;
        while (again)
        {
            again = false;
            foreach (var r in roads)
            {
                if (r.End?.OsmNode is not long node || node > -2_000_000 || PendingFork21.Any(f => f.Node == node)) continue;
                var others = roads.Where(o => o != r && (o.Start?.OsmNode == node || o.End?.OsmNode == node)).ToList();
                if (others.Count != 1 || others[0].Start?.OsmNode != node || others[0].End?.OsmNode == node) continue;
                var f = others[0];
                if (Class21(r.Runs[^1]) != Class21(f.Runs[0]) || Class21(r.Runs[^1]) == 0) continue;
                int off = r.Points.Count - 1;
                r.Points = r.Points.Concat(f.Points.Skip(1)).ToList();
                r.Runs = r.Runs.Concat(f.Runs.Select(x => x.Span(x.From + off, x.To + off))).ToList();
                r.End = f.End;
                r.LengthM = Interchanges.ArcLengthOf(r);
                roads.Remove(f);
                again = true;
                break;
            }
        }
    }

    /// <summary>
    /// Freie Rechtsabbieger (einspurige Einbahnstraße, die nahe einer Kreuzung/Einmündung flach von einer
    /// Zweirichtungsstraße abzweigt und flach in eine andere einmündet). Lassen sich beide Enden nicht mit
    /// den Abzweig-/Einmündungs-Prefabs (<see cref="ATDIV"/>/<see cref="ATMRG"/>, nur an 1+1) anschließen,
    /// entfällt der Abbieger – die Kreuzung übernimmt das Abbiegen – samt seiner Ausfädelspur (endet am
    /// Abzweig) und Einfädelspur (beginnt an der Einmündung). Die beiden übrigen Straßen an seinen Enden
    /// werden zu einer verbunden. Vor LaneCentering aufrufen (Spurzahlen bestimmen den Versatz).
    /// </summary>
    public static int PrepareSlips(List<RoadEntry> roads, List<string> warnings)
    {
        if (!UseSlipDrop) return 0;
        Dictionary<long, List<(RoadEntry R, bool AtStart)>> Ends()
        {
            var d = new Dictionary<long, List<(RoadEntry R, bool AtStart)>>();
            foreach (var r in roads)
                foreach (var st in new[] { true, false })
                    if ((st ? r.Start : r.End) is { Kind: "node", OsmNode: long id })
                        (d.TryGetValue(id, out var l) ? l : d[id] = []).Add((r, st));
            return d;
        }
        var ends = Ends();
        RunEntry RunAt(RoadEntry r, bool st) => st ? r.Runs[0] : r.Runs[^1];
        // Kreuzungsknoten: mindestens drei Zweirichtungsstraßen
        var junctions = ends.Where(kv => kv.Value.Count(e => !RunAt(e.R, e.AtStart).Oneway && !IsMinorWay(RunAt(e.R, e.AtStart))) >= 3)
                            .Select(kv => (kv.Key, P: P(kv.Value[0].AtStart ? kv.Value[0].R.Points[0] : kv.Value[0].R.Points[^1]))).ToList();
        // Ende eines Abbiegers: genau zwei weitere Straßen, beide zweirichtungs und gegenüber, Abbieger flach
        (RoadEntry A, bool AS, RoadEntry B, bool BS)? Main(RoadEntry slip, long node, bool atStart)
        {
            if (!ends.TryGetValue(node, out var l) || l.Count != 3) return null;
            var m = l.Where(e => e.R != slip).ToList();
            if (m.Count != 2 || m.Any(e => e.R.Closed || RunAt(e.R, e.AtStart).Oneway || IsMinorWay(RunAt(e.R, e.AtStart)))) return null;
            var a0 = Away(m[0].R, m[0].AtStart); var a1 = Away(m[1].R, m[1].AtStart); var sa = AwayWithin(slip, atStart, 12);   // Abbieger biegen ab: Richtung am Knoten
            if (a0.LengthSquared() < 0.5f || a1.LengthSquared() < 0.5f || sa.LengthSquared() < 0.5f) return null;
            if (Vector2.Dot(a0, a1) > -0.8f) return null;
            if (MathF.Max(Vector2.Dot(sa, a0), Vector2.Dot(sa, a1)) < MathF.Cos(50 * MathF.PI / 180)) return null;   // flach
            return (m[0].R, m[0].AtStart, m[1].R, m[1].AtStart);
        }
        bool Joinable((RoadEntry A, bool AS, RoadEntry B, bool BS) m) =>
            UseSlipJoints && Class21(RunAt(m.A, m.AS)) == 1 && Class21(RunAt(m.B, m.BS)) == 1;
        // Einmündungen mit Abbiegespur (wie TTURN in Find): Der gegenüberliegende Arm bekommt Sperrfläche und
        // Übergang (HatchNeed); Abzweige/Einmündungen von Abbiegern in diesem Bereich lassen sich nicht anschließen.
        var hatchBlocked = new HashSet<long>();
        if (UseTurnT)
            foreach (var (node, l) in ends)
            {
                if (l.Count != 3 || l.Any(e => e.R.Closed || RunAt(e.R, e.AtStart).Oneway || IsMinorWay(RunAt(e.R, e.AtStart)))) continue;
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                    {
                        if (i == j) continue;
                        var x = l[i]; var y = l[j]; var sd = l[3 - i - j];
                        if (!TurnArm(RunAt(x.R, x.AtStart), x.AtStart) || !PlainArm(RunAt(y.R, y.AtStart)) || !PlainArm(RunAt(sd.R, sd.AtStart))) continue;
                        var ax = Away(x.R, x.AtStart); var ay = Away(y.R, y.AtStart); var asd = Away(sd.R, sd.AtStart);
                        if (ax.LengthSquared() < 0.5f || ay.LengthSquared() < 0.5f || Vector2.Dot(Vector2.Normalize(ax), Vector2.Normalize(ay)) > -0.8f) continue;
                        var h = -ax; var left = new Vector2(h.Y, -h.X);
                        if (Vector2.Dot(asd, left) <= 0.3f) continue;
                        float dist = 0; var (road, st) = (y.R, y.AtStart);
                        for (int guard = 0; guard < 20; guard++)
                        {
                            dist += (float)Interchanges.ArcLengthOf(road);
                            long? far = st ? road.End?.OsmNode : road.Start?.OsmNode;
                            if (far is not long f || dist >= HatchNeed) break;
                            hatchBlocked.Add(f);
                            if (!ends.TryGetValue(f, out var lf)) break;
                            var next = lf.Where(e => e.R != road && !RunAt(e.R, e.AtStart).Oneway).ToList();
                            if (next.Count != 1) break;
                            (road, st) = (next[0].R, next[0].AtStart);
                        }
                    }
            }
        int dropped = 0, lanes = 0;
        foreach (var r in roads.ToList())
        {
            if (r.Closed || !r.Runs.All(x => x.Oneway && x.Lanes is null or 1)) continue;
            if (Interchanges.ArcLengthOf(r) > 150) continue;
            if (r.Start?.OsmNode is not long a || r.End?.OsmNode is not long b) continue;
            var ma = Main(r, a, true); var mb = Main(r, b, false);
            if (Environment.GetEnvironmentVariable("OSM2ETS_DBG21") == "1") Console.Error.WriteLine($"DBG21 slip {r.Id} a {a} {(ma is null ? "-" : "ok")} b {b} {(mb is null ? "-" : "ok")} ends {ends.GetValueOrDefault(a)?.Count}/{ends.GetValueOrDefault(b)?.Count}");
            if (ma is null || mb is null) continue;
            var pa = P(r.Points[0]); var pb = P(r.Points[^1]);
            if (!junctions.Any(j => j.Key != a && j.Key != b && Vector2.Distance(j.P, pa) < BypassReach && Vector2.Distance(j.P, pb) < BypassReach)) continue;
            bool blocked = hatchBlocked.Contains(a) || hatchBlocked.Contains(b);
            if (Joinable(ma.Value) && Joinable(mb.Value) && !blocked) continue;      // wird mit Prefabs angeschlossen
            roads.Remove(r); dropped++;
            var sa = Away(r, true); var arrive = -Away(r, false);
            // Ausfädelspur: Straße, aus der der Abbieger kommt, verliert zum Abzweig hin eine Spur,
            // wenn die weiterführende Straße in dieser Richtung weniger Spuren hat
            foreach (var (node, m, outward) in new[] { (a, ma.Value, false), (b, mb.Value, true) })
            {
                var dir = outward ? arrive : sa;
                // Straße auf der Seite, aus der der Verkehr kommt (Abzweig) bzw. in die er weiterfährt (Einmündung)
                var cand = new[] { (m.A, m.AS), (m.B, m.BS) };
                var (rr, st) = cand.OrderBy(c => outward ? -Vector2.Dot(Away(c.Item1, c.Item2), dir) : Vector2.Dot(Away(c.Item1, c.Item2), dir)).First();
                var (or, ost) = cand.First(c => c.Item1 != rr || c.Item2 != st);
                var x0 = RunAt(rr, st); var y0 = RunAt(or, ost);
                // Abzweig: rr = zuführend (Spuren zum Knoten), or = weiterführend (Spuren weg)
                // Einmündung: rr = weiterführend (Spuren weg), or = zuführend (Spuren zum Knoten)
                int? Toward(RunEntry x, bool s0) => s0 ? x.LanesBackward : x.LanesForward;
                int? AwayL(RunEntry x, bool s0) => s0 ? x.LanesForward : x.LanesBackward;
                int extra = outward ? (AwayL(x0, st) ?? 1) - (Toward(y0, ost) ?? 1) : (Toward(x0, st) ?? 1) - (AwayL(y0, ost) ?? 1);
                if (extra <= 0) continue;
                var seq = st ? rr.Runs : Enumerable.Reverse(rr.Runs).ToList();
                foreach (var x in seq)
                {
                    bool fwd = outward ? st : !st;           // Richtung des betroffenen Verkehrs relativ zur Straße
                    int? l = fwd ? x.LanesForward : x.LanesBackward;
                    if (l is not >= 2) break;
                    if (fwd) x.LanesForward--; else x.LanesBackward--;
                    if (x.Lanes is int ln) x.Lanes = ln - 1;
                    lanes++;
                }
            }
            warnings.Add(blocked
                ? $"Info: freier Rechtsabbieger {r.Id} weggelassen (Anschluss läge in Sperrfläche/Übergang vor einer Einmündung mit Abbiegespur, die Einmündung übernimmt das Abbiegen)"
                : $"Info: freier Rechtsabbieger {r.Id} weggelassen (kein Prefab zum Anschließen, die Kreuzung übernimmt das Abbiegen)");
            ends = Ends();
            foreach (var node in new[] { a, b }) JoinAt(roads, node);
            ends = Ends();
        }
        if (lanes > 0) warnings.Add($"Info: Aus-/Einfädelspuren weggelassener Rechtsabbieger entfallen ({lanes} Abschnitte)");
        return dropped;
    }

    /// <summary>
    /// Tankstellen (amenity=fuel): Die Wege auf dem Gelände (OSM-Service-Wege, die der Generator als
    /// Zufahrt aufnimmt) werden durch ein Tankstellen-Prefab ersetzt – sonst kann man dort nicht tanken
    /// (Tankpunkte gibt es nur in Prefabs). Vorrang hat <see cref="GASR"/> an einer geraden Landstraße 1+1
    /// (138 m ohne Abzweig, je 25 m Rest) neben der Tankstelle; passt das nicht, <see cref="GASB"/> mit
    /// einer kurzen Zufahrt von einer der bisherigen Anschlussstellen. Sonst bleibt alles wie in OSM.
    /// </summary>
    public static int PrepareFuel(List<RoadEntry> roads, List<string> warnings)
    {
        PendingGasR.Clear(); PendingGasB.Clear();
        if (!UseFuel) return 0;
        int placed = 0;
        foreach (var st in Stations.Where(a => a.Kind == "fuel"))
        {
            var poly = st.Points.Select(p => new Vector2((float)p[0], (float)p[1])).ToList();
            if (poly.Count < 3) continue;
            var c = poly.Aggregate(Vector2.Zero, (a, b) => a + b) / poly.Count;
            // Gelände: Rastanlagen-/Servicefläche um die Tankstelle, sonst ihr Umriss
            var site = Stations.Where(a => a.Kind is "services" or "rest_area")
                .Select(a => a.Points.Select(p => new Vector2((float)p[0], (float)p[1])).ToList())
                .FirstOrDefault(q => q.Count >= 3 && Inside(c, q)) ?? poly;
            bool OnSite(RoadEntry r) => r.Runs.All(x => x.Highway == "service" || IsMinorWay(x))
                && r.Points.Count(p => Inside(new((float)p[0], (float)p[2]), site) || DistToPoly(new((float)p[0], (float)p[2]), site) < 8) * 2 >= r.Points.Count;
            var inner = roads.Where(OnSite).ToList();
            string name = $"Tankstelle {(st.OsmWay > 0 ? "w" : "n")}{Math.Abs(st.OsmWay)}";
            if (inner.Count == 0) { warnings.Add($"Info: {name}: keine Wege auf dem Gelände in der Karte, kein Tankstellen-Prefab"); continue; }
            foreach (var r in inner) roads.Remove(r);
            JoinFreed(roads, inner);
            // 1. Durchfahrt an einer geraden Landstraße
            RoadEntry? best = null; double bestS = 0, bestCost = double.MaxValue;
            foreach (var r in roads)
            {
                if (r.Closed || !r.Runs.All(PlainRun)) continue;
                double len = Interchanges.ArcLengthOf(r);
                if (len < 2 * (GasHalf + GasTail)) continue;
                var (sc, lat) = Project(r, c);
                if (MathF.Abs(lat) > 80) continue;
                for (double sx = GasHalf + GasTail; sx <= len - GasHalf - GasTail; sx += 2)
                {
                    double cost = Math.Abs(sx - sc);
                    if (cost >= bestCost || cost > 60) continue;
                    if (!Straight(r, sx - GasHalf - 5, sx + GasHalf + 5)) continue;
                    best = r; bestS = sx; bestCost = cost;
                }
            }
            if (best is not null)
            {
                var pa = PointAlong(best, true, (float)bestS); var pb = PointAlong(best, true, (float)bestS + 2);
                var d = Vector2.Normalize(pb - pa); var left = new Vector2(d.Y, -d.X);
                bool stationLeft = Vector2.Dot(c - pa, left) > 0;
                long node = synthGas--;
                var far = SplitAtArc(best, bestS, node);
                if (far is not null)
                {
                    far.Id = best.Id + "g"; roads.Add(far);
                    // Anlage links der Fahrt von Knoten 1 nach Knoten 0: Arm 0 in Fahrtrichtung, wenn sie links liegt
                    PendingGasR.Add(stationLeft ? (node, far, best) : (node, best, far));
                    warnings.Add($"Info: {name}: Prefab gas_small_road_01_ger an {best.Id} ({bestCost:0} m längs von der OSM-Lage), {inner.Count} Wege des Geländes ersetzt");
                    placed++;
                    continue;
                }
            }
            // 2. Prefab mit einer Zufahrt: Einmündung irgendwo an der Straße vor dem Gelände, wo das
            //    Kreuzungs-Prefab (TR1, Arme 18 m) Platz hat – die OSM-Anschlüsse liegen oft zu nah an
            //    der nächsten Kreuzung oder an einem Spurwechsel (Aral-Autohof an der B 85: 13 m)
            var acc = UseFuelBalt ? BestAccess(roads, site, c, inner[0]) : null;
            if (acc is null)
            {
                // Gelände wie in OSM: verbundene Anschlussknoten wieder teilen, Wege zurück
                foreach (var r in inner)
                    foreach (var (e, q) in new[] { (r.Start, r.Points[0]), (r.End, r.Points[^1]) })
                        if (e?.OsmNode is long k) Resplit(roads, k, new((float)q[0], (float)q[2]));
                roads.AddRange(inner);
                warnings.Add($"Info: {name}: kein Tankstellen-Prefab (keine gerade Landstraße 1+1 mit 188 m daneben, kein Platz für eine Einmündung vor dem Gelände), Gelände wie in OSM ohne Tankfunktion");
                continue;
            }
            {
                var (host, sAt, dir, via) = acc.Value;
                long at = synthGas--;
                var p = PointAlong(host, true, (float)sAt);
                double y = HeightAt(host, true, (float)sAt);
                var far = SplitAtArc(host, sAt, at);
                if (far is null) continue;
                far.Id = host.Id + "a"; roads.Add(far);
                long node = synthGas--;
                var run = via.Runs[0].Span(0, 2);
                run.Oneway = false;
                var access = new RoadEntry
                {
                    Id = $"gas{Math.Abs(st.OsmWay)}", Chain = via.Chain, Piece = 0, Role = via.Role,
                    Start = new EndInfo { Kind = "node", OsmNode = at }, End = new EndInfo { Kind = "node", OsmNode = node },
                    Points = [[p.X, y, p.Y], [p.X + dir.X * GasAccess / 2, y, p.Y + dir.Y * GasAccess / 2], [p.X + dir.X * GasAccess, y, p.Y + dir.Y * GasAccess]],
                    Runs = [run],
                };
                access.LengthM = GasAccess;
                roads.Add(access);
                PendingGasB.Add((node, access));
                warnings.Add($"Info: {name}: Prefab gas_universal_03_balt mit Einmündung an {host.Id} und {GasAccess:0} m Zufahrt (keine gerade Landstraße für gas_small_road_01_ger), {inner.Count} Wege des Geländes ersetzt");
                placed++;
            }
        }
        return placed;

        static bool PlainRun(RunEntry x) => !x.Oneway && !x.Bridge && !x.Tunnel && !IsMinorWay(x) && x.Highway != "service"
            && (x.Lanes is null or <= 2) && (x.LanesForward is null or 1) && (x.LanesBackward is null or 1)
            && x.Highway is not ("motorway" or "motorway_link" or "trunk_link");
    }

    /// <summary>
    /// Stelle für die Einmündung der Tankstellen-Zufahrt: an einer Zweirichtungsstraße 1+1 vor dem
    /// Gelände (höchstens 25 m entfernt), mit Platz für das Kreuzungs-Prefab nach beiden Seiten – bis
    /// zu einer Kreuzung mit Prefab 45 m, bis zu einem Spurwechsel 26 m, sonst 20 m. Unter den
    /// möglichen Stellen die, bei der die Anlage (links der Einfahrt) am besten auf dem Gelände liegt.
    /// </summary>
    private static (RoadEntry Host, double S, Vector2 Dir, RoadEntry Via)? BestAccess(List<RoadEntry> roads, List<Vector2> site, Vector2 c, RoadEntry via)
    {
        var ends = new Dictionary<long, List<RoadEntry>>();
        foreach (var r in roads)
            foreach (var e in new[] { r.Start, r.End })
                if (e?.OsmNode is long id) (ends.TryGetValue(id, out var l) ? l : ends[id] = []).Add(r);
        float Need(EndInfo? e)
        {
            if (e?.OsmNode is not long id || !ends.TryGetValue(id, out var l)) return GasNeedFree;
            int full = l.Count(r => r.Runs.All(x => !IsMinorWay(x)));
            return l.Count >= 3 && full >= 3 ? GasNeedPrefab : GasNeedFree;
        }
        (RoadEntry, double, Vector2, RoadEntry)? best = null; double bestD = double.MaxValue;
        foreach (var r in roads)
        {
            if (r.Closed || r.Runs.Any(x => x.Oneway || x.Bridge || x.Tunnel || IsMinorWay(x))) continue;
            double len = Interchanges.ArcLengthOf(r);
            // Bogenlängen der Laufgrenzen (für Spurwechsel 2+1 ↔ 1+1)
            var cum = new double[r.Points.Count];
            for (int i = 1; i < r.Points.Count; i++)
                cum[i] = cum[i - 1] + Math.Sqrt(Math.Pow(r.Points[i][0] - r.Points[i - 1][0], 2) + Math.Pow(r.Points[i][2] - r.Points[i - 1][2], 2));
            float needA = Need(r.Start), needB = Need(r.End);
            for (double sx = needA; sx <= len - needB; sx += 1)
            {
                var run = r.Runs.FirstOrDefault(x => cum[x.From] <= sx && sx <= cum[x.To]);
                if (run is null || Class21(run) != 1 || run.Highway == "service") continue;
                // Abstand zum nächsten Laufwechsel mit anderer Spuraufteilung
                bool ok = true;
                foreach (var x in r.Runs)
                {
                    if (Class21(x) == 1) continue;
                    double d = sx < cum[x.From] ? cum[x.From] - sx : sx > cum[x.To] ? sx - cum[x.To] : 0;
                    if (d < GasNeedChange) { ok = false; break; }
                }
                if (!ok) continue;
                var p = PointAlong(r, true, (float)sx);
                if (DistToPoly(p, site) > 25 && !Inside(p, site)) continue;
                var q = PointAlong(r, true, (float)Math.Min(len, sx + 2)) - PointAlong(r, true, (float)Math.Max(0, sx - 2));
                if (q.LengthSquared() < 1) continue;
                var n = Vector2.Normalize(new Vector2(-q.Y, q.X));
                var dir = Vector2.Dot(c - p, n) >= 0 ? n : -n;
                if (Inside(p + dir * GasAccess, site) == false && DistToPoly(p + dir * GasAccess, site) > 5) continue;
                // Mitte der Anlage: 29 m hinter dem Ende der Zufahrt, 15 m links davon
                var body = p + dir * (GasAccess + 29.3f) + new Vector2(dir.Y, -dir.X) * 15.4f;
                double dist = Vector2.Distance(body, c);
                if (dist < bestD) { bestD = dist; best = (r, sx, dir, via); }
            }
        }
        return best;
    }

    /// <summary>Eine Straße, durch deren Inneres der Knoten (Lage p) läuft, dort wieder teilen.</summary>
    private static void Resplit(List<RoadEntry> roads, long node, Vector2 p)
    {
        if (roads.Any(r => r.Start?.OsmNode == node || r.End?.OsmNode == node)) return;
        foreach (var r in roads.ToList())
        {
            if (r.Closed) continue;
            var (sp, _) = Project(r, p);
            if (Vector2.Distance(PointAlong(r, true, (float)sp), p) > 0.5f || sp < 0.5 || sp > Interchanges.ArcLengthOf(r) - 0.5) continue;
            var far = SplitAtArc(r, sp, node);
            if (far is not null) { far.Id = r.Id + "a"; roads.Add(far); }
            return;
        }
    }

    /// <summary>Knoten, an denen nach dem Entfernen der Geländewege nur noch zwei Straßen hängen, verbinden.</summary>
    private static void JoinFreed(List<RoadEntry> roads, List<RoadEntry> removed, long keep = long.MinValue)
    {
        var nodes = removed.SelectMany(r => new[] { r.Start?.OsmNode, r.End?.OsmNode }).OfType<long>().Distinct().ToList();
        foreach (var n in nodes) if (n != keep) JoinAt(roads, n);
    }

    /// <summary>Lage eines Punkts zur Straße: Bogenlänge des Lotfußpunkts und seitlicher Abstand (links +).</summary>
    private static (double S, float Lat) Project(RoadEntry r, Vector2 p)
    {
        double s = 0, bestS = 0; float bestD = float.MaxValue, lat = 0;
        for (int i = 0; i + 1 < r.Points.Count; i++)
        {
            var a = new Vector2((float)r.Points[i][0], (float)r.Points[i][2]); var b = new Vector2((float)r.Points[i + 1][0], (float)r.Points[i + 1][2]);
            var ab = b - a; float l = ab.Length();
            if (l < 1e-3f) continue;
            float u = Math.Clamp(Vector2.Dot(p - a, ab) / (l * l), 0, 1);
            float d = Vector2.Distance(p, a + ab * u);
            if (d < bestD) { bestD = d; bestS = s + u * l; var dir = ab / l; lat = Vector2.Dot(p - a, new Vector2(dir.Y, -dir.X)); }
            s += l;
        }
        return (bestS, lat);
    }

    /// <summary>Straße zwischen den Bogenlängen s0 und s1 höchstens GasStraight m neben ihrer Sehne.</summary>
    private static bool Straight(RoadEntry r, double s0, double s1)
    {
        var a = PointAlong(r, true, (float)s0); var b = PointAlong(r, true, (float)s1);
        var ab = b - a; float l = ab.Length();
        if (l < 1) return false;
        var n = new Vector2(-ab.Y, ab.X) / l;
        for (double s = s0; s <= s1; s += 2)
            if (MathF.Abs(Vector2.Dot(PointAlong(r, true, (float)s) - a, n)) > GasStraight) return false;
        return true;
    }

    private static bool Inside(Vector2 p, List<Vector2> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y + 1e-9f) + poly[i].X)
                inside = !inside;
        return inside;
    }

    private static float DistToPoly(Vector2 p, List<Vector2> poly)
    {
        float best = float.MaxValue;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[j]; var ab = poly[i] - a; float l2 = ab.LengthSquared();
            float u = l2 < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0, 1);
            best = MathF.Min(best, Vector2.Distance(p, a + ab * u));
        }
        return best;
    }

    /// <summary>Verbindet die beiden Zweirichtungsstraßen an einem Knoten zu einer.</summary>
    private static void JoinAt(List<RoadEntry> roads, long node)
    {
        var l = new List<(RoadEntry R, bool AtStart)>();
        foreach (var r in roads)
            foreach (var st in new[] { true, false })
                if ((st ? r.Start : r.End)?.OsmNode == node) l.Add((r, st));
        if (l.Count != 2 || l[0].R == l[1].R || l.Any(e => e.R.Closed || e.R.Runs.Any(x => x.Oneway))) return;
        var (r1, s1) = l[0]; var (r2, s2) = l[1];
        if (s1) Connector.Reverse(r1);           // r1 endet jetzt am Knoten
        if (!s2) Connector.Reverse(r2);          // r2 beginnt jetzt am Knoten
        int off = r1.Points.Count - 1;
        r1.Points = r1.Points.Concat(r2.Points.Skip(1)).ToList();
        r1.Runs = r1.Runs.Concat(r2.Runs.Select(x => x.Span(x.From + off, x.To + off))).ToList();
        r1.End = r2.End;
        r1.LengthM = Interchanges.ArcLengthOf(r1);
        roads.Remove(r2);
    }

    /// <summary>Teilt Zweirichtungsstraßen dort, wo sie innerhalb einer OSM-Linie zwischen 2+1 und
    /// 1+1 wechseln; am neuen Knoten setzt <see cref="Find"/> den Übergang (FORK21 bzw. DROP21).</summary>
    public static int SplitLaneChanges21(List<RoadEntry> roads, bool shift = false)
    {
        if (!UseLaneChange21) return 0;
        int n = 0;
        for (int k = 0; k < roads.Count; k++)
        {
            var r = roads[k];
            if (r.Closed) continue;
            for (int i = 1; i < r.Runs.Count; i++)
            {
                int a = Class21(r.Runs[i - 1]), b = Class21(r.Runs[i]);
                if (a == 0 || b == 0 || a == b) continue;
                double d = 0; var pts = r.Points;
                for (int j = 0; j < r.Runs[i].From && j < pts.Count - 1; j++)
                    d += Math.Sqrt(Math.Pow(pts[j + 1][0] - pts[j][0], 2) + Math.Pow(pts[j + 1][2] - pts[j][2], 2));
                double d0 = d;
                var ra = r.Runs[i - 1]; var rb = r.Runs[i];
                (int? L, int? F, int? B) la = (ra.Lanes, ra.LanesForward, ra.LanesBackward), lb = (rb.Lanes, rb.LanesForward, rb.LanesBackward);
                if (shift)
                {
                    // nur innerhalb der beiden beteiligten Abschnitte verschieben
                    double Arc(int idx) { double acc = 0; for (int j = 0; j < idx && j < pts.Count - 1; j++) acc += Math.Sqrt(Math.Pow(pts[j + 1][0] - pts[j][0], 2) + Math.Pow(pts[j + 1][2] - pts[j][2], 2)); return acc; }
                    d = StraightestSpot(r, d0, Arc(ra.From) + 10, Arc(rb.To) - 10);
                }
                var far = SplitAtArc(r, d, synth21);
                if (far is null) continue;
                far.Id = r.Id + "l";
                // verschobene Wechselstelle: das Stück zwischen alter und neuer Stelle bekommt die Spuren der anderen Seite
                if (d > d0 + 1) foreach (var x in r.Runs.Where(x => Class21(x) == b)) (x.Lanes, x.LanesForward, x.LanesBackward) = la;
                if (d < d0 - 1) foreach (var x in far.Runs.Where(x => Class21(x) == a)) (x.Lanes, x.LanesForward, x.LanesBackward) = lb;
                if (Environment.GetEnvironmentVariable("OSM2ETS_DBG21") == "1") Console.Error.WriteLine($"DBG21 split {r.Id} at {d:0.0} m: near {Interchanges.ArcLengthOf(r):0.0} m [{string.Join(",", r.Runs.Select(q => $"{q.Lanes}/{q.LanesForward}/{q.LanesBackward}"))}] far {Interchanges.ArcLengthOf(far):0.0} m [{string.Join(",", far.Runs.Select(q => $"{q.Lanes}/{q.LanesForward}/{q.LanesBackward}"))}]");
                roads.Add(far); synth21--; n++;
                break;   // der Rest liegt in far und wird später geprüft
            }
        }
        return n;
    }
    private static long synth21 = -2_000_000;

    /// <summary>Wechsel 1+1 ↔ 2+1 ohne Übergang (aus: OSM2ETS_NO_PLAIN21=1 – dann wie bisher).</summary>
    public static bool UsePlain21 = Environment.GetEnvironmentVariable("OSM2ETS_NO_PLAIN21") != "1";
    /// <summary>So viel (m) nimmt ein Kreuzungs-Prefab am anderen Ende eines Stücks mindestens weg
    /// (Knoten–Mitte der kleinsten T-/X-Prefabs, eher zu knapp geschätzt).</summary>
    private const float PlainJunctionReach = 18f;
    /// <summary>Ergebnis von <see cref="PlainLaneChanges"/>: dort setzt <see cref="Find"/> keinen Übergang
    /// (LaneCentering hat die Lage schon darauf eingestellt).</summary>
    public static readonly HashSet<long> Plain21 = [];

    /// <summary>
    /// Knoten mit genau zwei Straßen, an denen 1+1 und 2+1 wechseln und der gerade 60-m-Übergang
    /// (<see cref="FORK21"/>/<see cref="DROP21"/>) sicher nicht gesetzt wird: kein gerader Durchgang, Brückenende
    /// dicht dahinter oder zu wenig Platz bis zu den Prefabs an den anderen Enden (zusammen weniger als
    /// das Prefab mit je einem kurzen Rest). Dort wechselt nur die Straßenvorlage;
    /// <see cref="LaneCentering"/> verschiebt die Linie dort nicht (sonst S-Bogen auf wenigen Metern).
    /// Vor LaneCentering aufrufen; eher zu wenige Knoten melden als einen möglichen Übergang verhindern.
    /// </summary>
    public static HashSet<long> PlainLaneChanges(List<RoadEntry> roads, List<string>? warnings = null)
    {
        var plain = Plain21;
        plain.Clear();
        if (!UsePlain21 || !UseLaneChange21) return plain;
        var ends = new Dictionary<long, List<(RoadEntry R, bool AtStart)>>();
        foreach (var r in roads)
            foreach (var st in new[] { true, false })
                if ((st ? r.Start : r.End) is { Kind: "node", OsmNode: long id })
                    (ends.TryGetValue(id, out var l) ? l : ends[id] = []).Add((r, st));
        const float half = 30f;   // halbe Länge der Übergänge (60 m)
        foreach (var (node, list) in ends)
        {
            if (list.Count != 2 || list.Any(e => e.R.Closed) || list[0].R == list[1].R || PendingFork21.Any(f => f.Node == node)) continue;
            var rr = list.Select(e => e.AtStart ? e.R.Runs[0] : e.R.Runs[^1]).ToList();
            int c0 = Class21(rr[0]), c1 = Class21(rr[1]);
            if (c0 == 0 || c1 == 0 || c0 == c1) continue;
            var a0 = Away(list[0].R, list[0].AtStart); var a1 = Away(list[1].R, list[1].AtStart);
            bool straight = a0.LengthSquared() > 0.5f && a1.LengthSquared() > 0.5f
                            && Vector2.Dot(Vector2.Normalize(a0), Vector2.Normalize(a1)) < -0.85f;
            bool bridge = list.Any(e => DistToBridge(e.R, e.AtStart) is >= 0 and <= 40);
            float Free((RoadEntry R, bool AtStart) e)
            {
                float len = (float)Interchanges.ArcLengthOf(e.R);
                var far = e.AtStart ? e.R.End : e.R.Start;
                if (far is { Kind: "node", OsmNode: long f } && ends.TryGetValue(f, out var fl) && fl.Count >= 3) len -= PlainJunctionReach;
                return len;
            }
            float f0 = Free(list[0]), f1 = Free(list[1]);
            bool room = f0 + f1 >= 2 * (half + MinShortTail);
            if (straight && !bridge && room) continue;
            plain.Add(node);
            string why = !straight ? "kein gerader Durchgang" : bridge ? "Brückenende dicht dahinter"
                : $"Platz bis zu den Nachbar-Prefabs {Math.Max(f0, 0):0} + {Math.Max(f1, 0):0} m, nötig zusammen {2 * (half + MinShortTail):0} m";
            warnings?.Add($"Info: Knoten {node}: Wechsel 2+1 ↔ Landstraße ohne Übergangs-Prefab ({why})");
        }
        return plain;
    }

    /// <summary>Der 60-m-Übergang ist gerade: Wechselstelle bis 40 m verschieben, wo die Straße 30 m
    /// zu beiden Seiten am geradesten läuft (Richtung dahinter weicht am wenigsten von der Sehne ab).</summary>
    private static double StraightestSpot(RoadEntry r, double d0, double lo, double hi)
    {
        var l = r.Points.Select(q => new Vector2((float)q[0], (float)q[2])).ToList();
        float total = 0; for (int i = 1; i < l.Count; i++) total += Vector2.Distance(l[i - 1], l[i]);
        Vector2 At(float s)
        {
            float acc = 0;
            for (int i = 1; i < l.Count; i++)
            {
                float dd = Vector2.Distance(l[i - 1], l[i]);
                if (acc + dd >= s) return l[i - 1] + (l[i] - l[i - 1]) * ((s - acc) / Math.Max(dd, 1e-3f));
                acc += dd;
            }
            return l[^1];
        }
        float Dev(Vector2 a, Vector2 b) => MathF.Abs(Wrap(Ang(a) - Ang(b))) * 180 / MathF.PI;
        double best = d0; float bestScore = float.MaxValue;
        for (int t = -40; t <= 40; t += 5)
        {
            float d = (float)d0 + t;
            if (d - 30 < 8 || d + 30 > total - 8 || (t != 0 && (d < lo || d > hi))) continue;
            var chord = At(d + 30) - At(d - 30);
            float sc = 0.05f * Math.Abs(t);
            if (d + 50 <= total) sc += Dev(At(d + 50) - At(d + 30), chord);
            if (d - 50 >= 0) sc += Dev(At(d - 30) - At(d - 50), chord);
            if (sc < bestScore) { bestScore = sc; best = d; }
        }
        return best;
    }

    /// <summary>
    /// Zweirichtungsstraße mit 2+1 oder 3+1 Spuren (zwei oder mehr vom Knoten weg, eine darauf zu), die
    /// sich an einem Knoten in eine einspurige Einbahnstraße hinein und eine heraus teilt: Für 2+1 gibt
    /// es kein Teilungs-Prefab. Die Straße wird deshalb <see cref="Fork21Near"/> m vom Knoten geteilt;
    /// das Stück am Knoten wird zur Landstraße 1+1 (dort die Teilung <see cref="SPLIT2"/>), am neuen
    /// Knoten sitzt der Übergang <see cref="FORK21"/>. Vor LaneCentering aufrufen (Versätze je Stück).
    /// </summary>
    public static int PrepareFork21(List<RoadEntry> roads, List<string> warnings)
    {
        PendingFork21.Clear();
        if (!UseFork21) return 0;
        var ends = new Dictionary<long, List<(RoadEntry R, bool AtStart)>>();
        foreach (var r in roads)
            foreach (var atStart in new[] { true, false })
            {
                var info = atStart ? r.Start : r.End;
                if (info?.Kind == "node" && info.OsmNode is long id)
                    (ends.TryGetValue(id, out var l) ? l : ends[id] = []).Add((r, atStart));
            }
        foreach (var (node, list) in ends)
        {
            if (list.Count != 3 || list.Any(e => e.R.Closed)) continue;
            var runs = list.Select(e => e.AtStart ? e.R.Runs[0] : e.R.Runs[^1]).ToList();
            if (runs.Count(x => x.Oneway) != 2 || runs.Any(IsMinorWay)) continue;
            int two = runs.FindIndex(x => !x.Oneway);
            int inc = Enumerable.Range(0, 3).FirstOrDefault(i => i != two && !list[i].AtStart, -1);
            int outg = Enumerable.Range(0, 3).FirstOrDefault(i => i != two && list[i].AtStart, -1);
            if (inc < 0 || outg < 0 || runs[inc].Lanes is not (null or 1) || runs[outg].Lanes is not (null or 1)) continue;
            var (t, tStart) = list[two];
            var tr = runs[two];
            int? away = tStart ? tr.LanesForward : tr.LanesBackward;
            int? toward = tStart ? tr.LanesBackward : tr.LanesForward;
            if (away is not >= 2 || toward != 1) continue;
            if (t.Runs.Any(x => x.Oneway)) continue;
            double len = Interchanges.ArcLengthOf(t);
            double d = len >= Fork21Near + Fork21Far ? Fork21Near : len - Fork21Far;
            if (d < 20)
            {
                warnings.Add($"Knoten {node}: {t.Id} ({len:0} m) zu kurz für Teilung und Übergang 2+1 → Landstraße");
                continue;
            }
            if (!tStart) Connector.Reverse(t);
            var far = SplitAtArc(t, d, synth21);
            if (far is null) continue;
            // Stück am Knoten: Landstraße mit je einer Spur
            foreach (var x in t.Runs) { x.Lanes = 2; x.LanesForward = 1; x.LanesBackward = 1; }
            roads.Add(far);
            PendingFork21.Add((synth21, far, t));
            synth21--;
        }
        if (PendingFork21.Count > 0)
            warnings.Add($"Info: {PendingFork21.Count} Teilungen von 2+1-Straßen in zwei Einbahnstraßen: Landstraßen-Stück ({Fork21Near:0} m) und Übergang 2+1");
        return PendingFork21.Count;
    }

    /// <summary>
    /// Gegenüberliegender Arm der Einmündung mit Abbiegespur: Straße mit Sperrfläche (
    /// <see cref="RunEntry.Hatched"/>, Zuordnung "hatched" → ger26) ab der Einmündung, nach <see cref="TTurnPlainArm"/> +
    /// <see cref="HatchGap"/> der Übergang <see cref="HATCH11"/> zur normalen Landstraße. Ist die Straße
    /// dafür zu kurz, bekommt sie ganz die Sperrfläche (Stufe dann an ihrem anderen Ende).
    /// </summary>
    private static void HatchedPlainArm(Plan p, List<RoadEntry> roads, List<Plan> plans, List<string> warnings)
    {
        var arm = p.Arms[1];
        var y = arm.Road;
        bool atStart = y.Start?.OsmNode == p.OsmNode;
        if (!atStart && y.End?.OsmNode != p.OsmNode) return;
        float len = (float)Interchanges.ArcLengthOf(y);
        float split = TTurnPlainArm + HatchGap + HatchTransition / 2;
        RoadEntry hatched = y;
        if (len >= HatchNeed && !y.Closed)
        {
            var far = SplitAtArc(y, atStart ? split : len - split, synth21);
            if (far is not null)
            {
                synth21--;
                far.Id = y.Id + (atStart ? "n" : "h");   // h = Stück mit Sperrfläche, n = Landstraße dahinter
                roads.Add(far);
                // Bezüge anderer Prefabs auf das Ende, das jetzt am neuen Stück hängt
                foreach (var q in plans)
                    for (int i = 0; i < q.Arms.Length; i++)
                        if (q != p && q.Arms[i].Road == y && (far.Start?.OsmNode == q.OsmNode || far.End?.OsmNode == q.OsmNode))
                            q.Arms[i] = new Arm(far, q.Arms[i].Away);
                RoadEntry narrow;
                if (atStart) { hatched = y; narrow = far; }
                else { hatched = far; narrow = y; p.Arms[1] = new Arm(far, arm.Away); }
                long node = (long)far.Start!.OsmNode!;
                bool hStart = hatched.Start?.OsmNode == node, nStart = narrow.Start?.OsmNode == node;
                var aw = new Arm(hatched, Away(hatched, hStart));
                var an = new Arm(narrow, Away(narrow, nStart));
                var anc = MapWriter.ToVector(nStart ? narrow.Points[0] : narrow.Points[^1]);
                var hp = new Plan { OsmNode = node, Anchor = anc, Def = HATCH11, Arms = [aw, an], ForceNodes = [0, 1] };
                plans.Add(hp);
                HatchParent[hp] = p;
            }
        }
        else
            warnings.Add($"Knoten {p.OsmNode}: Arm {y.Id} gegenüber der Abbiegespur zu kurz für Sperrfläche + Übergang ({len:0} m, nötig {HatchNeed:0} m) – ganz mit Sperrfläche");
        foreach (var x in hatched.Runs) x.Hatched = true;
    }

    /// <summary>Teilt <paramref name="r"/> <paramref name="d"/> m nach ihrem Anfang; r behält den
    /// vorderen Teil, zurück kommt der hintere (null, wenn kein sinnvoller Teilungspunkt).</summary>
    private static RoadEntry? SplitAtArc(RoadEntry r, double d, long node)
    {
        var pts = r.Points;
        double acc = 0;
        int i = 0;
        for (; i < pts.Count - 1; i++)
        {
            double sg = Math.Sqrt(Math.Pow(pts[i + 1][0] - pts[i][0], 2) + Math.Pow(pts[i + 1][2] - pts[i][2], 2));
            if (acc + sg >= d) break;
            acc += sg;
        }
        if (i >= pts.Count - 1) return null;
        double seg = Math.Sqrt(Math.Pow(pts[i + 1][0] - pts[i][0], 2) + Math.Pow(pts[i + 1][2] - pts[i][2], 2));
        double t = seg > 0 ? (d - acc) / seg : 0;
        int cut;                        // Index des Teilungspunkts in der neuen Punktliste
        var p = new List<double[]>(pts);
        if (t * seg < 1.5 && i > 0) cut = i;                       // vorhandener Punkt davor
        else if ((1 - t) * seg < 1.5 && i + 1 < pts.Count - 1) cut = i + 1;   // vorhandener Punkt danach
        else
        {
            p.Insert(i + 1, [pts[i][0] + t * (pts[i + 1][0] - pts[i][0]), pts[i][1] + t * (pts[i + 1][1] - pts[i][1]), pts[i][2] + t * (pts[i + 1][2] - pts[i][2])]);
            foreach (var x in r.Runs) { if (x.From > i) x.From++; if (x.To > i) x.To++; }
            cut = i + 1;
        }
        if (cut <= 0 || cut >= p.Count - 1) return null;
        var nearRuns = r.Runs.Where(x => x.From < cut).Select(x => x.Span(x.From, Math.Min(x.To, cut))).ToList();
        var farRuns = r.Runs.Where(x => x.To > cut).Select(x => x.Span(Math.Max(x.From, cut) - cut, x.To - cut)).ToList();
        var far = new RoadEntry
        {
            Id = r.Id + "w", Chain = r.Chain, Piece = r.Piece, Role = r.Role,
            Start = new EndInfo { Kind = "node", OsmNode = node }, End = r.End,
            Points = p.Skip(cut).ToList(), Runs = farRuns,
        };
        r.Points = p.Take(cut + 1).ToList();
        r.Runs = nearRuns;
        r.End = new EndInfo { Kind = "node", OsmNode = node };
        far.LengthM = Interchanges.ArcLengthOf(far);
        r.LengthM = Interchanges.ArcLengthOf(r);
        return far;
    }

    /// <summary>Größte erlaubte Abweichung eines Arms von der Prefab-Richtung (Grad).</summary>
    public const float MaxDeviation = 40f;

    /// <summary>
    /// Arme, die bis zu so viel (Grad) von der Prefab-Richtung abweichen, werden auf einem
    /// Bogen in die Prefab-Richtung umgelegt (spitzwinklige OSM-Einmündungen, Y-Formen).
    /// Sonst liegen die Fahrbahnen auf den ersten Metern übereinander.
    /// </summary>
    public const float MaxBend = 75f;
    /// <summary>So viel Straße (m) muss zwischen Prefab-Knoten und Brückenanfang bleiben.</summary>
    public const float BridgeLead = 14f;
    public const float MaxBridgeShift = 15f;
    public static int BridgeShifted;

    /// <summary>Bogenlänge vom Straßenende bis zum ersten Brückenpunkt (−1 ohne Brücke in 60 m).</summary>
    private static float DistToBridge(RoadEntry r, bool atStart)
    {
        int n = r.Points.Count; float acc = 0;
        for (int k = 0; k < n - 1; k++)
        {
            int i = atStart ? k : n - 1 - k;
            int seg = atStart ? i : i - 1;
            var run = r.Runs.FirstOrDefault(x => x.From <= seg && seg < x.To);
            if (run is { Bridge: true }) return acc;
            int j = atStart ? i + 1 : i - 1;
            acc += Vector2.Distance(P(r.Points[i]), P(r.Points[j]));
            if (acc > 60) break;
        }
        return -1;
    }
    /// <summary>Durchgehende Straße gilt als gerade, wenn ihre Arme höchstens so weit (Grad) von
    /// 180° abweichen; nur dann richtet sich das Prefab allein nach ihr (sonst Mittel aller Arme –
    /// eine abknickende Hauptstraße bog sonst die anschließende Brücke über die Autobahn).</summary>
    public const float StraightThroughDeg = 25f;

    /// <summary>Länge des Bogens je nach Abweichung (m); 0 = gerader Einlauf genügt.</summary>
    /// <summary>Größte Abweichung eines Arms von der Prefab-Knotenrichtung (Grad); sonst ohne Prefab.
    /// OSM2ETS_JUNCTION_MAXDEG überschreibt.</summary>
    public static float MaxArmDeg = float.TryParse(Environment.GetEnvironmentVariable("OSM2ETS_JUNCTION_MAXDEG"),
        System.Globalization.CultureInfo.InvariantCulture, out var md) ? md : 45f;

    public static float BendLength(float deg) => deg < 20 ? 0 : MathF.Min(25 + 1.2f * deg, 110);

    /// <summary>Gerader Einlauf in den Prefab-Knoten (m); kürzer als an Anschlussstellen.</summary>
    public const float Lead = 8f;

    /// <summary>So viel Straße muss hinter dem Prefab-Knoten mindestens übrig bleiben (m).</summary>
    public const float MinTail = 6f;

    public sealed record Arm(RoadEntry Road, Vector2 Away);

    /// <summary>Kürzester Rest (m) eines Arms, dessen anderes Ende frei ist (kein Prefab).</summary>
    public const float MinShortTail = 1.5f;

    private static bool EndsAtPrefab(RoadEntry r, Vector3 anchor)
    {
        var other = StartsAt(r, anchor) ? r.End : r.Start;
        return other?.Kind == "prefab";
    }

    /// <summary>Straßen, deren Richtung wegen Knoten 0 eines Prefabs festliegt (nicht mehr umdrehen).</summary>
    public static readonly HashSet<RoadEntry> Fixed = [];

    /// <summary>Straßenende → bereits vorbereitete Kreuzung und Arm (für direkt verbundene Prefabs).</summary>
    public static readonly Dictionary<RoadEntry, (Plan Plan, int Arm)> PlacedEnds = [];

    /// <summary>Zu kurze Verbindungsstraßen zwischen zwei Kreuzungs-Prefabs; die Prefabs werden direkt verbunden.</summary>
    public static readonly HashSet<RoadEntry> Dropped = [];

    /// <summary>OSM-Knoten mit noch nicht gesetzter Kreuzung (dürfen kurze Arme „übernehmen“).</summary>
    public static readonly HashSet<long> PendingNodes = [];

    public static void Reset() { Fixed.Clear(); PlacedEnds.Clear(); Dropped.Clear(); PendingNodes.Clear(); ShiftedForks.Clear(); ForksShifted = 0; HatchParent.Clear(); }

    /// <summary>Übergang HATCH11 → Einmündung mit Abbiegespur, an deren gegenüberliegendem Arm er liegt.</summary>
    public static readonly Dictionary<Plan, Plan> HatchParent = [];
    /// <summary>Größtes Gefälle (Anteil), nach dem ein Übergang geneigt wird.</summary>
    public const float MaxTransGrade = 0.10f;
    public static bool UseTransPitch = Environment.GetEnvironmentVariable("OSM2ETS_NO_TRANSPITCH") != "1";

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
        public float Grade;                    // Neigung eines Übergangs längs (Anteil, 0 = eben)
    }

    public static List<Plan> Find(List<RoadEntry> roads, Func<RoadEntry, bool, string> roadTypeAt, List<string> warnings)
    {
        Skip21.Clear(); BypassDrops.Clear();
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
            if (runs.Any(IsMinorWay)) continue;   // Feld-/Fußwege usw.: kein Kreuzungs-Prefab
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
            if (UseSlipJoints && list.Count == 3 && oneway == 1)
            {
                int si = runs.FindIndex(x => x.Oneway);
                var slip = list[si].R;
                if (slip.Runs.All(x => x.Oneway && x.Lanes is null or 1))
                {
                    var jp = SlipPlan(roads, slip, node, list[si].AtStart, true);
                    if (jp is not null) { plans.Add(jp); continue; }
                }
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
            if (UseTurnCross && list.Count == 4 && Enumerable.Range(0, 4).All(i => TurnArm(runs[i], list[i].AtStart)))
                def = XTURN4;
            if (UseTurnT && list.Count == 3)
            {
                int side = 3 - best.A - best.B;
                int turn = -1, plain = -1;
                if (TurnArm(runs[best.A], list[best.A].AtStart) && PlainArm(runs[best.B])) (turn, plain) = (best.A, best.B);
                else if (TurnArm(runs[best.B], list[best.B].AtStart) && PlainArm(runs[best.A])) (turn, plain) = (best.B, best.A);
                if (turn >= 0 && PlainArm(runs[side]))
                {
                    var h = -arms[turn].Away;                       // Fahrtrichtung auf dem Abbiegearm zum Knoten
                    var left = new Vector2(h.Y, -h.X);               // x Ost, z Süd
                    if (Vector2.Dot(arms[side].Away, left) > 0.3f)
                    {
                        var anc = MapWriter.ToVector(list[0].AtStart ? list[0].R.Points[0] : list[0].R.Points[^1]);
                        plans.Add(new Plan { OsmNode = node, Anchor = anc, Def = TTURN,
                                             Arms = [arms[turn], arms[plain], arms[side]], ForceNodes = [0, 2, 1] });
                        continue;
                    }
                }
            }
            // 2+1-Arme passen nicht an die schmalen Landstraßen-Prefabs (1+1): dort kein Prefab – die
            // Zweirichtungsstraßen laufen direkt durch, der übrige Arm zweigt daneben ab
            if (def != XTURN4 && Enumerable.Range(0, list.Count).Any(i => roadTypeAt(list[i].R, list[i].AtStart) is "ger24" or "ger25"))
            {
                warnings.Add($"Info: Knoten {node}: 2+1-Straße an der Kreuzung – kein passendes Prefab, Straßen ohne Prefab verbunden");
                Skip21[node] = MapWriter.ToVector(list[0].AtStart ? list[0].R.Points[0] : list[0].R.Points[^1]);
                continue;
            }
            var anchor = MapWriter.ToVector(list[0].AtStart ? list[0].R.Points[0] : list[0].R.Points[^1]);
            // Arme so ordnen: durchgehend A, durchgehend B, dann die übrigen
            var order = new List<int> { best.A, best.B };
            order.AddRange(Enumerable.Range(0, arms.Length).Where(i => i != best.A && i != best.B));
            plans.Add(new Plan { OsmNode = node, Anchor = anchor, Def = def, Arms = order.Select(i => arms[i]).ToArray() });
        }
        // Freie Rechtsabbieger an 2+1-Kreuzungen: einspurige Einbahnstraße, beide Enden an Knoten ohne
        // passendes Prefab (2+1) und nahe derselben Abbiegespur-Kreuzung – ETS2 hat kein Prefab, um sie
        // anzuschließen; die Kreuzung bietet das Abbiegen selbst. Die Rampe entfällt.
        var crosses = plans.Where(x => x.Def == XTURN4).Select(x => x.Anchor).ToList();
        foreach (var r in roads)
        {
            if (r.Closed || !r.Runs.All(x => x.Oneway && x.Lanes is null or 1)) continue;
            if (r.Start?.OsmNode is not long a || r.End?.OsmNode is not long b) continue;
            if (!Skip21.TryGetValue(a, out var pa) || !Skip21.TryGetValue(b, out var pb)) continue;
            if (!crosses.Any(c => Vector3.Distance(c, pa) < BypassReach && Vector3.Distance(c, pb) < BypassReach)) continue;
            BypassDrops.Add(r);
            warnings.Add($"Info: freier Rechtsabbieger {r.Id} an der 2+1-Kreuzung weggelassen (kein Prefab zum Anschließen)");
        }
        // Einfädelspur des weggelassenen Rechtsabbiegers entfällt mit: an seinem Endknoten die Straße, die
        // seine Richtung fortsetzt, verliert in Fahrtrichtung eine Spur (über ihre Läufe, solange ≥ 2)
        foreach (var r in BypassDrops)
        {
            long node = r.End!.OsmNode!.Value;
            var arrive = Away(r, false) * -1;
            RoadEntry? best = null; bool bestStart = false; float bestDot = 0.5f;
            foreach (var o in roads)
            {
                if (o == r || BypassDrops.Contains(o)) continue;
                foreach (var st in new[] { true, false })
                {
                    if ((st ? o.Start : o.End)?.OsmNode != node) continue;
                    var aw = Away(o, st);
                    if (aw.LengthSquared() < 0.5f) continue;
                    float dot = Vector2.Dot(Vector2.Normalize(aw), Vector2.Normalize(arrive));
                    if (dot > bestDot) { bestDot = dot; best = o; bestStart = st; }
                }
            }
            if (best is null) continue;
            var seq = bestStart ? best.Runs : Enumerable.Reverse(best.Runs).ToList();
            int n = 0;
            foreach (var x in seq)
            {
                int? dir = bestStart ? x.LanesForward : x.LanesBackward;
                if (x.Oneway) { if (x.Lanes is not >= 2) break; x.Lanes--; n++; continue; }
                if (dir is not >= 2) break;
                if (bestStart) x.LanesForward--; else x.LanesBackward--;
                if (x.Lanes is int l) x.Lanes = l - 1;
                n++;
            }
            if (n > 0) warnings.Add($"Info: Einfädelspur von {r.Id} auf {best.Id} entfällt ({n} Abschnitte)");
        }
        // Test: Rechtsabbieger mit Universal-Gabelungen anschließen, wenn beide Enden passen
        if (UseUnSlip)
            foreach (var r in roads)
            {
                if (r.Closed || !r.Runs.All(x => x.Oneway && x.Lanes is null or 1)) continue;
                if (r.Start?.OsmNode is not long a || r.End?.OsmNode is not long b) continue;
                if (!Skip21.ContainsKey(a) || !Skip21.ContainsKey(b)) continue;
                var pd = SlipPlan(roads, r, a, true, false);
                var pm = SlipPlan(roads, r, b, false, false);
                if (pd is null || pm is null) { warnings.Add($"Info: Rechtsabbieger {r.Id}: Universal-Gabelung passt nicht (Abzweig {(pd is null ? "nein" : "ja")}, Einfädeln {(pm is null ? "nein" : "ja")})"); continue; }
                BypassDrops.Remove(r);
                plans.Add(pd); plans.Add(pm);
                warnings.Add($"Info: Rechtsabbieger {r.Id} mit Universal-Gabelungen angeschlossen (Test)");
            }
        // Übergänge 2+1 ↔ Landstraße an Knoten mit genau zwei Straßen – erst hier, weil das Entfernen von
        // Einfädelspuren oben die Spurzahlen noch ändert; Wechsel mitten in einem Stück vorher teilen
        if (UseLaneChange21)
        {
            SplitLaneChanges21(roads);
            JoinPlainSplits21(roads);
            var ends2 = new Dictionary<long, List<(RoadEntry R, bool AtStart)>>();
            foreach (var r in roads)
                foreach (var st in new[] { true, false })
                    if ((st ? r.Start : r.End) is { Kind: "node", OsmNode: long id })
                        (ends2.TryGetValue(id, out var l2) ? l2 : ends2[id] = []).Add((r, st));
            foreach (var (node, list) in ends2)
            {
                if (list.Count == 2 && list.All(e => !e.R.Closed) && !PendingFork21.Any(f => f.Node == node) && !Plain21.Contains(node))
                {
                    var rr = list.Select(e => e.AtStart ? e.R.Runs[0] : e.R.Runs[^1]).ToList();
                    int c0 = Class21(rr[0]), c1 = Class21(rr[1]);
                    if (c0 != 0 && c1 != 0 && c0 != c1)
                    {
                        int w = c0 == 2 ? 0 : 1;
                        var (wr, wStart) = list[w]; var x = rr[w];
                        int? away = wStart ? x.LanesForward : x.LanesBackward;
                        var aw = new Arm(wr, Away(wr, wStart)); var an = new Arm(list[1 - w].R, Away(list[1 - w].R, list[1 - w].AtStart));
                        if (aw.Away.LengthSquared() > 0.5f && an.Away.LengthSquared() > 0.5f
                            && Vector2.Dot(Vector2.Normalize(aw.Away), Vector2.Normalize(an.Away)) < -0.85f   // gerade durch (Prefab ist gerade)
                            && list.All(e => DistToBridge(e.R, e.AtStart) is < 0 or > 40))   // nicht an einem Brückenende
                        {
                            var nr = list[1 - w];
                            var anc = MapWriter.ToVector(nr.AtStart ? nr.R.Points[0] : nr.R.Points[^1]);   // Landstraße liegt auf der OSM-Linie
                            plans.Add(new Plan { OsmNode = node, Anchor = anc, Def = away >= 2 ? FORK21 : DROP21, Arms = [aw, an], ForceNodes = [0, 1] });
                        }
                    }
                }
            }
        }
        // Übergänge 2+1 → Landstraße an den künstlichen Knoten aus PrepareFork21
        foreach (var (node, wide, narrow) in PendingFork21)
        {
            if (!roads.Contains(wide) || !roads.Contains(narrow)) continue;
            bool wStart = wide.Start?.OsmNode == node, nStart = narrow.Start?.OsmNode == node;
            var aw = new Arm(wide, Away(wide, wStart));
            var an = new Arm(narrow, Away(narrow, nStart));
            if (aw.Away.LengthSquared() < 0.5f || an.Away.LengthSquared() < 0.5f) continue;
            var anc = MapWriter.ToVector(nStart ? narrow.Points[0] : narrow.Points[^1]);   // Landstraße liegt auf der OSM-Linie
            plans.Add(new Plan { OsmNode = node, Anchor = anc, Def = FORK21, Arms = [aw, an], ForceNodes = [0, 1] });
        }
        foreach (var p in plans.Where(q => q.Def == TTURN).ToList()) HatchedPlainArm(p, roads, plans, warnings);
        foreach (var (node, a0, a1) in PendingGasR)
        {
            if (!roads.Contains(a0) || !roads.Contains(a1)) continue;
            bool s0 = a0.Start?.OsmNode == node, s1 = a1.Start?.OsmNode == node;
            var anc = MapWriter.ToVector(s1 ? a1.Points[0] : a1.Points[^1]);
            plans.Add(new Plan { OsmNode = node, Anchor = anc, Def = GASR, Arms = [new Arm(a0, Away(a0, s0)), new Arm(a1, Away(a1, s1))], ForceNodes = [0, 1] });
        }
        foreach (var (node, acc) in PendingGasB)
        {
            if (!roads.Contains(acc)) continue;
            var anc = MapWriter.ToVector(acc.Points[^1]);
            plans.Add(new Plan { OsmNode = node, Anchor = anc, Def = GASB, Arms = [new Arm(acc, Away(acc, false))], ForceNodes = [0] });
        }
        return plans;
    }

    /// <summary>Wege ohne Lkw-Verkehr (nur L5): track, Fuß-/Rad-/Reitwege, Parkplatzgassen …</summary>
    public static bool IsMinorWay(RunEntry r) =>
        r.Highway is "track" or "path" or "footway" or "cycleway" or "bridleway" or "pedestrian" or "steps"
        || (r.Highway == "service" && r.Service is "parking_aisle" or "driveway" or "drive-through" or "emergency_access");

    public static bool UseForkShift = Environment.GetEnvironmentVariable("OSM2ETS_NO_FORK_SHIFT") is null;
    public static int ForksShifted;
    public const float MaxForkShift = 120f;
    private static readonly HashSet<long> ShiftedForks = [];

    /// <summary>
    /// Gabelung (Stamm und zwei fast parallele Äste, z. B. zweibahnige Rampe teilt sich in zwei
    /// Einbahnstraßen, ch_42029): Am OSM-Knoten liegen die Äste noch fast aufeinander, die Astknoten des
    /// Prefabs aber schon um ihren Abstand auseinander – ein Ast lief in einer S-Kurve zurück. Wie bei
    /// den Ausfahrten wandert die Gabelung entlang der Äste, bis diese so weit auseinander sind wie die
    /// Astknoten. Der Stamm wird auf der Mittellinie zwischen den Ästen bis dorthin verlängert.
    /// </summary>
    private static void ShiftFork(Plan p, Vector3[] local)
    {
        if (!ShiftedForks.Add(p.OsmNode)) return;   // Prepare kann mehrfach laufen
        // nur echte Gabelungen (Einbahn-Äste, z. B. Rampen); an gewöhnlichen Einmündungen von
        // Straßen mit Gegenverkehr bleibt das Prefab am OSM-Knoten (sonst wanderte die Einmündung
        // einer Zufahrt 80 m die Zufahrt entlang und ihr Bogen ging verloren)
        if (p.Arms.Any(a => !(a.Road.Runs[0].Oneway || a.Road.Runs[^1].Oneway))) return;
        int trunkNode = p.Def.Side[0];
        var br = Enumerable.Range(0, local.Length).Where(k => k != trunkNode).ToArray();
        if (br.Length != 2) return;
        var l1 = new Vector2(local[br[0]].X, local[br[0]].Z); var l2 = new Vector2(local[br[1]].X, local[br[1]].Z);
        float w = Vector2.Distance(l1, l2);
        float half = Vector2.Distance(new Vector2(local[trunkNode].X, local[trunkNode].Z), (l1 + l2) / 2) / 2;
        int ia = -1, ib = -1; float best = float.MaxValue;
        for (int i = 0; i < 3; i++) for (int j = i + 1; j < 3; j++)
        {
            float a = MathF.Abs(Wrap(Ang(p.Arms[i].Away) - Ang(p.Arms[j].Away)));
            if (a < best) { best = a; ia = i; ib = j; }
        }
        if (best * 180 / MathF.PI > 45) return;
        int it = 3 - ia - ib;
        var trunk = p.Arms[it].Road; var A = p.Arms[ia].Road; var B = p.Arms[ib].Road;
        if (A == trunk || B == trunk || A == B) return;
        bool aS = StartsAt(A, p.Anchor), bS = StartsAt(B, p.Anchor), tS = StartsAt(trunk, p.Anchor);
        var pa = Line(A, aS); var pb = Line(B, bS);
        float la = Arc(pa), lb = Arc(pb);
        float found = -1;
        for (float sh = 0; sh <= MathF.Min(MaxForkShift, MathF.Min(la, lb) - half - 30); sh += 1f)
        {
            // Astknoten liegen etwa half hinter der Mitte des Prefabs
            var q = PointAt(pa, sh + half);
            if (DistLine(pb, q) >= w) { found = sh; break; }
        }
        if (found < 5) return;
        // Mittellinie zwischen den Ästen vom Knoten bis found (alle 5 m)
        var y0 = p.Anchor.Y;
        var mid = new List<double[]>();
        for (float s0 = 0; ; s0 = MathF.Min(s0 + 5, found))
        {
            var qa = PointAt(pa, s0); var qb = PointAt(pb, ArcAtNearest(pb, qa));
            var m = (qa + qb) / 2;
            float y = HeightAt(A, aS, s0);
            mid.Add([m.X, y, m.Y]);
            if (s0 >= found) break;
        }
        float sb = ArcAtNearest(pb, PointAt(pa, found));
        CutNear(A, aS, found);
        CutNear(B, bS, sb);
        // Äste beginnen jetzt an ihren Schnittpunkten; der neue Knoten liegt auf der Mittellinie:
        // Astanfänge auf den Mittelpunkt ziehen wäre falsch (die Arme enden an den Prefab-Knoten) –
        // die Arme werden später ohnehin auf die Knoten gekürzt
        mid[0] = tS ? trunk.Points[0] : trunk.Points[^1];
        AppendAtNode(trunk, tS, mid);
        var np = mid[^1];
        // Astanfänge an den neuen Knoten heranführen (sonst Lücke zwischen Ast und Knoten)
        Prepend(A, aS, np); Prepend(B, bS, np);
        p.Anchor = new Vector3((float)np[0], (float)np[1], (float)np[2]);
        p.Arms = p.Arms.Select(a => new Arm(a.Road, Away(a.Road, StartsAt(a.Road, p.Anchor)))).ToArray();
        ForksShifted++;
        if (Environment.GetEnvironmentVariable("OSM2ETS_IPROBE") is not null)
            Console.Error.WriteLine($"FORKSHIFT {p.OsmNode} {found:0} m (w {w:0.0}, half {half:0.0}) anchor ({np[0]:0.0},{np[2]:0.0})");
    }

    private static float HeightAt(RoadEntry r, bool fromStart, float s)
    {
        var pts = fromStart ? r.Points : Enumerable.Reverse(r.Points).ToList();
        for (int i = 1; i < pts.Count; i++)
        {
            double d = Dist(pts[i - 1], pts[i]);
            if (s <= d) return (float)(pts[i - 1][1] + (pts[i][1] - pts[i - 1][1]) * (d > 0 ? s / d : 0));
            s -= (float)d;
        }
        return (float)pts[^1][1];
    }

    /// <summary>Setzt am Knoten-Ende einen neuen ersten Punkt davor (gleicher Lauf).</summary>
    private static void Prepend(RoadEntry r, bool atStart, double[] q)
    {
        if (atStart)
        {
            r.Points.Insert(0, (double[])q.Clone());
            foreach (var x in r.Runs) { if (x.From > 0) x.From++; x.To++; }
            r.Runs[0].From = 0;
        }
        else
        {
            r.Points.Add((double[])q.Clone());
            r.Runs[^1].To = r.Points.Count - 1;
        }
    }

    private static List<Vector2> Line(RoadEntry r, bool fromStart)
    {
        var l = r.Points.Select(P).ToList();
        if (!fromStart) l.Reverse();
        return l;
    }

    private static float Arc(List<Vector2> l) { float a = 0; for (int i = 1; i < l.Count; i++) a += Vector2.Distance(l[i - 1], l[i]); return a; }

    private static Vector2 PointAt(List<Vector2> l, float s)
    {
        for (int i = 1; i < l.Count; i++)
        {
            float d = Vector2.Distance(l[i - 1], l[i]);
            if (s <= d) return Vector2.Lerp(l[i - 1], l[i], d > 0 ? s / d : 0);
            s -= d;
        }
        return l[^1];
    }

    private static float DistLine(List<Vector2> l, Vector2 q)
    {
        float best = float.MaxValue;
        for (int i = 1; i < l.Count; i++)
        {
            var a = l[i - 1]; var ab = l[i] - a; float l2 = ab.LengthSquared();
            float t = l2 > 0 ? Math.Clamp(Vector2.Dot(q - a, ab) / l2, 0, 1) : 0;
            best = MathF.Min(best, Vector2.Distance(q, a + ab * t));
        }
        return best;
    }

    private static float ArcAtNearest(List<Vector2> l, Vector2 q)
    {
        float best = float.MaxValue, at = 0, acc = 0;
        for (int i = 1; i < l.Count; i++)
        {
            var a = l[i - 1]; var ab = l[i] - a; float l2 = ab.LengthSquared(), len = MathF.Sqrt(l2);
            float t = l2 > 0 ? Math.Clamp(Vector2.Dot(q - a, ab) / l2, 0, 1) : 0;
            float d = Vector2.Distance(q, a + ab * t);
            if (d < best) { best = d; at = acc + t * len; }
            acc += len;
        }
        return at;
    }

    private static List<double[]> CutNear(RoadEntry r, bool atStart, float s)
    {
        if (!atStart) Reverse(r);
        var pts = r.Points; double acc = 0; int k = 0;
        while (k < pts.Count - 2 && acc + Dist(pts[k], pts[k + 1]) < s) { acc += Dist(pts[k], pts[k + 1]); k++; }
        double seg = Dist(pts[k], pts[k + 1]), t = seg > 0 ? Math.Clamp((s - acc) / seg, 0, 1) : 0;
        var a = pts[k]; var b = pts[k + 1];
        var cut = new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t };
        var piece = pts.Take(k + 1).Append(cut).ToList();
        r.Points = new List<double[]> { cut }.Concat(pts.Skip(k + 1)).ToList();
        r.Runs = r.Runs.Where(x => x.To > k).Select(x => { x.From = Math.Max(0, x.From - k); x.To -= k; return x; }).ToList();
        r.Runs[0].From = 0; r.Runs[^1].To = r.Points.Count - 1;
        if (!atStart) Reverse(r);
        return piece;
    }

    private static void AppendAtNode(RoadEntry r, bool atStart, List<double[]> piece)
    {
        if (!atStart) Reverse(r);
        var add = piece.Skip(1).Reverse().ToList();
        int n = add.Count;
        r.Points = add.Concat(r.Points).ToList();
        foreach (var x in r.Runs) { if (x.From > 0) x.From += n; x.To += n; }
        r.Runs[0].From = 0;
        if (!atStart) Reverse(r);
    }

    private static void Reverse(RoadEntry r)
    {
        int last = r.Points.Count - 1;
        r.Points.Reverse();
        foreach (var x in r.Runs) { int f = x.From; x.From = last - x.To; x.To = last - f; (x.LanesForward, x.LanesBackward) = (x.LanesBackward, x.LanesForward); }
        r.Runs.Reverse();
    }

    private static double Dist(double[] a, double[] b) => Math.Sqrt(Math.Pow(b[0] - a[0], 2) + Math.Pow(b[2] - a[2], 2));

    /// <summary>Plan für eine Universal-Gabelung am Ende eines einspurigen Rechtsabbiegers: Abzweig
    /// (<paramref name="leaving"/>) aus der zweispurigen Richtung oder Einfädeln in die einspurige.</summary>
    private static Plan? SlipPlan(List<RoadEntry> roads, RoadEntry slip, long node, bool leaving, bool at)
    {
        var ends = new List<(RoadEntry R, bool AtStart)>();
        foreach (var o in roads)
            foreach (var st in new[] { true, false })
                if ((st ? o.Start : o.End)?.OsmNode == node) ends.Add((o, st));
        if (ends.Count != 3) return null;
        var main = ends.Where(e => e.R != slip).ToList();
        if (main.Count != 2) return null;
        var sAway = Away(slip, leaving);
        if (sAway.LengthSquared() < 0.5f) return null;
        foreach (var (feed, cont) in new[] { (main[0], main[1]), (main[1], main[0]) })
        {
            var fr = feed.AtStart ? feed.R.Runs[0] : feed.R.Runs[^1];
            var cr = cont.AtStart ? cont.R.Runs[0] : cont.R.Runs[^1];
            if (fr.Oneway || cr.Oneway) continue;
            int? fIn = feed.AtStart ? fr.LanesBackward : fr.LanesForward, fOut = feed.AtStart ? fr.LanesForward : fr.LanesBackward;
            int? cIn = cont.AtStart ? cr.LanesBackward : cr.LanesForward, cOut = cont.AtStart ? cr.LanesForward : cr.LanesBackward;
            fIn ??= 1; fOut ??= 1; cIn ??= 1; cOut ??= 1;
            bool ok = at ? (fIn == 1 && fOut == 1 && cIn == 1 && cOut == 1 && Class21(fr) == 1 && Class21(cr) == 1)
                : leaving ? (fIn >= 2 && cOut >= 2 && fOut == 1 && cIn == 1)
                          : (fIn == 1 && cOut == 1 && fOut >= 2 && cIn >= 2);
            if (!ok) continue;
            var fa = Away(feed.R, feed.AtStart); var ca = Away(cont.R, cont.AtStart);
            if (fa.LengthSquared() < 0.5f || ca.LengthSquared() < 0.5f) continue;
            if (Vector2.Dot(fa, ca) > -0.8f) continue;                   // durchgehend
            var h = -fa;                                                // Fahrtrichtung zum Knoten
            var right = new Vector2(-h.Y, h.X);
            var sDir = leaving ? sAway : sAway;
            if (Vector2.Dot(sDir, right) < 0.15f) continue;              // Abzweig rechts
            if (leaving ? Vector2.Dot(sAway, ca) < 0.5f : Vector2.Dot(sAway, fa) < 0.5f) continue; // spitzer Winkel
            float lf = ArcLen(feed.R), lc = ArcLen(cont.R);
            float longArm = leaving ? lf : lc, shortArm = leaving ? lc : lf;
            if (longArm < UnForkLen || shortArm < UnForkShort || ArcLen(slip) < 2 * UnForkShort) continue;
            var anc = MapWriter.ToVector(feed.AtStart ? feed.R.Points[0] : feed.R.Points[^1]);
            var af = new Arm(feed.R, fa); var ac = new Arm(cont.R, ca); var asl = new Arm(slip, sAway);
            return leaving
                ? new Plan { OsmNode = node, Anchor = anc, Def = at ? ATDIV : UNDIV, Arms = [af, ac, asl], ForceNodes = [0, 1, 2] }
                : new Plan { OsmNode = node, Anchor = anc, Def = at ? ATMRG : UNMRG, Arms = [af, ac, asl], ForceNodes = [2, 0, 1] };
        }
        return null;
    }
    private static float ArcLen(RoadEntry r)
    {
        float s = 0;
        for (int i = 1; i < r.Points.Count; i++)
            s += Vector2.Distance(new((float)r.Points[i - 1][0], (float)r.Points[i - 1][2]), new((float)r.Points[i][0], (float)r.Points[i][2]));
        return s;
    }

    /// <summary>Bezugspunkt des Prefabs, der auf den OSM-Knoten gelegt wird: Kreuzungsmitte; bei den
    /// Universal-Gabelungen die Stelle an der Hauptstraße, wo der Abzweig ansetzt (8 m vom Ende an dessen Seite).</summary>
    private static Vector3 PlanCenter(Plan p, IReadOnlyList<Vector3> pos, Quaternion rot)
    {
        if (p.Def == FSPLIT || p.Def == FJOIN || p.Def == SPLIT2) return (pos[0] + pos[1] + pos[2]) / 3;
        if (p.Def == GASR) return (pos[0] + pos[1]) / 2;
        if (p.Def == GASB) return pos[0];
        if (p.Def == FORK21 || p.Def == DROP21 || p.Def == HATCH11)
        {
            // längs: Mitte des Übergangs; quer: auf der Achse der Landstraße (Knoten 1). Die 2+1-Seite
            // (Knoten 0) liegt im Prefab schon um eine halbe Spur seitlich – wie die versetzte 2+1-Straße.
            // Querabstand Knoten 1 ↔ Mitte in Prefab-Koordinaten (x): ch_42001 (0,25−2,50)/2, mod_ger_101 (0,25+2,25)/2,
            // mod_ger_99 (−2,25+2,375)/2
            float lx = p.Def == FORK21 ? (0.254f - 2.5f) / 2 : p.Def == DROP21 ? (0.25f + 2.25f) / 2 : (-2.25f + 2.375f) / 2;
            var xw = Vector3.Transform(Vector3.UnitX, rot); xw.Y = 0;
            return (pos[0] + pos[1]) / 2 + Vector3.Normalize(xw) * lx;
        }
        if (p.Def == UNDIV || p.Def == UNMRG || p.Def == ATDIV || p.Def == ATMRG)
        {
            var near = pos[p.Def == UNDIV || p.Def == ATDIV ? 1 : 2]; var far = pos[0];
            return near + Vector3.Normalize(far - near) * 8f;
        }
        return (pos[p.Def.Through[0]] + pos[p.Def.Through[1]]) / 2;
    }

    private static Vector2 Away(RoadEntry r, bool atStart) => AwayWithin(r, atStart, 40);

    /// <summary>Abweichung (Grad) eines Arms von der Prefab-Knotenrichtung für die Bogenlänge:
    /// die kleinere aus Richtung über 40 m und über die ersten 12 m. Biegt eine Straße direkt
    /// hinter der Einmündung eng ab (Haarnadel einer Zufahrt), passt ihr Anfang zum Prefab –
    /// dann kein langer Ausgleichsbogen, der die Haarnadel durch einen weiten Bogen ersetzt.</summary>
    private static float ArmDev(Arm a, Vector2 outDir, float rot, Vector3 anchor)
    {
        float Dev(Vector2 v) => v == Vector2.Zero ? 999f : MathF.Abs(Wrap(Ang(v) - Ang(outDir) - rot)) * 180 / MathF.PI;
        var near = AwayWithin(a.Road, StartsAt(a.Road, anchor), 12);
        return MathF.Min(Dev(a.Away), Dev(near));
    }

    /// <summary>Zwischenpunkte einfügen, damit kein Abschnitt länger als <paramref name="max"/> m ist
    /// (Laufgrenzen werden mitverschoben).</summary>
    private static void Densify(RoadEntry r, float max)
    {
        var pts = r.Points;
        for (int i = pts.Count - 2; i >= 0; i--)
        {
            double len = Math.Sqrt(Math.Pow(pts[i + 1][0] - pts[i][0], 2) + Math.Pow(pts[i + 1][2] - pts[i][2], 2));
            int k = (int)Math.Ceiling(len / max) - 1;
            if (k <= 0) continue;
            var a = pts[i]; var b = pts[i + 1];
            var add = Enumerable.Range(1, k).Select(j => { double t = j / (double)(k + 1); return new[] { a[0] + t * (b[0] - a[0]), a[1] + t * (b[1] - a[1]), a[2] + t * (b[2] - a[2]) }; }).ToList();
            pts.InsertRange(i + 1, add);
            foreach (var x in r.Runs) { if (x.From > i) x.From += k; if (x.To > i) x.To += k; }
        }
    }

    /// <summary>Punkt <paramref name="s"/> m entlang der Straße vom genannten Ende.</summary>
    private static Vector2 PointAlong(RoadEntry r, bool atStart, float s)
    {
        var l = r.Points.Select(P).ToList();
        if (!atStart) l.Reverse();
        float acc = 0;
        for (int i = 1; i < l.Count; i++)
        {
            float d = Vector2.Distance(l[i - 1], l[i]);
            if (acc + d >= s) return l[i - 1] + (l[i] - l[i - 1]) * ((s - acc) / d);
            acc += d;
        }
        return l[^1];
    }

    private static Vector2 AwayWithin(RoadEntry r, bool atStart, float dist)
    {
        var p = r.Points;
        Vector2 a = atStart ? P(p[0]) : P(p[^1]);
        int i = atStart ? 1 : p.Count - 2;
        Vector2 b = a;
        while (i >= 0 && i < p.Count)
        {
            b = P(p[i]);
            if (Vector2.Distance(a, b) > dist) break;
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
        if (UseForkShift && p.Arms.Length == 3 && p.Def.Side.Length == 1 && p.Def.Through.Length == 2)
            ShiftFork(p, local);

        // Kandidaten: durchgehende Arme auf Through[0]/[1] (beide Richtungen),
        // Seitenarme auf die Side-Knoten (alle Zuordnungen)
        var sideArms = p.Arms.Skip(2).ToArray();
        var sidePerms = Permutations(p.Def.Side.Length).ToList();
        float bestErr = float.MaxValue;
        ushort[]? bestNodes = null; float bestRot = 0;
        // Tankstelle mit einer Zufahrt: Knoten 0 an die Zufahrt, Anlage geradeaus dahinter
        if (p.Def == GASB) { bestNodes = [0]; bestRot = Wrap(Ang(p.Arms[0].Away) - Ang(Out(0))); }
        else
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
                // Kreuzung/Einmündung mit durchgehender Straße: Prefab nur nach den beiden
                // durchgehenden Armen drehen – sonst zöge ein schräger Seitenarm die gerade
                // Hauptstraße in ein S (St 2183 an der Zufahrt zur A 70)
                if (p.Def.Through.Length == 2 && p.Def.Side.Length >= 1 && (p.ForceNodes is null || p.Def == TTURN || p.Def == ATDIV || p.Def == ATMRG)
                    && Vector2.Dot(p.Arms[0].Away, p.Arms[1].Away) < -MathF.Cos(StraightThroughDeg * MathF.PI / 180))
                {
                    float tx = 0, ty = 0;
                    for (int i = 0; i < 2; i++)
                    {
                        float d = Ang(p.Arms[i].Away) - Ang(Out(nodes[i]));
                        tx += MathF.Cos(d); ty += MathF.Sin(d);
                    }
                    rot = MathF.Atan2(ty, tx);
                }
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
        // Übergang 2+1 ↔ Landstraße (gerade, 60 m): nach der Sehne zwischen den Punkten 30 m weit auf
        // beiden Straßen ausrichten und Knoten 1 auf die Landstraße legen – in Kurven läge das Prefab
        // sonst neben der Straße und die Landstraße knickte am Knoten ab
        Vector2? trans1 = null; int transIdx = 1;
        bool isTrans = p.Def == FORK21 || p.Def == DROP21 || p.Def == HATCH11 || p.Def == GASR;
        if (isTrans)
        {
            Densify(p.Arms[0].Road, 8); Densify(p.Arms[1].Road, 8);   // Platz für den Ausgleichsbogen
            var lp = Interchanges.CalibrateNodes(ppd, unit, Quaternion.Identity);
            float half = Vector2.Distance(new(lp[0].X, lp[0].Z), new(lp[1].X, lp[1].Z)) / 2;
            bool chordOk = Interchanges.ArcLengthOf(p.Arms[0].Road) >= half + 25 && Interchanges.ArcLengthOf(p.Arms[1].Road) >= half + 25;
            // Übergang hinter einer schon gesetzten Einmündung mit Abbiegespur: deren gegenüberliegender Arm
            // liegt fest. Die Sperrflächen-Straße (HatchGap lang) läuft von dort als gleichmäßiger Bogen zum
            // Übergang; dessen Richtung so wählen, dass sein Landstraßen-Ende auf der OSM-Straße liegt
            // (gerade Verlängerung des Arms läge in Kurven meterweit daneben, die Landstraße schlüge dann
            // einen S-Bogen zurück)
            Plan? tp = null;
            if (p.Def == HATCH11 && HatchParent.TryGetValue(p, out tp) && tp.NodePos.Length > 0)
            {
                int tn = tp.NodeOf[1];
                var outT = -new Vector2(tp.Inward[tn].X, tp.Inward[tn].Z);
                if (outT.LengthSquared() > 0.01f)
                {
                    outT = Vector2.Normalize(outT);
                    var pT = new Vector2(tp.NodePos[tn].X, tp.NodePos[tn].Z);
                    float aT = Ang(outT);
                    int n0 = p.NodeOf[0], n1 = p.NodeOf[1];
                    var o = new Vector2(lp[n1].X - lp[n0].X, lp[n1].Z - lp[n0].Z);
                    bool s1 = StartsAt(p.Arms[1].Road, p.Anchor);
                    float reach = MathF.Min((float)Interchanges.ArcLengthOf(p.Arms[1].Road), 2 * half + 20);
                    var samples = new List<Vector2>();
                    for (float s = 0; s <= reach; s += 0.5f) samples.Add(PointAlong(p.Arms[1].Road, s1, s));
                    float bestD = 0, bestCost = float.MaxValue;
                    for (float dd = -20; dd <= 20.001f && samples.Count > 0; dd += 0.25f)
                    {
                        float d = dd * MathF.PI / 180, aH = aT + d, am = aT + d / 2;
                        var n0w = pT + HatchGap * new Vector2(MathF.Cos(am), MathF.Sin(am));
                        float rot = Wrap(Ang(-new Vector2(MathF.Cos(aH), MathF.Sin(aH))) - Ang(Out(n0)));
                        float ao = Ang(o) + rot;
                        var n1w = n0w + o.Length() * new Vector2(MathF.Cos(ao), MathF.Sin(ao));
                        float cost = samples.Min(q => Vector2.Distance(q, n1w)) + 0.01f * MathF.Abs(dd);
                        if (cost < bestCost) { bestCost = cost; bestD = d; }
                    }
                    float bm = aT + bestD / 2, bh = aT + bestD;
                    trans1 = pT + HatchGap * new Vector2(MathF.Cos(bm), MathF.Sin(bm));
                    transIdx = n0;
                    bestRot = Wrap(Ang(-new Vector2(MathF.Cos(bh), MathF.Sin(bh))) - Ang(Out(n0)));
                    if (Environment.GetEnvironmentVariable("OSM2ETS_DBG21") == "1") Console.Error.WriteLine($"DBG21 hatch {p.OsmNode} bogen {bestD * 180 / MathF.PI:0.00} abstand {bestCost:0.00}");
                }
            }
            else if (chordOk)
            {
                bool s0 = StartsAt(p.Arms[0].Road, p.Anchor), s1 = StartsAt(p.Arms[1].Road, p.Anchor);
                var w = PointAlong(p.Arms[0].Road, s0, half);
                var nn = PointAlong(p.Arms[1].Road, s1, half);
                // Brücke kurz hinter dem Prefab-Ende: dort lässt sich die Straße nicht mehr biegen –
                // Prefab nach diesem Arm ausrichten, der andere Arm gleicht mit einem Bogen aus
                int keep = -1;
                for (int k = 0; k < 2; k++)
                {
                    float db = DistToBridge(p.Arms[k].Road, k == 0 ? s0 : s1);
                    if (db >= 0 && db < half + 45) { keep = k; break; }
                }
                if (Environment.GetEnvironmentVariable("OSM2ETS_DBG21") == "1") Console.Error.WriteLine($"DBG21 orient {p.OsmNode} keep {keep} db0 {DistToBridge(p.Arms[0].Road, s0):0} db1 {DistToBridge(p.Arms[1].Road, s1):0} arc0 {Interchanges.ArcLengthOf(p.Arms[0].Road):0} arc1 {Interchanges.ArcLengthOf(p.Arms[1].Road):0} pts1 {p.Arms[1].Road.Points.Count}");
                if (keep >= 0)
                {
                    var road = p.Arms[keep].Road; bool st = keep == 0 ? s0 : s1;
                    var pk = keep == 0 ? w : nn;
                    var dk = PointAlong(road, st, half + 15) - pk;
                    if (dk.LengthSquared() > 1)
                    {
                        bestRot = Wrap(Ang(dk) - Ang(Out(p.NodeOf[keep])));
                        trans1 = pk; transIdx = p.NodeOf[keep];
                    }
                }
                else
                {
                    var lc = new Vector2(lp[0].X - lp[1].X, lp[0].Z - lp[1].Z);
                    bestRot = Wrap(Ang(w - nn) - Ang(lc));
                    trans1 = nn; transIdx = 1;
                }
            }
        }
        // TruckLib: am Ursprungsknoten muss eine Straße mit ihrem ENDE anschließen. Ursprung
        // daher auf einen Arm legen, der am Prefab endet (oder sich umdrehen lässt).
        int originArm = Enumerable.Range(0, p.Arms.Length).FirstOrDefault(i => !StartsAt(p.Arms[i].Road, p.Anchor), -1);
        // Einmündung mit Abbiegespur: Ursprung lieber an der Nebenstraße oder am Abbiegearm – der
        // gegenüberliegende Arm führt zum Übergang HATCH11, der ihn als Ursprung braucht, wenn seine
        // Landstraßen-Seite schon an einem anderen Übergang festliegt
        if (p.Def == TTURN)
            originArm = new[] { 2, 0, 1 }.Where(i => !StartsAt(p.Arms[i].Road, p.Anchor)).DefaultIfEmpty(-1).First();
        // Übergang 2+1 ↔ Landstraße: Ursprung an der Landstraße, damit die 2+1-Straße sich in ihrer Kette
        // noch umdrehen lässt (sonst bleibt sie am anderen Ende ungepaart und wird als Abzweig gekürzt)
        if ((p.Def == FORK21 || p.Def == DROP21 || p.Def == HATCH11 || p.Def == GASR) && (!StartsAt(p.Arms[1].Road, p.Anchor) || !Fixed.Contains(p.Arms[1].Road)))
            originArm = 1;
        if (originArm < 0)
            originArm = Enumerable.Range(0, p.Arms.Length).FirstOrDefault(i =>
                !Fixed.Contains(p.Arms[i].Road) && p.Arms[i].Road.Runs.All(x => !x.Oneway), -1);
        if (originArm < 0) { reason = "alle Arme beginnen am Prefab und lassen sich nicht umdrehen"; return false; }
        p.Origin = p.NodeOf[originArm];
        p.Bend = new float[p.Arms.Length];
        for (int i = 0; i < p.Arms.Length; i++)
            p.Bend[i] = BendLength(ArmDev(p.Arms[i], Out(p.NodeOf[i]), bestRot, p.Anchor));
        // Übergang: das gerade Prefab endet 30 m in der Straße – auch kleine Winkel dort mit einem Bogen
        // ausgleichen (sonst sichtbarer Knick am Prefab-Ende)
        if (isTrans)
            for (int i = 0; i < 2; i++)
            {
                var r = p.Arms[i].Road; bool st = StartsAt(r, p.Anchor);
                var lp = Interchanges.CalibrateNodes(ppd, unit, Quaternion.Identity);
                float half = Vector2.Distance(new(lp[0].X, lp[0].Z), new(lp[1].X, lp[1].Z)) / 2;
                var dir = PointAlong(r, st, half + 20) - PointAlong(r, st, half);
                if (dir.LengthSquared() < 1) continue;
                float dev = MathF.Abs(Wrap(Ang(dir) - Ang(Out(p.NodeOf[i])) - bestRot)) * 180 / MathF.PI;
                if (dev > 2) p.Bend[i] = MathF.Max(p.Bend[i], MathF.Min(15 + 1.5f * dev, 60));
                if (Environment.GetEnvironmentVariable("OSM2ETS_DBG21") == "1") Console.Error.WriteLine($"DBG21 bend {p.OsmNode} arm {i} {r.Id} dev {dev:0.0} bend {p.Bend[i]:0}");
            }

        // Kurze Verbindung zu einer schon gesetzten Kreuzung: Prefabs direkt aneinandersetzen
        var localPos = Interchanges.CalibrateNodes(ppd, unit, Quaternion.Identity);
        var localCenter = PlanCenter(p, localPos, Quaternion.Identity);
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
        // Tankstelle mit einer Zufahrt, direkt am Kreuzungs-Prefab: Ursprung auf Knoten 1 (ohne Spuren,
        // dort hängt keine Straße)
        if (p.Def == GASB && p.SnapArm >= 0) p.Origin = 1;
        else if (p.SnapArm >= 0 && p.SnapArm == originArm)
        {
            int alt = Enumerable.Range(0, p.Arms.Length).FirstOrDefault(i => i != p.SnapArm &&
                (!StartsAt(p.Arms[i].Road, p.Anchor) || (!Fixed.Contains(p.Arms[i].Road) && p.Arms[i].Road.Runs.All(x => !x.Oneway))), -1);
            if (alt < 0) { reason = "kein Ursprungsknoten neben der direkten Prefab-Verbindung"; return false; }
            originArm = alt; p.Origin = p.NodeOf[alt];
        }
        if (p.SnapArm >= 0)
            for (int i = 0; i < p.Arms.Length; i++)   // Bögen zur neuen Drehung neu bestimmen
                if (i != p.SnapArm)
                    p.Bend[i] = BendLength(ArmDev(p.Arms[i], Out(p.NodeOf[i]), bestRot, p.Anchor));

        // Passt ein Arm im Winkel gar nicht zum Prefab (z. B. spitz einmündende Rampe an einem
        // T-Prefab), müsste die Straße auf bis zu 110 m in einem Bogen zum Prefab-Knoten umgelegt
        // werden – bei kurzen Straßen fast ganz, weit neben ihrer OSM-Lage (Rampe an der B 85: 45 m).
        // Dann lieber ohne Prefab.
        for (int i = 0; i < p.Arms.Length; i++)
        {
            if (i == p.SnapArm) continue;
            float e = ArmDev(p.Arms[i], Out(p.NodeOf[i]), bestRot, p.Anchor);
            if (e > MaxArmDeg)
            { reason = $"{p.Arms[i].Road.Id} weicht {e:0}° von der Prefab-Richtung ab (höchstens {MaxArmDeg:0}°)"; return false; }
        }

        // Drehung als Quaternion: lokalen Ausgang von Knoten 0 auf die Zielrichtung drehen
        var v0 = Out(p.NodeOf[0]);
        var target = new Vector2(MathF.Cos(Ang(v0) + bestRot), MathF.Sin(Ang(v0) + bestRot));
        p.Rotation = Interchanges.YawTo(ppd, unit, new Vector3(v0.X, 0, v0.Y), new Vector3(target.X, 0, target.Y));
        var placed = Interchanges.CalibrateNodes(ppd, unit, p.Rotation);
        // Übergang (gerade, 60 m) am Hang: nach dem Gefälle der Straße zwischen seinen Enden neigen. Eben
        // gesetzt lag bei 6 % ein Ende 1,8 m unter, das andere 1,8 m über der Straße – steile Stücke davor
        // und dahinter. Nicht, wenn er direkt an einem anderen Prefab hängt (gemeinsamer Knoten).
        p.Grade = 0;
        if (isTrans && p.SnapArm < 0 && UseTransPitch)
        {
            int k0 = p.NodeOf[0], k1 = p.NodeOf[1];
            var run = placed[k1] - placed[k0]; run.Y = 0;
            float len = run.Length(), r0 = len / 2;
            float h0 = HeightAt(p.Arms[0].Road, StartsAt(p.Arms[0].Road, p.Anchor), r0);
            float h1 = HeightAt(p.Arms[1].Road, StartsAt(p.Arms[1].Road, p.Anchor), r0);
            float grade = len > 1 ? Math.Clamp((h1 - h0) / len, -MaxTransGrade, MaxTransGrade) : 0;
            if (Environment.GetEnvironmentVariable("OSM2ETS_DBG21") == "1") Console.Error.WriteLine($"DBG21 grade {p.OsmNode} {p.Def.Prefab.Unit} h0 {h0:0.00} h1 {h1:0.00} anchor {p.Anchor.Y:0.00} grade {grade * 100:0.0} %");
            if (MathF.Abs(grade) > 0.005f)
            {
                var axis = Vector3.Normalize(Vector3.Cross(Vector3.Normalize(run), Vector3.UnitY));
                foreach (var sgn in new[] { 1f, -1f })
                {
                    var q = Quaternion.Normalize(Quaternion.Concatenate(p.Rotation, Quaternion.CreateFromAxisAngle(axis, sgn * MathF.Atan(grade))));
                    var pl = Interchanges.CalibrateNodes(ppd, unit, q);
                    var d = pl[k1] - pl[k0]; float rise = d.Y; d.Y = 0;
                    if (MathF.Abs(rise / d.Length() - grade) < 0.2f * MathF.Abs(grade) + 1e-3f) { p.Rotation = q; placed = pl; p.Grade = grade; break; }
                }
            }
        }
        // Kreuzungsmitte = Mitte der beiden durchgehenden Knoten
        var center = PlanCenter(p, placed, p.Rotation);
        var shift = p.SnapArm >= 0
            ? p.SnapTo.Plan.NodePos[p.SnapTo.Plan.NodeOf[p.SnapTo.Arm]] - placed[p.NodeOf[p.SnapArm]]
            : trans1 is Vector2 t1 ? new Vector3(t1.X, p.Anchor.Y, t1.Y) - new Vector3(placed[transIdx].X, center.Y, placed[transIdx].Z)
            : p.Anchor - center;
        // Brücke dicht hinter der Kreuzung: Prefab von ihr weg verschieben, damit zwischen
        // Prefab-Knoten und Brückenanfang Platz für deren Verlängerung über die untere Straße bleibt
        // (sonst stand das Widerlager auf der Autobahn)
        if (p.SnapArm < 0)
            for (int i = 0; i < p.Arms.Length; i++)
            {
                float db = DistToBridge(p.Arms[i].Road, StartsAt(p.Arms[i].Road, p.Anchor));
                if (db < 0) continue;
                var nl = placed[p.NodeOf[i]] - center; nl.Y = 0;
                float deficit = nl.Length() + BridgeLead - db;
                if (deficit <= 0) continue;
                deficit = MathF.Min(deficit, MaxBridgeShift);
                shift -= new Vector3(p.Arms[i].Away.X, 0, p.Arms[i].Away.Y) * deficit;
                BridgeShifted++;
            }
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
            // kurzer Arm, am anderen Ende kein Prefab: ein kurzes Reststück genügt (ohne Bogen)
            if (arc < r + MinTail && arc >= r + MinShortTail && !EndsAtPrefab(p.Arms[i].Road, p.Anchor))
            { p.Bend[i] = 0; continue; }
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
            if (isTrans && Environment.GetEnvironmentVariable("OSM2ETS_DBG21") == "1") Console.Error.WriteLine($"DBG21 attach {p.OsmNode} arm {i} {a.Road.Id} lead {lead:0.0} bend {p.Bend[i]:0} pts {a.Road.Points.Count} arc {Interchanges.ArcLengthOf(a.Road):0}");
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
