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
    public static readonly PrefabDef Split = new("ch_4202p", "/prefab2/fork_temp/ger/ger_hw2_exit_joint_mirrored_tmpl_v02.ppd", "default", "ger_1");
    public static readonly PrefabDef Join = new("ch_4200c", "/prefab2/fork_temp/ger/ger_hw2_exit_joint_tmpl_v02.ppd", "default", "ger_1");
    public static readonly PrefabDef Narrow = new("ch_4202q", "/prefab2/fork_temp/ger/ger_hw2-hw2_exit_tmpl_v02.ppd", "default", "ger_1");

    // Knotenrollen je Prefab (Index in der .ppd)
    private const ushort WidenIn = 0, WidenOut = 1;
    private const ushort SplitIn = 0, SplitMain = 1, SplitRamp = 2;
    private const ushort JoinMain = 0, JoinOut = 1, JoinRamp = 2;
    private const ushort NarrowIn = 0, NarrowOut = 1;

    public const float RampTrim = 25f;     // Rampe so weit vom Knoten kürzen (m)
    public const float Slack = 5f;

    public sealed class Plan
    {
        public required string Kind;        // "exit" | "entry"
        public required long OsmNode;
        public required RoadEntry MainIn, MainOut, Ramp;
        public required Vector3 Anchor;     // Lage des OSM-Knotens
        public required Vector3 Direction;  // Fahrtrichtung der Hauptfahrbahn (x/z)
        public float Grade;                  // Steigung der Hauptfahrbahn über die Prefab-Länge (m/m)
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
            if (list.Count != 3) continue;
            var mains = list.Where(e => IsMotorway2(e.R)).ToList();
            var ramps = list.Where(e => IsRamp(e.R)).ToList();
            if (mains.Count != 2 || ramps.Count != 1) continue;
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
                Kind = ramp.AtStart ? "exit" : "entry",
                OsmNode = node, MainIn = mainIn, MainOut = mainOut, Ramp = ramp.R,
                Anchor = anchor, Direction = dir,
            });
        }
        return plans;
    }

    private static bool IsMotorway2(RoadEntry r) =>
        r.Runs.All(x => x.Oneway && x.Highway == "motorway") && r.Runs.All(x => x.Lanes is null or 2);

    private static bool IsRamp(RoadEntry r) =>
        r.Runs.All(x => x.Oneway && x.Highway is "motorway_link" or "trunk_link");

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
    /// Annahme: aufgeweitete Prefabs je etwa 9 m halbe Breite.</summary>
    public const float OppositeMinGap = 19f;

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

    private static Vector3 Flat(Vector3 v, Vector3 fallback)
    {
        v.Y = 0;
        if (v.LengthSquared() < 1e-6f) return fallback;
        v = Vector3.Normalize(v);
        return Vector3.Dot(v, fallback) < 0 ? -v : v;
    }

    public sealed record Placed(Plan Plan, Prefab First, ushort FirstIn, Prefab Last, ushort LastOut, Prefab RampPrefab, ushort RampNode);

    /// <summary>Kürzt die Straßen für einen Plan; false = Plan nicht umsetzbar (Straßen zu kurz).</summary>
    public static bool Prepare(Plan p, PpdSource src)
    {
        // erst prüfen, dann kürzen – sonst bliebe bei Abbruch eine Lücke
        if (p.Kind == "exit")
        {
            float len = Length(src, Widen, WidenIn, WidenOut) + Length(src, Split, SplitIn, SplitMain);
            if (ArcLength(p.MainIn) < len + Slack + 20 || ArcLength(p.Ramp) < RampTrim + 20) return false;
            p.Grade = (p.Anchor.Y - HeightFromEnd(p.MainIn, len)) / len;
            return TrimEnd(p.MainIn, len + Slack) && TrimStart(p.Ramp, RampTrim);
        }
        else
        {
            float len = Length(src, Join, JoinMain, JoinOut) + Length(src, Narrow, NarrowIn, NarrowOut);
            if (ArcLength(p.MainOut) < len + Slack + 20 || ArcLength(p.Ramp) < RampTrim + 20) return false;
            p.Grade = (HeightFromStart(p.MainOut, len) - p.Anchor.Y) / len;
            return TrimStart(p.MainOut, len + Slack) && TrimEnd(p.Ramp, RampTrim);
        }
    }

    /// <summary>Setzt die Prefabs eines Plans (Straßen müssen schon gebaut sein).</summary>
    public static Placed Build(Map map, Plan p, PpdSource src)
    {
        if (p.Kind == "exit")
        {
            // Teilung so, dass ihr Hauptausgang auf dem OSM-Knoten liegt; Aufweitung davor
            var split = Place(map, src, Split, SplitIn, SplitMain, p.Direction, SplitMain, p.Anchor, p.Grade);
            // Richtung an der Fuge aus dem gesetzten Nachbar-Prefab übernehmen (sonst Keil in der Fuge)
            var dSplit = Flat(InwardDirections[split][SplitIn], p.Direction);
            var widen = Place(map, src, Widen, WidenIn, WidenOut, dSplit, WidenOut, split.Nodes[SplitIn].Position, p.Grade);
            widen.Attach(WidenOut, split, SplitIn);
            return new Placed(p, widen, WidenIn, split, SplitMain, split, SplitRamp);
        }
        else
        {
            var join = Place(map, src, Join, JoinMain, JoinOut, p.Direction, JoinMain, p.Anchor, p.Grade);
            var dJoin = Flat(-InwardDirections[join][JoinOut], p.Direction);
            var narrow = Place(map, src, Narrow, NarrowIn, NarrowOut, dJoin, NarrowIn, join.Nodes[JoinOut].Position, p.Grade);
            join.Attach(JoinOut, narrow, NarrowIn);
            return new Placed(p, join, JoinMain, narrow, NarrowOut, join, JoinRamp);
        }
    }

    // ------------------------------------------------------------------ tangentialer Anschluss

    public const float Lead = 20f;   // so lang läuft die Straße gerade in den Prefab-Knoten ein

    /// <summary>Lässt eine Straße genau am Prefab-Knoten enden und in dessen Richtung einlaufen.</summary>
    public static void SetTail(RoadEntry r, Vector3 node, Vector3 dirInto, float leadLen = Lead, float curve = 0)
    {
        var lead = node - dirInto * leadLen;
        int k = r.Points.Count - 1;
        float cut = MathF.Max(leadLen + 10, curve);
        while (k > 0 && Vector2.Distance(P(r.Points[k]), new Vector2(node.X, node.Z)) < cut) k--;
        // Punkte, die schon jenseits des Einlaufpunkts liegen, ebenfalls weg (sonst Rücklauf)
        while (k > 0 && Vector2.Dot(P(r.Points[k]) - new Vector2(lead.X, lead.Z), new Vector2(dirInto.X, dirInto.Z)) > -2) k--;
        var pts = r.Points.Take(k + 1).ToList();
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
        var runs = r.Runs.Where(x => x.From < k || r.Runs.Count == 1).Select(x => { x.To = Math.Min(x.To, k); return x; }).ToList();
        if (runs.Count == 0) runs = [r.Runs[^1]];
        runs[^1].To = segs;
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
        var rest = r.Points.Skip(k).ToList();
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
        var runs = r.Runs.Where(x => x.To > k).Select(x => { x.From = Math.Max(0, x.From + shift); x.To += shift; return x; }).ToList();
        if (runs.Count == 0) runs = [r.Runs[0]];
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
    public static void Adjust(Placed pl)
    {
        var p = pl.Plan;
        var inNode = pl.First.Nodes[pl.FirstIn];
        var outNode = pl.Last.Nodes[pl.LastOut];
        var rampNode = pl.RampPrefab.Nodes[pl.RampNode];
        // ppd-Richtungen zeigen in das Prefab hinein
        SetTail(p.MainIn, inNode.Position, InwardDirections[pl.First][pl.FirstIn]);
        SetHead(p.MainOut, outNode.Position, -InwardDirections[pl.Last][pl.LastOut]);
        var rampIn = InwardDirections[pl.RampPrefab][pl.RampNode];
        if (p.Kind == "exit") SetHead(p.Ramp, rampNode.Position, -rampIn);
        else SetTail(p.Ramp, rampNode.Position, rampIn);

        // Knotendrehungen auf die Fahrtrichtung setzen (aus der ppd-Geometrie). TruckLib
        // liefert für schräge Knoten nach dem Drehen eines Prefabs eine gespiegelte
        // Richtung; die Straßenkurve würde sonst am Knoten abknicken.
        inNode.Rotation = MathEx.GetNodeRotation(Sloped(InwardDirections[pl.First][pl.FirstIn], p.Grade));
        outNode.Rotation = MathEx.GetNodeRotation(Sloped(-InwardDirections[pl.Last][pl.LastOut], p.Grade));
        rampNode.Rotation = MathEx.GetNodeRotation(p.Kind == "exit" ? -rampIn : rampIn);
    }
}
