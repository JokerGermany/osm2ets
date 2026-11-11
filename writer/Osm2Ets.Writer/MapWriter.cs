// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using TruckLib;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

public sealed record WriteResult(
    int RoadsWritten,
    int RoadsSkipped,
    int Items,
    int StyleChanges,
    Vector3 StartPosition,
    string? StartRoad,
    List<string> Warnings,
    int Connections = 0,
    int Reversed = 0,
    int TerrainClamped = 0,
    List<Overlaps.Violation>? Overlaps = null,
    int BranchesTrimmed = 0);

/// <summary>Baut aus dem Zwischenformat eine TruckLib-Karte.</summary>
public static class MapWriter
{
    // Spielgrenzen (TruckLib: Road.MinLength bei HighPoly, Road.MaxLength).
    // TruckLib prüft beides NICHT selbst; doppelte Punkte bringen den Prozess
    // sogar zum Absturz (Stapelüberlauf). Daher hier streng prüfen.
    public const float MinSegment = 1.25f;
    public const float MaxSegment = 1000f;

    public static (Map Map, WriteResult Result) Build(IntermediateFile input, MappingConfig mapping,
        Interchanges.PpdSource? prefabs = null)
    {
        var map = new Map
        {
            NormalScale = mapping.Map.NormalScale ?? (float)input.Projection.Scale,
            CityScale = mapping.Map.CityScale ?? (float)input.Projection.Scale,
        };

        var warnings = new List<string>();
        int written = 0, skipped = 0, items = 0, styleChanges = 0;
        RoadEntry? startRoad = null;

        var valid = new List<RoadEntry>();
        foreach (var entry in input.Roads)
        {
            var problem = Validate(entry, out _, out _);
            if (problem is not null)
            {
                skipped++;
                warnings.Add($"{entry.Id}: übersprungen – {problem}");
                continue;
            }
            valid.Add(entry);
        }
        Triangles.Collapse(valid, warnings);
        // Anschlussstellen mit Prefabs (nur wenn Prefab-Daten verfügbar sind)
        // Modelle mittig auf die OSM-Linie setzen, dann Gegenfahrbahnen höchstens berühren lassen
        if (Environment.GetEnvironmentVariable("OSM2ETS_NOCENTER") is null) LaneCentering.Apply(valid, mapping.Resolve);
        if (LaneCentering.Shifted > 0) warnings.Add($"Info: {LaneCentering.Shifted} Straßen seitlich versetzt (Modell mittig auf OSM-Linie)");
        if (Environment.GetEnvironmentVariable("OSM2ETS_NOTWINSEP") is null)
            TwinSeparation.Apply(valid, run => mapping.Resolve(run).EdgeLeft, warnings);
        int laneDrops = prefabs is not null ? Interchanges.PrepareLaneDrops(valid) : 0;
        foreach (var entry in valid) LaneSmoothing.Apply(entry);
        // Brückenläufe vor den Prefabs: Unterführungen auch unter Prefabs, die auf einer Brücke liegen
        var bridgeShots = Underpasses.Snapshot(valid);
        var interchangePlans = new List<Interchanges.Plan>();
        Interchanges.OffsetOf = run => mapping.Resolve(run).Offset;
        if (prefabs is not null)
        {
            foreach (var plan in Interchanges.Find(valid, warnings))
            {
                bool ok;
                try { ok = Interchanges.Prepare(plan, prefabs); }
                catch (Exception ex) { warnings.Add($"Knoten {plan.OsmNode}: {ex.Message}"); ok = false; }
                if (!ok)
                {
                    warnings.Add(plan.Blocked
                        ? $"Knoten {plan.OsmNode} ({plan.Kind}): Brücke/Tunnel im Prefab-Bereich – ohne Prefab gebaut"
                        : $"Knoten {plan.OsmNode} ({plan.Kind}): Straßen zu kurz für Prefabs – übersprungen");
                    continue;
                }
                plan.MainIn.End = new EndInfo { Kind = "prefab" };
                plan.MainOut.Start = new EndInfo { Kind = "prefab" };
                if (plan.Kind == "exit") plan.Ramp.Start = new EndInfo { Kind = "prefab" };
                else if (plan.Kind == "entry") plan.Ramp.End = new EndInfo { Kind = "prefab" };
                // Bauwerk unter dem äußeren Prefab: Straße mit Zusatzspur dazwischen
                if (plan.ExtRoad is not null)
                {
                    valid.Add(plan.ExtRoad);
                    warnings.Add($"Info: Knoten {plan.OsmNode} ({plan.Kind}): {(plan.ExtForCurve ? "Kurve" : "Bauwerk")} im Prefab-Bereich – {plan.Ext:0} m Zusatzspur-Straße zwischen den Prefabs"
                                 + (plan.Shift > 0 ? $", {(plan.Kind == "exit" ? "Teilung" : "Zusammenführung")} {plan.Shift:0} m vom Bauwerk weggerückt" : ""));
                    if (Environment.GetEnvironmentVariable("OSM2ETS_IPROBE") is not null)
                        Console.Error.WriteLine($"EXT {plan.ExtRoad.Id} pts " + string.Join(" ", plan.ExtRoad.Points.Select(q => $"({q[0]:0.0},{q[1]:0.0},{q[2]:0.0})")) +
                            " runs " + string.Join(" ", plan.ExtRoad.Runs.Select(x => $"{x.From}-{x.To}{(x.Bridge ? "B" : "")}{(x.Tunnel ? "T" : "")} {x.Role}")));
                }
                if (plan.Moved > 0)
                    warnings.Add($"Info: Knoten {plan.OsmNode} ({plan.Kind}): Einmündung {plan.Moved:0} m stromauf (Rampe läuft in OSM getrennt nebenher)");
                interchangePlans.Add(plan);
            }
        }
        if (Environment.GetEnvironmentVariable("OSM2ETS_IPROBE") is not null)
            foreach (var p in interchangePlans) Console.Error.WriteLine($"PRE {p.OsmNode} {p.Kind} anchor {p.Anchor} dir {p.Direction}");
        // nach den Anschluss-Prefabs: deren Enden zählen nicht mehr als Knoten
        int twoLaneLinks = Interchanges.NormalizeLinkLanes(valid);
        int icSep = Interchanges.SeparateOpposite(interchangePlans);
        warnings.Add($"Info: zweispurige Rampen: {twoLaneLinks}");
        if (Environment.GetEnvironmentVariable("OSM2ETS_IPROBE") is not null)
            foreach (var p in interchangePlans) Console.Error.WriteLine($"SEP {p.OsmNode} {p.Kind} anchor {p.Anchor}");
        if (icSep > 0) warnings.Add($"Info: {icSep} gegenüberliegende Anschlussstellen-Prefabs seitlich auseinandergerückt");

        // ebenerdige Kreuzungen normaler Straßen (T/X-Prefabs)
        var junctionPlans = new List<Junctions.Plan>();
        Junctions.Reset();
        PrefabFootprint.Reset();
        if (prefabs is not null)
        {
            string TypeAt(RoadEntry r, bool atStart) => mapping.Resolve(atStart ? r.Runs[0] : r.Runs[^1]).RoadType;
            // mehrere Durchgänge: eine Kreuzung, deren Arm zu kurz ist, kann im nächsten
            // Durchgang direkt an das inzwischen gesetzte Nachbar-Prefab angeschlossen werden
            var pending = Junctions.Find(valid, TypeAt, warnings);
            var lastReason = new Dictionary<Junctions.Plan, string>();
            foreach (var jp in pending) Junctions.PendingNodes.Add(jp.OsmNode);
            bool progress = true;
            while (pending.Count > 0 && progress)
            {
                progress = false;
                foreach (var jp in pending.ToList())
                {
                    try
                    {
                        if (Junctions.Prepare(jp, prefabs, out var reason)) { junctionPlans.Add(jp); pending.Remove(jp); Junctions.PendingNodes.Remove(jp.OsmNode); progress = true; }
                        else lastReason[jp] = reason ?? "?";
                    }
                    catch (Exception ex) { lastReason[jp] = $"{ex.GetType().Name}: {ex.Message}"; pending.Remove(jp); }
                }
            }
            foreach (var (jp, reason) in lastReason.Where(kv => !junctionPlans.Contains(kv.Key)))
                warnings.Add($"Knoten {jp.OsmNode} (Kreuzung): kein Prefab – {reason}");
            int dropped = valid.RemoveAll(Junctions.Dropped.Contains);
            if (dropped > 0) warnings.Add($"Info: {dropped} kurze Verbindungsstraßen durch direkt verbundene Kreuzungs-Prefabs ersetzt");
        }

        var (pairs, reversed) = Connector.Plan(valid, Junctions.Fixed);
        var firstItem = new Dictionary<RoadEntry, Road>();
        var lastItem = new Dictionary<RoadEntry, Road>();

        // Prefabs zuerst setzen, dann die Straßen tangential an ihre Knoten heranführen
        var placed = new List<Interchanges.Placed>();
        foreach (var plan in interchangePlans)
        {
            try
            {
                var pl = Interchanges.Build(map, plan, prefabs!);
                Interchanges.Adjust(pl);
                // Einfahrt direkt am Spurende-Prefab: die Rampenstraße dazwischen entfällt
                if (plan.DirectRamp) valid.Remove(plan.Ramp);
                if (Environment.GetEnvironmentVariable("OSM2ETS_IPROBE") is not null)
                {
                    string R(RoadEntry r) => $"{r.Id} start {string.Join('/', r.Points[0].Select(v => v.ToString("0.0")))} next {string.Join('/', r.Points[1].Select(v => v.ToString("0.0")))} end {string.Join('/', r.Points[^1].Select(v => v.ToString("0.0")))} prev {string.Join('/', r.Points[^2].Select(v => v.ToString("0.0")))}";
                    Console.Error.WriteLine($"IC {plan.OsmNode} {plan.Kind} anchor {plan.Anchor} grade {plan.Grade:0.000}");
                    foreach (var pf in new[] { pl.First, pl.Last }.Distinct()) for (int k = 0; k < pf.Nodes.Count; k++)
                        Console.Error.WriteLine($"   node {k} {pf.Nodes[k].Position} fwd {Vector3.Transform(Vector3.UnitZ * -1, pf.Nodes[k].Rotation)}");
                    foreach (var (nm, rr, st) in new[] { ("in", plan.MainIn, false), ("out", plan.MainOut, true), ("ramp", plan.Ramp, plan.Kind == "exit") }) { var pts = st ? rr.Points.Take(6) : rr.Points.TakeLast(6); Console.Error.WriteLine($"   {nm} {rr.Id}: " + string.Join(" | ", pts.Select(q => $"{q[0]:0.0},{q[1]:0.00},{q[2]:0.0}"))); }
                }
                placed.Add(pl);
                // direkt angehängte Einfahrt hat ein Spurende-Prefab neu gesetzt: Liste nachführen
                for (int k = 0; k < placed.Count; k++)
                {
                    var q = placed[k];
                    if (Interchanges.Replaced.TryGetValue(q.First, out var nf) || Interchanges.Replaced.ContainsKey(q.Last) || Interchanges.Replaced.ContainsKey(q.RampPrefab))
                        placed[k] = q with { First = Interchanges.Replaced.GetValueOrDefault(q.First, q.First), Last = Interchanges.Replaced.GetValueOrDefault(q.Last, q.Last), RampPrefab = Interchanges.Replaced.GetValueOrDefault(q.RampPrefab, q.RampPrefab) };
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Knoten {plan.OsmNode} ({plan.Kind}): Prefab nicht gesetzt – {ex.GetType().Name}: {ex.Message}");
                if (Environment.GetEnvironmentVariable("OSM2ETS_IPROBE") is not null) Console.Error.WriteLine(ex);
            }
        }

        // Nebenstraßen von Autobahnen, Rampen und Anschluss-Prefabs wegschieben, wo sie sich überdecken
        {
            var pfObs = new List<(Vector3[] Nodes, float HalfWidth)>();
            foreach (var pl in placed)
                foreach (var pf in new[] { pl.First, pl.Last }.Distinct())
                    pfObs.Add((pf.Nodes.Select(n => n.Position).ToArray(), 8f));
            // lange Segmente fein unterteilen, sonst liegt kein Punkt dort, wo der Weg die Rampe überdeckt
            if (MinorPush.Enabled)
                foreach (var e in valid)
                    if (!e.Runs.Any(x => x.Highway is "motorway" or "motorway_link" or "trunk" or "trunk_link")) SubdivideLinear(e, MinorPush.Sample);
            MinorPush.Apply(valid, run => { var st = mapping.Resolve(run); return (st.Width / 2, st.ModelCenter); }, pfObs, warnings, links: true);
            MinorPush.Apply(valid, run => { var st = mapping.Resolve(run); return (st.Width / 2, st.ModelCenter); }, pfObs, warnings);
            // eingefügte Prüfpunkte, die gerade geblieben sind, wieder entfernen
            if (MinorPush.Enabled)
                foreach (var e in valid)
                    if (!e.Runs.Any(x => x.Highway is "motorway" or "motorway_link" or "trunk" or "trunk_link")) DropCollinear(e, 0.02);
        }

        // Abzweige ohne Prefab bis an den Fahrbahnrand der anderen Straße zurücknehmen
        Overlaps.CenterOf = run => mapping.Resolve(run).ModelCenter;
        int branchesTrimmed = Overlaps.TrimBranches(valid, pairs, run => mapping.Resolve(run).Width / 2, warnings);

        // lange Abschnitte teilen, damit die Geländebreite nicht über hunderte Meter
        // an der engsten Stelle hängt
        if (input.TerrainGrid is { Nx: > 1, Nz: > 1 } && TerrainFill.WideRoadTerrain)
            foreach (var entry in valid) Subdivide(entry, 40f);

        // durch Unterteilen und Kürzen entstandene Kleinstabschnitte entfernen
        foreach (var entry in valid) DropShort(entry, MinSegment);

        // Stile je Abschnitt auflösen, dann Gelände an Nachbarstraßen anpassen
        var styles = new Dictionary<RoadEntry, ResolvedStyle[]>();
        foreach (var entry in valid)
        {
            if (Validate(entry, out var pts, out var ros) is { } verr) throw new InvalidOperationException($"{entry.Id}: {verr}");
            styles[entry] = Enumerable.Range(0, pts.Count - 1).Select(i => mapping.Resolve(entry.Runs[ros[i]])).ToArray();
        }
        int separated = CarriagewaySeparation.Apply(valid, styles);
        // Brückenkörper (Railing mit Widerlagern) für Paare paralleler Autobahnbrücken über Straßen:
        // nach dem Auseinanderrücken (Abstand der Linien), Brücke des Trägers als ein Stück
        {
            var bodies = mapping.BridgeBody is { } bb0 ? BridgeBodies.Plan(valid, bb0, warnings) : [];
            // alle übrigen Brücken: Körper je Straßentyp (Brücke als ein Stück)
            var covered = new HashSet<RunEntry>(bodies.SelectMany(b => new[] { b.Run, b.PartnerRun }).OfType<RunEntry>());
            var singles = mapping.BridgeBodies is { Count: > 0 }
                ? BridgeBodies.PlanSingle(valid, mapping.Resolve, mapping.BodyFor, covered,
                                          mapping.BridgeBody?.MaxLength ?? 200f, warnings, PrefabLowerRoads(placed))
                : [];
            foreach (var r in bodies.Select(x => x.Road).Concat(singles.Select(x => x.Road)).Distinct())
            {
                Validate(r, out var pts2, out var ros2);
                styles[r] = Enumerable.Range(0, pts2.Count - 1).Select(i => mapping.Resolve(r.Runs[ros2[i]])).ToArray();
            }
            foreach (var b in bodies)
            {
                int seg = b.Run?.From ?? b.Segment;
                styles[b.Road][seg] = styles[b.Road][seg] with { Body = mapping.BridgeBody!.Railing, BodyOffset = b.Offset };
            }
            foreach (var s in singles)
                for (int k = s.Run.From; k < s.Run.To; k++)
                    styles[s.Road][k] = styles[s.Road][k] with
                    {
                        Body = s.Type.Railing, BodyOffset = s.Type.Offset, BodyRight = s.Type.Side == "right", BodyParapet = s.Type.Parapet,
                    };
            if (bodies.Count > 0)
                warnings.Add($"Info: Brückenkörper ({mapping.BridgeBody!.Railing}) an {bodies.Count} Brückenpaaren über Straßen: " +
                             string.Join(", ", bodies.Select(b => $"{b.Road.Id}/{b.Partner} {b.Offset:0.0} m")));
            if (singles.Count > 0)
                warnings.Add($"Info: Brückenkörper an {singles.Count} einzelnen Brücken ({singles.Count(s => !s.Merged)} aus mehreren Stücken): " +
                             string.Join(", ", singles.GroupBy(s => s.Type.Railing).Select(g => $"{g.Key} {g.Count()}×")));
        }
        // An Anschluss-Prefabs kein Straßengelände auf den ersten 30 m: die Geländestreifen von
        // Rampe und Hauptfahrbahn kreuzen sich dort im spitzen Winkel und falten sich ins Prefab
        foreach (var pl in TerrainFill.WideRoadTerrain ? [] : interchangePlans)
        {
            void Clear(RoadEntry r, bool atStart, bool left)
            {
                if (!styles.TryGetValue(r, out var st)) return;
                double acc = 0;
                int n = r.Points.Count - 1;
                for (int k = 0; k < n && acc < IcTerrainGap; k++)
                {
                    int i = atStart ? k : n - 1 - k;
                    st[i] = left ? st[i] with { TerrainLeft = 0 } : st[i] with { TerrainRight = 0 };
                    acc += Vector3.Distance(ToVector(r.Points[i]), ToVector(r.Points[i + 1]));
                }
            }
            // nur die Seite zum Zwickel: Hauptfahrbahn rechts, Rampe links (Rechtsverkehr)
            Clear(pl.MainIn, false, false); Clear(pl.MainOut, true, false);
            if (pl.Kind != "drop") Clear(pl.Ramp, pl.Kind == "exit", true);
        }
        if (separated > 0) warnings.Add($"Info: {separated} Richtungsfahrbahnen seitlich verschoben (OSM-Abstand kleiner als Vorlagenbreite)");
        // Prefab-Flächen als Hindernisse: Strecken von jedem Knoten zur Prefab-Mitte
        var obstacles = new List<(Vector3[] Nodes, float HalfWidth)>();
        foreach (var pl in placed)
            foreach (var pf in new[] { pl.First, pl.Last }.Distinct())
                obstacles.Add((pf.Nodes.Select(n => n.Position).ToArray(), 8f));
        foreach (var jp in junctionPlans) obstacles.Add((jp.NodePos, 6f));
        TerrainClearance.Share = false; TerrainClearance.SearchRadius = 80f;
        if (input.TerrainGrid is { Nx: > 1, Nz: > 1 } && TerrainFill.WideRoadTerrain) TerrainClearance.Widen(styles);
        string Stat(string n) { var all = styles.Values.SelectMany(x => x).ToList(); return $"{n}: L {all.Average(x => x.TerrainLeft):0.0} R {all.Average(x => x.TerrainRight):0.0} L0 {all.Count(x => x.TerrainLeft <= 0)} R0 {all.Count(x => x.TerrainRight <= 0)}"; }
        if (Environment.GetEnvironmentVariable("OSM2ETS_DEBUG") is not null) Console.Error.WriteLine(Stat("vor"));
        TerrainClearance.HardLimited = 0;
        int terrainClamped = TerrainClearance.Apply(valid, styles, obstacles);
        if (TerrainClearance.HardLimited > 0) warnings.Add($"Info: Straßengelände an {TerrainClearance.HardLimited} Seiten schmaler als 2 m (fremder Fahrbahnrand näher)");
        if (TerrainClearance.Share) terrainClamped += TerrainClearance.ClampCurves(valid, styles);
        if (Environment.GetEnvironmentVariable("OSM2ETS_DEBUG") is not null) Console.Error.WriteLine(Stat("clear"));
        Overlaps.ExactRoads = TerrainClearance.Share && UseModelEdges && TerrainFill.GapRamp;
        terrainClamped += Overlaps.EnforceTerrain(valid, styles, obstacles);
        if (Environment.GetEnvironmentVariable("OSM2ETS_DEBUG") is not null) Console.Error.WriteLine(Stat("enforce"));
        if (Environment.GetEnvironmentVariable("OSM2ETS_PROBE") is { } probe)
        {
            var pp = probe.Split(',').Select(float.Parse).ToArray();
            foreach (var e in valid) for (int i = 0; i < e.Points.Count - 1; i++)
            {
                var m = new Vector2((float)(e.Points[i][0] + e.Points[i + 1][0]) / 2, (float)(e.Points[i][2] + e.Points[i + 1][2]) / 2);
                if (Vector2.Distance(m, new Vector2(pp[0], pp[1])) < 400)
                    Console.Error.WriteLine($"{e.Id}[{i}] mid {m} y {e.Points[i][1]:0.0} w {styles[e][i].Width} L {styles[e][i].TerrainLeft} R {styles[e][i].TerrainRight}");
            }
        }
        var overlaps = Overlaps.Report(valid, styles, pairs, obstacles);

        foreach (var entry in valid)
        {
            Validate(entry, out var points, out var runOfSegment);

            Road? road = null;
            ResolvedStyle? previous = null;
            for (int i = 0; i < points.Count - 1; i++)
            {
                var style = styles[entry][i];
                // Auflösung mit zu großer Mindestlänge für dieses Stück: HighPoly nehmen
                float length = Vector3.Distance(points[i], points[i + 1]);
                if (length < MinLengthFor(style.Resolution))
                    style = style with { Resolution = RoadResolution.HighPoly };
                if (road is null)
                {
                    road = Road.Add(map, points[0], points[1], style.RoadType, style.TerrainLeft, style.TerrainRight);
                    ApplyStyle(road, style);
                }
                else if (style == previous)
                {
                    road = road.Append(points[i + 1]);
                    ApplyFlags(road, style);
                }
                else
                {
                    road = road.Append(points[i + 1], style.RoadType, style.TerrainLeft, style.TerrainRight);
                    ApplyStyle(road, style);
                    styleChanges++;
                }
                RegisterSector(map, road);
                if (i == 0) firstItem[entry] = road;
                previous = style;
                items++;
            }
            lastItem[entry] = road!;
            written++;

            if (IsBetterStart(entry, startRoad))
                startRoad = entry;
        }

        var startPos = Vector3.Zero;
        if (startRoad is not null)
        {
            int k = StartSegment(startRoad);
            var a = ToVector(startRoad.Points[k]);
            var b = ToVector(startRoad.Points[k + 1]);
            startPos = (a + b) / 2f;  // Mitte des Abschnitts, nicht an einem Bauwerksende
            map.StartPosition = startPos;
            map.StartRotation = MathEx.GetNodeRotation(a, b);
        }

        // Durchgehende Fahrbahnen an Knoten verbinden (Ende von A = Anfang von B)
        int connections = 0;
        foreach (var (a, b) in pairs)
        {
            try
            {
                lastItem[a].ForwardNode.Merge(firstItem[b].Node);
                connections++;
            }
            catch (InvalidOperationException ex)
            {
                warnings.Add($"{a.Id} → {b.Id}: nicht verbunden – {ex.Message}");
            }
        }

        if (input.TerrainGrid is { Nx: > 1, Nz: > 1 } tg)
        {
            if (!TerrainFill.WideRoadTerrain && Environment.GetEnvironmentVariable("OSM2ETS_MEDIAN_SLOPES") is not null)
            {
                int sloped = MedianSlopes.Apply(valid, styles, firstItem, lastItem);
                warnings.Insert(0, $"Info: Mittelstreifen-Böschungen: {sloped} Geländeseiten zur tieferen Nachbarfahrbahn geneigt");
            }
        }

        int prefabCount = 0;
        foreach (var pl in placed)
        {
            var plan = pl.Plan;
            try
            {
                pl.First.Attach(pl.FirstIn, lastItem[plan.MainIn].ForwardNode);
                pl.Last.Attach(pl.LastOut, firstItem[plan.MainOut].Node);
                if (plan.Kind == "exit") pl.RampPrefab.Attach(pl.RampNode, firstItem[plan.Ramp].Node);
                else if (plan.Kind == "entry") pl.RampPrefab.Attach(pl.RampNode, lastItem[plan.Ramp].ForwardNode);
                if (plan.ExtRoad is not null && pl.ExtFrom is not null && pl.ExtTo is not null)
                {
                    pl.ExtFrom.Attach(pl.ExtFromNode, firstItem[plan.ExtRoad].Node);
                    pl.ExtTo.Attach(pl.ExtToNode, lastItem[plan.ExtRoad].ForwardNode);
                }
                Interchanges.FixRotations(pl);   // Attach übernimmt sonst die gekippte Prefab-Knotendrehung
                foreach (var pf in new[] { pl.First, pl.Last })
                    RegisterSector(map, pf.Nodes[0].Position);
                prefabCount += 2;
            }
            catch (Exception ex)
            {
                warnings.Add($"Knoten {plan.OsmNode} ({plan.Kind}): Prefab nicht gesetzt – {ex.GetType().Name}: {ex.Message}");
                if (Environment.GetEnvironmentVariable("OSM2ETS_IPROBE") is not null) Console.Error.WriteLine(ex);
            }
        }
        int junctionsSet = 0;
        foreach (var jp in junctionPlans)
        {
            try
            {
                var pf = Junctions.Build(map, jp, prefabs!);
                try
                {
                    // Knoten 0 zuerst, damit ein Fehler dort nichts halb verbunden zurücklässt
                    int cnt = pf.Nodes.Count;
                    foreach (int i in Enumerable.Range(0, jp.Arms.Length).OrderBy(i => Junctions.NodeIndex(jp, jp.NodeOf[i], cnt)))
                    {
                        var a = jp.Arms[i];
                        ushort ni = Junctions.NodeIndex(jp, jp.NodeOf[i], cnt);
                        if (Junctions.Dropped.Contains(a.Road))
                        {
                            if (i == jp.SnapArm && jp.SnapTo.Plan.Placed is { } other)
                            {
                                var q = jp.SnapTo;
                                ushort qi = Junctions.NodeIndex(q.Plan, q.Plan.NodeOf[q.Arm], other.Nodes.Count);
                                pf.Attach(ni, other, qi);
                            }
                            continue;
                        }
                        var at = pf.Nodes[ni].Position;
                        if (Junctions.StartsAt(a.Road, at)) pf.Attach(ni, firstItem[a.Road].Node);
                        else pf.Attach(ni, lastItem[a.Road].ForwardNode);
                    }
                    Junctions.FixNodeRotations(jp, pf);
                }
                catch { map.Delete(pf); throw; }
                RegisterSector(map, pf.Nodes[0].Position);
                foreach (var n in pf.Nodes) RegisterSector(map, n.Position);
                junctionsSet++;
            }
            catch (Exception ex)
            {
                warnings.Add($"Knoten {jp.OsmNode} (Kreuzung): Prefab nicht gesetzt – {ex.GetType().Name}: {ex.Message}");
                if (Environment.GetEnvironmentVariable("OSM2ETS_IPROBE") is not null) Console.Error.WriteLine(ex);
            }
        }
        // Neigung aller Prefab-Knoten aus der Prefab-Ebene (das Spiel setzt Prefabs nach dem Ursprungsknoten)
        PrefabFootprint.FixPitch(map);
        if (PrefabFootprint.PitchFixed > 0) warnings.Add($"Info: Neigung von {PrefabFootprint.PitchFixed} Prefab-Knoten an die Prefab-Lage angepasst");
        if (prefabs is not null)
            warnings.Insert(0, $"Info: Kreuzungen mit Prefabs: {junctionPlans.Count} geplant, {junctionsSet} gesetzt");
        if (prefabs is not null)
            warnings.Insert(0, $"Info: Anschlussstellen mit Prefabs: {interchangePlans.Count} geplant, {prefabCount / 2} gesetzt");

        // Füllgelände zuletzt: dann sind alle Prefabs gesetzt und ihre Fahrspuren bekannt
        if (input.TerrainGrid is { Nx: > 1, Nz: > 1 } tg2 && Environment.GetEnvironmentVariable("OSM2ETS_NOFILL") is null)
        {
            // Unterführungen: unter Brücken über Straßen Gelände auf Höhe der unteren Straße,
            // Böschungen von den Brückenenden (wie in der SCS-Karte) – als Obergrenze des Rasters
            var underpasses = Underpasses.Enabled ? Underpasses.Build(valid, styles, PrefabFootprint.All3(map), bridgeShots, mapping.Resolve) : null;
            Func<float, float, float, float>? post = underpasses is { Count: > 0 } ? underpasses.Cap : null;
            // Böschung neben Prefabs als Untergrenze jeder Rasterabfrage – auch für das Straßengelände
            // anderer Straßen. Bisher nur für das Füllgelände; das breite Straßengelände einer Straße
            // daneben folgte dem tiefen Raster. An der Zusammenführung vor der A-70-Brücke über die
            // Gemeindeverbindung lag das Gelände neben dem Prefab so 3–4 m tiefer (Mulde mit Wänden).
            // Unter Brücken gilt weiter die Obergrenze der Unterführung (danach angewandt).
            if (PrefabBanks && TerrainFill.WideRoadTerrain)
            {
                var banks = RoadCap.PrefabsOnly(PrefabFootprint.All3(map));
                var cap = post;
                post = (x, z, h) =>
                {
                    float f = banks.Floor(x, z);
                    if (f > h) h = f;
                    return cap is null ? h : cap(x, z, h);
                };
            }
            tg2.Post = post;
            if (Environment.GetEnvironmentVariable("OSM2ETS_DEBUG") is not null && underpasses is not null)
                Console.Error.WriteLine("UNDERPASS " + string.Join(" ", underpasses.Ids));
            if (TerrainFill.WideRoadTerrain)
            {
                int shaped = 0, bad = 0, folded = 0;
                // Zwickel an Gabelungen: nahe anderer Straßen/Prefabs kürzerer Übergang ins Raster
                RoadCap.PrefabLanes = PrefabFootprint.All3(map);
                RoadCap.RoadTerrainLowered = 0; TerrainFill.GapRamped = 0; TerrainFill.ColumnsClamped = 0; TerrainFill.SlopeLimited = 0;
                TerrainFill.PrefabRamped = 0;
                var others = Environment.GetEnvironmentVariable("OSM2ETS_NO_ROADTERRAIN_CAP") is null
                    ? RoadCap.Build(valid, styles, null) : null;
                // nächste Prefab-Spur für die Rampe ans Prefab – nicht im Bereich einer Unterführung
                // (dort liegt das Prefab womöglich über der Straße)
                Func<float, float, (float, float)>? prefabNear = others is null ? null : (x, z) =>
                    underpasses is { Count: > 0 } && underpasses.Inside(x, z) ? (TerrainFill.GoreDist, float.NaN)
                                                                              : others.NearestPrefab(x, z, TerrainFill.GoreDist);
                foreach (var entry in valid)
                {
                    if (!firstItem.TryGetValue(entry, out var it)) continue;
                    var forced = Environment.GetEnvironmentVariable("OSM2ETS_ROADFRAME");
                    TerrainFill.Mode = forced is { Length: > 0 } ? (forced == "mixed" ? entry.Id[0] : forced[0]) : 'D';
                    TerrainFill.RoadEdgeInData = TerrainFill.Mode is 'B' or 'D';
                    int seg = 0;
                    for (var rd = it; rd is not null; rd = rd.ForwardNode.ForwardItem as Road, seg++)
                    {
                        rd.Recalculate();
                        // Geländebeginn je Seite = Rand des Straßenmodells. Die Einbahn-Vorlagen sind
                        // asymmetrisch (ger13: −4,5…+8,0 m, ger2: −5,5…+2,5 m um die Straßenlinie);
                        // mit der halben Breite (symmetrisch) lag die erste Geländereihe rechts bzw.
                        // links INNERHALB des Modells – das Spiel setzt Reihe 0 an den Modellrand,
                        // die Quads dazwischen klappten um (schwarzer Streifen entlang der Fahrbahn).
                        var st = styles[entry][Math.Min(seg, styles[entry].Length - 1)];
                        if (RtProbe is { } rp && Vector2.Distance(new Vector2(rd.Node.Position.X, rd.Node.Position.Z), rp) < 40)
                        {
                            var rr = entry.Runs.FirstOrDefault(x => x.From <= seg && seg < x.To);
                            Console.Error.WriteLine($"RT {entry.Id}[{seg}] {rd.Uid:X} {rd.RoadType} L {rd.Left.Terrain.Size} R {rd.Right.Terrain.Size} style L {st.TerrainLeft} R {st.TerrainRight} bridge {rr?.Bridge} tunnel {rr?.Tunnel} hw {rr?.Highway} pts {entry.Points.Count}");
                            if (seg == 0 || Environment.GetEnvironmentVariable("OSM2ETS_RTPROBE_ALL") is not null)
                                Console.Error.WriteLine("   pts " + string.Join(" ", entry.Points.Select(p => $"({p[0]:0.0},{p[2]:0.0})")) + " runs " + string.Join(" ", entry.Runs.Select(x => $"{x.From}-{x.To}{(x.Bridge ? "B" : "")}")));
                        }
                        var (edL, edR) = TerrainClearance.Edges(st);
                        float eL = TerrainFill.RoadEdgeInData ? edL : 0;
                        float eR = TerrainFill.RoadEdgeInData ? edR : 0;
                        var pa = rd.Node.Position; var pb = rd.ForwardNode.Position;
                        Vector2 Tan(Quaternion q)
                        {
                            var v = Vector3.Transform(-Vector3.UnitZ, q); var t = new Vector2(v.X, v.Z);
                            var ch = new Vector2(pb.X - pa.X, pb.Z - pa.Z);
                            if (t.LengthSquared() < 1e-6f) t = ch;
                            t = Vector2.Normalize(t);
                            return Vector2.Dot(t, ch) < 0 ? -t : t;
                        }
                        Vector3 Dir3(Quaternion q)
                        {
                            var v = Vector3.Transform(-Vector3.UnitZ, q);
                            return Vector3.Dot(v, pb - pa) < 0 ? -v : v;
                        }
                        Vector2? ta = null, tb = null;
                        Vector3? da = null, db = null;
                        if (TerrainFill.Mode is 'C' or 'D') { ta = Tan(rd.Node.Rotation); tb = Tan(rd.ForwardNode.Rotation); da = Dir3(rd.Node.Rotation); db = Dir3(rd.ForwardNode.Rotation); }
                        for (int tries = 0; tries < 4; tries++)
                        {
                            Func<float, float, float, (float, float, bool)>? cap = others is null ? null : (x, z, y) => others.NearestOther(x, z, entry, TerrainFill.GoreDist, y);
                            var sa2 = new Vector2(pa.X, pa.Z); var sb2 = new Vector2(pb.X, pb.Z);
                            Func<Vector2, Vector2, float, float>? ray = others is null || !TerrainFill.ClampColumns || !Overlaps.ExactRoads ? null
                                : (o, dir, maxD) => others.RayFree(o, dir, maxD, entry, sa2, sb2);
                            if (TerrainFill.ShapeRoadSide(rd.Left.Terrain, tg2, pa, pb, rd.Left.Terrain.Size, -1, eL, ta, tb, cap, da, db, ray, prefabNear) < 0) bad++;
                            if (TerrainFill.ShapeRoadSide(rd.Right.Terrain, tg2, pa, pb, rd.Right.Terrain.Size, +1, eR, ta, tb, cap, da, db, ray, prefabNear) < 0) bad++;
                            // gefaltete Quads (Innenseite enger Kurven/Knoten) → Seite schmaler machen
                            float nl = TerrainFill.FoldFreeSize(rd, rd.Left.Terrain, -1, eL, ta, tb, da, db);
                            float nr = TerrainFill.FoldFreeSize(rd, rd.Right.Terrain, +1, eR, ta, tb, da, db);
                            if (nl >= rd.Left.Terrain.Size && nr >= rd.Right.Terrain.Size) break;
                            rd.Left.Terrain.Size = nl; rd.Right.Terrain.Size = nr; folded++;
                            rd.Recalculate();
                        }
                        shaped++;
                        if (rd == lastItem[entry]) break;
                    }
                }
                warnings.Insert(0, $"Info: Straßengelände an Höhenraster angepasst: {shaped} Stücke, Raster unpassend: {bad}, wegen Faltung verschmälert: {folded}, im Zwickel schneller ins Raster: {RoadCap.RoadTerrainLowered} Punkte, als Rampe zur Nachbarfahrbahn: {TerrainFill.GapRamped} Punkte, als Rampe ans Prefab: {TerrainFill.PrefabRamped} Punkte, Spalten vor fremder Fahrbahn gekürzt: {TerrainFill.ColumnsClamped}, Neigung begrenzt: {TerrainFill.SlopeLimited} Punkte");
                if (underpasses is { Count: > 0 })
                {
                    int paved = underpasses.Pave(valid, firstItem, lastItem);
                    warnings.Insert(0, $"Info: Unterführungen: {underpasses.Count} Brücken über Straßen – Gelände darunter auf Höhe der unteren Straße, Böschungen von den Brückenenden, {paved} Böschungsquads mit Steinmaterial");
                }
            }
            var lanes = PrefabFootprint.All(map);
            RoadCap.PrefabLanes = PrefabFootprint.All3(map);
            var cover = TerrainFill.WideRoadTerrain && CoverCap.Enabled ? CoverCap.Build(valid, firstItem, lastItem, styles) : null;
            TerrainFill.FinePrefabLanes = PrefabFootprint.KitLanes(map);
            var tf = TerrainFill.Build(map, tg2, valid, styles, obstacles, lanes, cover);
            if (cover is not null)
                warnings.Insert(0, $"Info: Füllgelände unter Fahrbahn/Straßengelände begrenzt: {CoverCap.CoverLowered} Punkte, am Rand per Böschung ({CoverCap.Slope:0.0}) gesenkt: {CoverCap.ConeLowered}, angehoben: {CoverCap.ConeRaised} Punkte, Durchstoßungen nachträglich abgesenkt: {CoverCap.SettledQuads} Quads ({CoverCap.Settled} Höhenpunkte, höchstens {CoverCap.SettledMax:0.00} m)");
            warnings.Insert(0, $"Info: flächiges Gelände: {tf.Items} Terrain-Stücke in {tf.Rows} Reihen, {tf.AreaKm2:0.0} km², Raster unpassend: {TerrainFill.Mismatch}, Raster eine Zeile mehr: {TerrainFill.RowShort}, freie Fläche ohne Gelände: {TerrainFill.Uncovered:P1}, Stücke mit 4-m-Raster an Prefabs: {TerrainFill.FinePieces}, davon in feinen Reihen um Anschluss-Prefabs: {TerrainFill.ZonePieces}, Prefab-Spurpunkte: {lanes.Count} (nicht zuordenbar: {PrefabFootprint.Unfitted}), an Fahrbahnen abgesenkt: {RoadCap.Lowered}, angehoben: {RoadCap.Raised} Punkte");
        }
        if (Environment.GetEnvironmentVariable("OSM2ETS_ONLYBOX") is { } ob)
        {
            var q = ob.Split(',').Select(float.Parse).ToArray(); // x0,z0,x1,z1
            bool In(Vector3 v) => v.X >= q[0] && v.X <= q[2] && v.Z >= q[1] && v.Z <= q[3];
            foreach (var it in map.MapItems.Values.ToList())
            {
                bool keep = it is Terrain t ? In(t.Node.Position) || In(t.ForwardNode.Position)
                          : it is Road r ? In(r.Node.Position) || In(r.ForwardNode.Position) : false;
                if (!keep) map.Delete(it);
            }
        }
        if (Environment.GetEnvironmentVariable("OSM2ETS_TPROBE") is { } tp)
        {
            var q = tp.Split(',').Select(float.Parse).ToArray();
            foreach (var t in map.MapItems.Values.OfType<Terrain>())
            {
                var a = t.Node.Position; var b = t.ForwardNode.Position;
                if (MathF.Max(a.X, b.X) < q[0] - 40 || MathF.Min(a.X, b.X) > q[0] + 40 || MathF.Abs(a.Z - q[1]) > 40) continue;
                Console.Error.WriteLine($"T {a.X:0.0}..{b.X:0.0} z {a.Z:0.0} y {a.Y:0.0}/{b.Y:0.0} L {t.Left.Terrain.Size:0.0} R {t.Right.Terrain.Size:0.0} offs {t.Left.Terrain.QuadData.Offsets.Count}/{t.Right.Terrain.QuadData.Offsets.Count} cols {t.Left.Terrain.QuadData.Cols} rows {t.Left.Terrain.QuadData.Rows}/{t.Right.Terrain.QuadData.Rows}");
            }
        }
        DeadEndsPlaced = DeadEnds.Apply(map, valid, styles, firstItem, lastItem);
        if (DeadEndsPlaced.Count > 0) warnings.Add($"Info: {DeadEndsPlaced.Count} Sperrbaken an Straßenenden, die ins Nichts führen");
        if (AreaCheck.Run(map, input.Areas) is { } ac)
            warnings.Add($"Info: Lage zu area:highway: {ac.Points} Spurpunkte an Autobahnflächen, {ac.Outside} mehr als {AreaCheck.Tol:0} m außerhalb" +
                         $" ({100.0 * ac.Outside / Math.Max(ac.Points, 1):0.0} %), höchstens {ac.Max:0.0} m" +
                         (ac.Spots.Count > 0 ? "; Stellen: " + string.Join(", ", ac.Spots.Take(6).Select(sp => $"({sp.At.X:0},{sp.At.Y:0}) {sp.N}× bis {sp.Max:0.0} m")) : ""));
        return (map, new WriteResult(written, skipped, items, styleChanges, startPos, startRoad?.Id, warnings,
                                     connections, reversed, terrainClamped, overlaps, branchesTrimmed));
    }

    /// <summary>
    /// Abschnitte länger als maxLen teilen. Neue Punkte auf einer Catmull-Rom-Kurve durch
    /// die Nachbarpunkte (nähert die Spielkurve), Höhe linear. Runs werden umnummeriert.
    /// </summary>
    /// <summary>
    /// Anschluss-Prefabs als untere Straßen für die Brückenverlängerung: Hauptfahrbahn (Eingang →
    /// Ausgang) und Rampe als dreispurige Autobahn bzw. Rampe. Sonst stand ein Widerlager auf der
    /// Fahrbahn eines Prefabs unter einer Brücke (die Prefabs liegen nicht genau auf der OSM-Linie).
    /// </summary>
    private static List<RoadEntry> PrefabLowerRoads(List<Interchanges.Placed> placed)
    {
        var res = new List<RoadEntry>();
        static double[] V(Vector3 v) => [v.X, v.Y, v.Z];
        foreach (var pl in placed)
        {
            var a = pl.First.Nodes[pl.FirstIn].Position; var b = pl.Last.Nodes[pl.LastOut].Position;
            var ramp = pl.RampPrefab.Nodes[pl.RampNode].Position;
            var c = (a + b) / 2;
            foreach (var (p, q, lanes) in new[] { (a, b, 3), (c, ramp, 1) })
                res.Add(new RoadEntry
                {
                    Id = $"prefab@{pl.Plan.OsmNode}", Points = [V(p), V(q)],
                    Runs = [new RunEntry { From = 0, To = 1, Highway = lanes == 1 ? "motorway_link" : "motorway", Oneway = true, Lanes = lanes }],
                });
        }
        return res;
    }

    /// <summary>Innere Punkte entfernen, die einen Abschnitt kürzer als minLen machen (Läufe angepasst).</summary>
    /// <summary>Innere Punkte auf der Geraden ihrer Nachbarn (Abweichung unter tol, auch in der Höhe) entfernen; Laufgrenzen bleiben.</summary>
    internal static void DropCollinear(RoadEntry r, double tol)
    {
        var keep = new HashSet<int> { 0, r.Points.Count - 1 };
        foreach (var run in r.Runs) { keep.Add(run.From); keep.Add(Math.Min(run.To, r.Points.Count - 1)); }
        for (int i = r.Points.Count - 2; i >= 1; i--)
        {
            if (keep.Contains(i)) continue;
            var a = r.Points[i - 1]; var b = r.Points[i]; var c = r.Points[i + 1];
            double len2 = (c[0] - a[0]) * (c[0] - a[0]) + (c[1] - a[1]) * (c[1] - a[1]) + (c[2] - a[2]) * (c[2] - a[2]);
            if (len2 < 1e-9) continue;
            double t = ((b[0] - a[0]) * (c[0] - a[0]) + (b[1] - a[1]) * (c[1] - a[1]) + (b[2] - a[2]) * (c[2] - a[2])) / len2;
            double dx = a[0] + t * (c[0] - a[0]) - b[0], dy = a[1] + t * (c[1] - a[1]) - b[1], dz = a[2] + t * (c[2] - a[2]) - b[2];
            if (t <= 0 || t >= 1 || dx * dx + dy * dy + dz * dz > tol * tol) continue;
            // neben dem Punkt darf kein Abschnitt länger als das Maximum werden
            if (Math.Sqrt(len2) > MaxSegment * 0.9) continue;
            r.Points.RemoveAt(i);
            foreach (var run in r.Runs) { if (run.From > i) run.From--; if (run.To > i) run.To--; }
            keep = keep.Select(k => k > i ? k - 1 : k).ToHashSet();
        }
    }

    internal static void DropShort(RoadEntry r, float minLen)
    {
        static float D(double[] a, double[] b) => MathF.Sqrt((float)((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2])));
        for (int i = 0; i < r.Points.Count - 1 && r.Points.Count > 2;)
        {
            if (D(r.Points[i], r.Points[i + 1]) >= minLen) { i++; continue; }
            int k = i + 1 < r.Points.Count - 1 ? i + 1 : i;      // inneren Punkt entfernen
            if (k == 0) { i++; continue; }
            // Lauf, der an Punkt k beginnt, beginnt nun einen früher; Indizes dahinter rücken auf
            r.Points.RemoveAt(k);
            foreach (var run in r.Runs)
            {
                if (run.From >= k) run.From--;
                if (run.To >= k) run.To--;
            }
            r.Runs.RemoveAll(x => x.From >= x.To);
        }
    }

    /// <summary>Gerade Zwischenpunkte einfügen (Form bleibt), Läufe angepasst.</summary>
    internal static void SubdivideLinear(RoadEntry r, float maxLen)
    {
        var p = r.Points;
        if (p.Count < 2) return;
        var np = new List<double[]> { p[0] };
        var map = new int[p.Count];
        for (int i = 0; i < p.Count - 1; i++)
        {
            var a = p[i]; var b = p[i + 1];
            double len = Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[2] - a[2]) * (b[2] - a[2]));
            int n = (int)Math.Ceiling(len / maxLen);
            for (int k = 1; k < n; k++)
            {
                double t = (double)k / n;
                np.Add([a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t]);
            }
            np.Add(b);
            map[i + 1] = np.Count - 1;
        }
        if (np.Count == p.Count) return;
        foreach (var run in r.Runs) { run.From = map[run.From]; run.To = map[Math.Min(run.To, map.Length - 1)]; }
        r.Points = np;
    }

    internal static void Subdivide(RoadEntry r, float maxLen)
    {
        var p = r.Points;
        if (p.Count < 2) return;
        var np = new List<double[]> { p[0] };
        var map = new int[p.Count];
        static Vector2 V(double[] a) => new((float)a[0], (float)a[2]);
        for (int i = 0; i < p.Count - 1; i++)
        {
            var a = p[i]; var b = p[i + 1];
            float len = Vector2.Distance(V(a), V(b));
            int n = (int)MathF.Ceiling(len / maxLen);
            var p0 = i > 0 ? V(p[i - 1]) : 2 * V(a) - V(b);
            var p3 = i + 2 < p.Count ? V(p[i + 2]) : 2 * V(b) - V(a);
            var p1 = V(a); var p2 = V(b);
            for (int k = 1; k < n; k++)
            {
                float t = (float)k / n, t2 = t * t, t3 = t2 * t;
                var q = 0.5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
                np.Add([q.X, a[1] + (b[1] - a[1]) * t, q.Y]);
            }
            np.Add(b);
            map[i + 1] = np.Count - 1;
        }
        if (np.Count == p.Count) return;
        int M(int k) => k >= map.Length ? np.Count - 1 + (k - map.Length + 1) : map[k];   // To kann hinter dem letzten Punkt liegen
        foreach (var run in r.Runs) { run.From = M(run.From); run.To = M(run.To); }
        r.Points = np;
    }

    public const float IcTerrainGap = 30f;

    /// <summary>Böschung neben Prefabs auch im Straßengelände (aus: OSM2ETS_NO_PREFAB_BANKS).</summary>
    public static bool PrefabBanks = Environment.GetEnvironmentVariable("OSM2ETS_NO_PREFAB_BANKS") is null;

    private static void ApplyStyle(Road road, ResolvedStyle style)
    {
        // flaches Gelände neben der Straße (Standard wäre 100 % Rauschen)
        road.Left.Terrain.Noise = TerrainNoise.Percent0;
        road.Right.Terrain.Noise = TerrainNoise.Percent0;
        if (road.Resolution != style.Resolution)
        {
            road.Resolution = style.Resolution;
            // TruckLib hat das Geländeraster schon für HighPoly berechnet
            road.Recalculate();
        }
        style.Left?.ApplyTo(road.Left);
        style.Right?.ApplyTo(road.Right);
        road.Boundary = style.Boundary;
        ApplyFlags(road, style);
        // Geländer an beiden Modellrändern (Brücken), Brückenkörper links (zur Gegenfahrbahn); alle
        // Plätze immer setzen – Append übernimmt die Einstellungen des vorigen Stücks
        for (int k = 0; k < road.Left.Railings.Models.Length; k++) road.Left.Railings.Models[k] = default;
        for (int k = 0; k < road.Right.Railings.Models.Length; k++) road.Right.Railings.Models[k] = default;
        // Körper mit eigener Brüstung: kein zusätzliches Geländer
        if (style.Railing is { } rail && !(style.Body is not null && style.BodyParapet))
        {
            if (style.RailingLeft)
                road.Left.Railings.Models[0] = new Railing { Model = new Token(rail), Offset = style.Width / 2 - style.ModelCenter + style.RailingOffset };
            road.Right.Railings.Models[0] = new Railing { Model = new Token(rail), Offset = style.Width / 2 + style.ModelCenter + style.RailingOffset };
        }
        if (style.Body is { } body && style.BodyParapet)
            (style.BodyRight ? road.Right : road.Left).Railings.Models[0] = new Railing { Model = new Token(body), Offset = style.BodyOffset };
        else if (style.Body is { } body2)   // erster freier Platz links (wie in der SCS-Karte Platz 1 ohne Geländer)
            road.Left.Railings.Models[style.Railing is not null && style.RailingLeft ? 1 : 0] = new Railing { Model = new Token(body2), Offset = style.BodyOffset };
        // Standard wären 400 m – bei langen Stücken verschwinden ferne Straßen,
        // während der Verkehr darauf sichtbar bleibt ("schwebende" Fahrzeuge)
        road.ViewDistance = 1400; // TruckLib KdopItem.ViewDistanceExtremelyFar
    }

    /// <summary>Geländebeginn am asymmetrischen Modellrand (aus: OSM2ETS_RT_SYMMETRIC).</summary>
    public static bool UseModelEdges = Environment.GetEnvironmentVariable("OSM2ETS_RT_SYMMETRIC") is null;
    private static readonly Vector2? RtProbe = Environment.GetEnvironmentVariable("OSM2ETS_RTPROBE") is { } e
        ? new Vector2(float.Parse(e.Split(',')[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(e.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture)) : null;

    /// <summary>Navi-/Karten-Schalter aus der Zuordnung (nur gesetzte Werte).</summary>
    private static void ApplyFlags(Road road, ResolvedStyle style)
    {
        if (style.ShowInUiMap is bool show) road.ShowInUiMap = show;
        if (style.GpsAvoid is bool avoid) road.GpsAvoid = avoid;
        if (style.Secret is bool secret) road.Secret = secret;
        if (style.AiVehicles is bool ai) road.AiVehicles = ai;
    }

    /// <summary>Mindestlänge eines Straßenstücks je Auflösung (TruckLib Road.MinLength).</summary>
    internal static float MinLengthFor(RoadResolution resolution) => resolution switch
    {
        RoadResolution.Superfine => 0.4f,
        RoadResolution.Normal => 3.75f,
        _ => 1.25f,
    };

    /// <summary>
    /// TruckLib 0.5.1 legt beim Anhängen keinen Sektor für den Mittelpunkt des
    /// neuen Stücks an; Map.Save schreibt nur angelegte Sektoren. Ein Stück,
    /// dessen Mittelpunkt in einem Sektor ohne Knoten liegt, ginge sonst beim
    /// Speichern verloren.
    /// </summary>
    private static void RegisterSector(Map map, Road road) =>
        map.AddSector(Map.GetSectorOfCoordinate((road.Node.Position + road.ForwardNode.Position) / 2f));

    private static void RegisterSector(Map map, Vector3 pos) => map.AddSector(Map.GetSectorOfCoordinate(pos));

    /// <summary>Startpunkt für "Run Map": möglichst eine lange Autobahn-Richtungsfahrbahn.</summary>
    private static bool IsBetterStart(RoadEntry candidate, RoadEntry? current)
    {
        static int Rank(RoadEntry r) =>
            r.Runs.Any(x => x.Highway == "motorway") ? 0 :
            r.Role == "core" ? 1 : 2;
        if (candidate.Points.Count < 2) return false;
        if (current is null) return true;
        int rc = Rank(candidate), rr = Rank(current);
        return rc < rr || (rc == rr && candidate.LengthM > current.LengthM);
    }

    /// <summary>
    /// Legt für jeden Sektor ohne .layer-Datei eine leere an (Kopf der .base-Datei
    /// + EOF-Marker). Hintergrund: Laut SCS-Modding-Wiki braucht ein Sektor die
    /// Dateien .aux, .base, .data, .desc und .layer; TruckLib schreibt .layer nur,
    /// wenn ein Item eine Editor-Ebene hat (die map-docs nennen die Datei optional).
    /// Eine leere Datei ist in beiden Lesarten unschädlich.
    /// </summary>
    public static int EnsureLayerFiles(string sectorDirectory)
    {
        const int headerSize = 16; // u32 Formatversion, Token "euro2", u32 game_map_version
        int created = 0;
        foreach (var basePath in Directory.EnumerateFiles(sectorDirectory, "*.base"))
        {
            var layerPath = Path.ChangeExtension(basePath, ".layer");
            if (File.Exists(layerPath)) continue;
            var header = new byte[headerSize];
            using (var input = File.OpenRead(basePath))
                input.ReadExactly(header);
            using var output = File.Create(layerPath);
            output.Write(header);
            output.Write(BitConverter.GetBytes(ulong.MaxValue)); // EOF-Marker 0xFFFFFFFFFFFFFFFF
            created++;
        }
        return created;
    }

    /// <summary>
    /// Abschnitt möglichst in Straßenmitte, der selbst und dessen Nachbarn weder
    /// auf einer Brücke noch im Tunnel liegen (ersatzweise nur er selbst).
    /// </summary>
    internal static int StartSegment(RoadEntry road)
    {
        int segments = road.Points.Count - 1;
        int mid = segments / 2;
        bool Plain(int k)
        {
            if (k < 0 || k >= segments) return true;
            var run = road.Runs.FirstOrDefault(r => r.From <= k && k < r.To);
            return run is not null && !run.Bridge && !run.Tunnel;
        }
        foreach (bool strict in new[] { true, false })
        {
            for (int d = 0; d <= segments; d++)
            {
                foreach (var k in new[] { mid + d, mid - d })
                {
                    if (k < 0 || k >= segments || !Plain(k)) continue;
                    if (!strict || (Plain(k - 1) && Plain(k + 1)))
                        return k;
                }
            }
        }
        return Math.Clamp(mid, 0, segments - 1);
    }

    public static Vector3 ToVector(double[] p) => new((float)p[0], (float)p[1], (float)p[2]);

    /// <summary>Prüft ein Straßenstück und liefert Punkte und Lauf je Abschnitt.</summary>
    /// <summary>Zuletzt gesetzte Sperrbaken (für die Begleitdatei).</summary>
    internal static List<DeadEnds.Placed> DeadEndsPlaced = [];

    internal static string? Validate(RoadEntry entry, out List<Vector3> points, out int[] runOfSegment)
    {
        points = [];
        runOfSegment = [];
        if (entry.Points.Count < 2)
            return "weniger als zwei Punkte";
        foreach (var p in entry.Points)
        {
            if (p.Length != 3 || p.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                return "ungültige Koordinate";
            points.Add(ToVector(p));
        }
        int segments = points.Count - 1;
        for (int i = 0; i < segments; i++)
        {
            float len = Vector3.Distance(points[i], points[i + 1]);
            if (len < MinSegment)
                return $"Abschnitt {i} ist {len:0.###} m kurz (Minimum {MinSegment} m)";
            if (len > MaxSegment)
                return $"Abschnitt {i} ist {len:0.#} m lang (Maximum {MaxSegment} m)";
        }
        runOfSegment = new int[segments];
        Array.Fill(runOfSegment, -1);
        for (int r = 0; r < entry.Runs.Count; r++)
        {
            var run = entry.Runs[r];
            if (run.From < 0 || run.To > segments || run.From >= run.To)
                return $"Lauf {r} hat ungültige Grenzen {run.From}..{run.To}";
            for (int s = run.From; s < run.To; s++)
            {
                if (runOfSegment[s] != -1)
                    return $"Abschnitt {s} gehört zu mehreren Läufen";
                runOfSegment[s] = r;
            }
        }
        if (runOfSegment.Any(r => r == -1))
            return "nicht alle Abschnitte haben Attribute";
        return null;
    }
}
