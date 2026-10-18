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
        // Anschlussstellen mit Prefabs (nur wenn Prefab-Daten verfügbar sind)
        foreach (var entry in valid) LaneSmoothing.Apply(entry);
        var interchangePlans = new List<Interchanges.Plan>();
        if (prefabs is not null)
        {
            foreach (var plan in Interchanges.Find(valid, warnings))
            {
                bool ok;
                try { ok = Interchanges.Prepare(plan, prefabs); }
                catch (Exception ex) { warnings.Add($"Knoten {plan.OsmNode}: {ex.Message}"); ok = false; }
                if (!ok) { warnings.Add($"Knoten {plan.OsmNode} ({plan.Kind}): Straßen zu kurz für Prefabs – übersprungen"); continue; }
                plan.MainIn.End = new EndInfo { Kind = "prefab" };
                plan.MainOut.Start = new EndInfo { Kind = "prefab" };
                if (plan.Kind == "exit") plan.Ramp.Start = new EndInfo { Kind = "prefab" };
                else plan.Ramp.End = new EndInfo { Kind = "prefab" };
                interchangePlans.Add(plan);
            }
        }
        int icSep = Interchanges.SeparateOpposite(interchangePlans);
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
                if (Environment.GetEnvironmentVariable("OSM2ETS_IPROBE") is not null)
                {
                    string R(RoadEntry r) => $"{r.Id} start {string.Join('/', r.Points[0].Select(v => v.ToString("0.0")))} next {string.Join('/', r.Points[1].Select(v => v.ToString("0.0")))} end {string.Join('/', r.Points[^1].Select(v => v.ToString("0.0")))} prev {string.Join('/', r.Points[^2].Select(v => v.ToString("0.0")))}";
                    Console.Error.WriteLine($"IC {plan.OsmNode} {plan.Kind} anchor {plan.Anchor} grade {plan.Grade:0.000}");
                    foreach (var pf in new[] { pl.First, pl.Last }.Distinct()) for (int k = 0; k < pf.Nodes.Count; k++)
                        Console.Error.WriteLine($"   node {k} {pf.Nodes[k].Position} fwd {Vector3.Transform(Vector3.UnitZ * -1, pf.Nodes[k].Rotation)}");
                    foreach (var (nm, rr, st) in new[] { ("in", plan.MainIn, false), ("out", plan.MainOut, true), ("ramp", plan.Ramp, plan.Kind == "exit") }) { var pts = st ? rr.Points.Take(6) : rr.Points.TakeLast(6); Console.Error.WriteLine($"   {nm} {rr.Id}: " + string.Join(" | ", pts.Select(q => $"{q[0]:0.0},{q[1]:0.00},{q[2]:0.0}"))); }
                }
                placed.Add(pl);
            }
            catch (Exception ex)
            {
                warnings.Add($"Knoten {plan.OsmNode} ({plan.Kind}): Prefab nicht gesetzt – {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Abzweige ohne Prefab bis an den Fahrbahnrand der anderen Straße zurücknehmen
        int branchesTrimmed = Overlaps.TrimBranches(valid, pairs, run => mapping.Resolve(run).Width / 2, warnings);

        // lange Abschnitte teilen, damit die Geländebreite nicht über hunderte Meter
        // an der engsten Stelle hängt
        if (input.TerrainGrid is { Nx: > 1, Nz: > 1 } && TerrainFill.WideRoadTerrain)
            foreach (var entry in valid) Subdivide(entry, 40f);

        // Stile je Abschnitt auflösen, dann Gelände an Nachbarstraßen anpassen
        var styles = new Dictionary<RoadEntry, ResolvedStyle[]>();
        foreach (var entry in valid)
        {
            Validate(entry, out var pts, out var ros);
            styles[entry] = Enumerable.Range(0, pts.Count - 1).Select(i => mapping.Resolve(entry.Runs[ros[i]])).ToArray();
        }
        int separated = CarriagewaySeparation.Apply(valid, styles);
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
            Clear(pl.Ramp, pl.Kind == "exit", true);
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
        int terrainClamped = TerrainClearance.Apply(valid, styles, obstacles);
        if (TerrainClearance.Share) terrainClamped += TerrainClearance.ClampCurves(valid, styles);
        if (Environment.GetEnvironmentVariable("OSM2ETS_DEBUG") is not null) Console.Error.WriteLine(Stat("clear"));
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
            if (TerrainFill.WideRoadTerrain)
            {
                int shaped = 0, bad = 0;
                foreach (var entry in valid)
                {
                    if (!firstItem.TryGetValue(entry, out var it)) continue;
                    var hw = styles[entry][0].Width / 2;
                    var forced = Environment.GetEnvironmentVariable("OSM2ETS_ROADFRAME");
                    TerrainFill.Mode = forced is { Length: > 0 } ? (forced == "mixed" ? entry.Id[0] : forced[0]) : 'D';
                    TerrainFill.RoadEdgeInData = TerrainFill.Mode is 'B' or 'D';
                    for (var rd = it; rd is not null; rd = rd.ForwardNode.ForwardItem as Road)
                    {
                        rd.Recalculate();
                        float e = TerrainFill.RoadEdgeInData ? MathF.Max(0, hw) : 0;
                        var pa = rd.Node.Position; var pb = rd.ForwardNode.Position;
                        Vector2 Tan(Quaternion q)
                        {
                            var v = Vector3.Transform(-Vector3.UnitZ, q); var t = new Vector2(v.X, v.Z);
                            var ch = new Vector2(pb.X - pa.X, pb.Z - pa.Z);
                            if (t.LengthSquared() < 1e-6f) t = ch;
                            t = Vector2.Normalize(t);
                            return Vector2.Dot(t, ch) < 0 ? -t : t;
                        }
                        Vector2? ta = null, tb = null;
                        if (TerrainFill.Mode is 'C' or 'D') { ta = Tan(rd.Node.Rotation); tb = Tan(rd.ForwardNode.Rotation); }
                        if (TerrainFill.ShapeRoadSide(rd.Left.Terrain, tg, pa, pb, rd.Left.Terrain.Size, -1, e, ta, tb) < 0) bad++;
                        if (TerrainFill.ShapeRoadSide(rd.Right.Terrain, tg, pa, pb, rd.Right.Terrain.Size, +1, e, ta, tb) < 0) bad++;
                        shaped++;
                        if (rd == lastItem[entry]) break;
                    }
                }
                warnings.Insert(0, $"Info: Straßengelände an Höhenraster angepasst: {shaped} Stücke, Raster unpassend: {bad}");
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
                else pl.RampPrefab.Attach(pl.RampNode, lastItem[plan.Ramp].ForwardNode);
                Interchanges.FixRotations(pl);   // Attach übernimmt sonst die gekippte Prefab-Knotendrehung
                foreach (var pf in new[] { pl.First, pl.Last })
                    RegisterSector(map, pf.Nodes[0].Position);
                prefabCount += 2;
            }
            catch (Exception ex)
            {
                warnings.Add($"Knoten {plan.OsmNode} ({plan.Kind}): Prefab nicht gesetzt – {ex.GetType().Name}: {ex.Message}");
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
                }
                catch { map.Delete(pf); throw; }
                RegisterSector(map, pf.Nodes[0].Position);
                foreach (var n in pf.Nodes) RegisterSector(map, n.Position);
                junctionsSet++;
            }
            catch (Exception ex)
            {
                warnings.Add($"Knoten {jp.OsmNode} (Kreuzung): Prefab nicht gesetzt – {ex.GetType().Name}: {ex.Message}");
            }
        }
        if (prefabs is not null)
            warnings.Insert(0, $"Info: Kreuzungen mit Prefabs: {junctionPlans.Count} geplant, {junctionsSet} gesetzt");
        if (prefabs is not null)
            warnings.Insert(0, $"Info: Anschlussstellen mit Prefabs: {interchangePlans.Count} geplant, {prefabCount / 2} gesetzt");

        // Füllgelände zuletzt: dann sind alle Prefabs gesetzt und ihre Fahrspuren bekannt
        if (input.TerrainGrid is { Nx: > 1, Nz: > 1 } tg2 && Environment.GetEnvironmentVariable("OSM2ETS_NOFILL") is null)
        {
            var lanes = PrefabFootprint.All(map);
            var tf = TerrainFill.Build(map, tg2, valid, styles, obstacles, lanes);
            warnings.Insert(0, $"Info: flächiges Gelände: {tf.Items} Terrain-Stücke in {tf.Rows} Reihen, {tf.AreaKm2:0.0} km², Raster unpassend: {TerrainFill.Mismatch}, freie Fläche ohne Gelände: {TerrainFill.Uncovered:P1}, Prefab-Spurpunkte: {lanes.Count} (nicht zuordenbar: {PrefabFootprint.Unfitted})");
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
        return (map, new WriteResult(written, skipped, items, styleChanges, startPos, startRoad?.Id, warnings,
                                     connections, reversed, terrainClamped, overlaps, branchesTrimmed));
    }

    /// <summary>
    /// Abschnitte länger als maxLen teilen. Neue Punkte auf einer Catmull-Rom-Kurve durch
    /// die Nachbarpunkte (nähert die Spielkurve), Höhe linear. Runs werden umnummeriert.
    /// </summary>
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
        foreach (var run in r.Runs) { run.From = map[run.From]; run.To = map[run.To]; }
        r.Points = np;
    }

    public const float IcTerrainGap = 30f;

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
        // Standard wären 400 m – bei langen Stücken verschwinden ferne Straßen,
        // während der Verkehr darauf sichtbar bleibt ("schwebende" Fahrzeuge)
        road.ViewDistance = 1400; // TruckLib KdopItem.ViewDistanceExtremelyFar
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
