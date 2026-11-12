// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;

namespace Osm2Ets.Writer;

/// <summary>
/// Brückenkörper (Überbau samt Widerlagern) wie in der SCS-Karte: ein Railing-Modell an EINER
/// Richtungsfahrbahn eines Paars paralleler Autobahnbrücken über eine Straße, zur Gegenfahrbahn hin
/// versetzt (halber Abstand der Straßenlinien + <see cref="BridgeBodySpec.ExtraOffset"/>; in der
/// SCS-Karte 11 + 2 = 13 m). Das Modell setzt seine Widerlager an Anfang und Ende des
/// Straßenstücks – die Brücke dieser Fahrbahn wird deshalb ein einziges Stück.
/// Ob Anfangs-/Endstücke auch an Stückgrenzen innerhalb einer Brücke erschienen, ist ungeprüft;
/// gekrümmte Brücken (Punkte weiter als <see cref="MaxChordDeviation"/> neben der Sehne) bleiben
/// deshalb ohne Körper.
/// </summary>
public static class BridgeBodies
{
    public sealed record Body(RoadEntry Road, int Segment, float Offset, string Partner,
                              RunEntry? Run = null, RunEntry? PartnerRun = null);

    /// <summary>Körper für eine einzelne Brücke: alle Abschnitte des Laufs (gerade Brücken als ein Stück).</summary>
    public sealed record Single(RoadEntry Road, RunEntry Run, BridgeBodyType Type, bool Merged);

    public const float MaxChordDeviation = 1.5f;

    private sealed record Span(RoadEntry R, RunEntry Run, Vector3 A, Vector3 B)
    {
        public Vector2 A2 => new(A.X, A.Z);
        public Vector2 B2 => new(B.X, B.Z);
        public float Len => Vector2.Distance(A2, B2);
        public Vector2 Dir => Vector2.Normalize(B2 - A2);
        public Vector2 Mid => (A2 + B2) / 2;
    }

    public static List<Body> Plan(List<RoadEntry> roads, BridgeBodySpec spec, List<string> warnings)
    {
        var res = new List<Body>();
        // alle Abschnitte für die Prüfung „über eine Straße“
        var segs = new List<(RoadEntry R, Vector3 A, Vector3 B)>();
        foreach (var r in roads)
            for (int i = 0; i < r.Points.Count - 1; i++)
                segs.Add((r, MapWriter.ToVector(r.Points[i]), MapWriter.ToVector(r.Points[i + 1])));

        var spans = new List<Span>();
        foreach (var r in roads)
            foreach (var run in r.Runs.Where(x => x.Bridge && !x.Tunnel && x.Oneway &&
                         (x.Highway is "motorway" or "trunk" || x.Role is Interchanges.ExtRole or "ext3")))
            {
                var a = MapWriter.ToVector(r.Points[run.From]); var b = MapWriter.ToVector(r.Points[run.To]);
                var sp = new Span(r, run, a, b);
                if (sp.Len < 5 || sp.Len > spec.MaxLength) continue;
                if (!OverRoad(r, run, segs)) continue;
                spans.Add(sp);
            }

        var used = new HashSet<Span>();
        foreach (var a in spans.OrderBy(s => s.R.Id).ThenBy(s => s.Run.From))
        {
            if (used.Contains(a)) continue;
            Span? best = null; float bestD = float.MaxValue;
            foreach (var b in spans)
            {
                if (b == a || used.Contains(b) || ReferenceEquals(b.R, a.R)) continue;
                if (Vector2.Dot(a.Dir, b.Dir) > -0.9f) continue;          // Gegenrichtung
                var right = new Vector2(-a.Dir.Y, a.Dir.X);
                float lat = Vector2.Dot(b.Mid - a.A2, right);
                if (lat >= 0) continue;                                   // Gegenfahrbahn links (Rechtsverkehr)
                float d = -lat;
                if (d < spec.MinGap || d > spec.MaxGap) continue;
                // beide Brücken beginnen und enden etwa gleich (der Körper spannt über beide und setzt
                // seine Widerlager an die Enden des Trägers)
                float s0 = Vector2.Dot(b.A2 - a.A2, a.Dir), s1 = Vector2.Dot(b.B2 - a.A2, a.Dir);
                if (MathF.Abs(MathF.Min(s0, s1)) > spec.MaxEndOffset || MathF.Abs(MathF.Max(s0, s1) - a.Len) > spec.MaxEndOffset) continue;
                if (d < bestD) { bestD = d; best = b; }
            }
            if (best is null) continue;
            used.Add(a); used.Add(best);
            // Träger: die tiefere Fahrbahn – die andere liegt dann auf dem Überbau statt darin
            var carrier = (a.A.Y + a.B.Y) <= (best.A.Y + best.B.Y) ? a : best;
            var other = ReferenceEquals(carrier, a) ? best : a;
            if (!Straight(carrier))
            {
                warnings.Add($"Info: Brücke {carrier.R.Id} gekrümmt – ohne Brückenkörper");
                continue;
            }
            int seg = Merge(carrier.R, carrier.Run);
            res.Add(new Body(carrier.R, seg, bestD / 2 + spec.ExtraOffset, other.R.Id, carrier.Run, other.Run));
        }
        return res;
    }

    /// <summary>
    /// Brückenkörper für alle übrigen Brücken: je Straßentyp ein Railing mit Überbau, Widerlagern
    /// und Brüstung (SCS: shared_2lanes_x_1way, shared_3lanes_x_1way, shared_hw_exit, shared_simple).
    /// Gerade Brücken werden ein Stück, damit die Widerlager sicher an ihren Enden sitzen. Gekrümmte
    /// (auch in der Höhe: Kuppe über einer Straße – als ein Stück fehlte dort bis zu 1,3 m Lichtraum)
    /// bleiben mehrere Stücke mit demselben Körper; angenommen, das Spiel setzt Widerlager nur an
    /// die Enden einer durchgehenden Railing-Folge (ungeprüft). <paramref name="covered"/>: Läufe mit
    /// Paar-Körper.
    /// </summary>
    public static List<Single> PlanSingle(List<RoadEntry> roads, Func<RunEntry, ResolvedStyle> resolve,
                                          Func<ResolvedStyle, BridgeBodyType?> bodyFor, HashSet<RunEntry> covered,
                                          float maxLength, List<string> warnings,
                                          List<RoadEntry>? extraLower = null)
    {
        var res = new List<Single>();
        int tooLong = 0, extended = 0, jointsMoved = 0, pushedRoads = 0;
        float pushedMax = 0;
        var stillBlocked = new List<string>();
        float extendedM = 0, extendedMax = 0;
        // alle Abschnitte mit Lauf (untere Straßen für die Verlängerung schräg unterquerter Brücken)
        var segs = new List<(RoadEntry R, RunEntry Run, Vector3 A, Vector3 B)>();
        foreach (var r in roads.Concat(extraLower ?? []))
            for (int i = 0; i < r.Points.Count - 1; i++)
                if (r.Runs.FirstOrDefault(x => x.From <= i && i < x.To) is { } run)
                    segs.Add((r, run, MapWriter.ToVector(r.Points[i]), MapWriter.ToVector(r.Points[i + 1])));
        foreach (var r in roads)
            foreach (var run in r.Runs.Where(x => x.Bridge && !x.Tunnel && x.To > x.From).ToList())
            {
                if (covered.Contains(run)) continue;
                var style = resolve(run);
                var type = bodyFor(style);
                if (type is null) continue;
                var sp = new Span(r, run, MapWriter.ToVector(r.Points[run.From]), MapWriter.ToVector(r.Points[run.To]));
                if (sp.Len < 3f) continue;
                if (sp.Len > maxLength) { tooLong++; continue; }
                if (UseExtend)
                {
                    float half = type.HalfWidth ?? style.Width / 2 + MathF.Abs(style.ModelCenter) + 1.5f;
                    var interior0 = Enumerable.Range(run.From + 1, Math.Max(0, run.To - run.From - 1))
                                              .Select(k => MapWriter.ToVector(r.Points[k])).ToList();
                    var (e0, e1) = Extend(r, run, half, resolve, segs);
                    // zusätzlich: Widerlager-Fläche an den Enden frei von tieferen Straßen, auch von
                    // solchen, die die Brückenlinie nicht kreuzen (Kurve unter dem Brückenende)
                    foreach (bool atStart in new[] { true, false })
                    {
                        // Schrittweise suchen, dann in EINEM Schritt verlängern: jeder Schritt fügt einen
                        // Punkt ein, und an jedem Stoß setzt ETS2 ein Widerlager (2-m-Stücke)
                        var snapPts = r.Points.Select(p => (double[])p.Clone()).ToList();
                        var snapRuns = r.Runs.Select(x => (x.From, x.To)).ToList();
                        float add = 0;
                        while (add < MaxExtendFoot && EndBlocked(r, run, atStart, half, resolve, segs))
                        {
                            float got = atStart ? ExtendStart(r, run, FootStep) : ExtendEnd(r, run, FootStep);
                            if (got < 0.3f) break;
                            add += got;
                        }
                        if (add > 0)
                        {
                            r.Points = snapPts;
                            for (int q = 0; q < r.Runs.Count; q++) { r.Runs[q].From = snapRuns[q].From; r.Runs[q].To = snapRuns[q].To; }
                            add = atStart ? ExtendStart(r, run, add) : ExtendEnd(r, run, add);
                            Refresh(r, segs);
                        }
                        if (atStart) e0 += add; else e1 += add;
                    }
                    if (e0 + e1 > 0)
                    {
                        // Punkte, die erst durch das Verlängern in den Brückenlauf kamen, wieder heraus:
                        // jeder innere Punkt ist ein Stoß, und dort setzt ETS2 ein Widerlager
                        for (int k = run.To - 1; k > run.From; k--)
                        {
                            var pk = MapWriter.ToVector(r.Points[k]);
                            if (interior0.Any(q => Vector3.DistanceSquared(q, pk) < 1e-4f)) continue;
                            r.Points.RemoveAt(k);
                            foreach (var x in r.Runs)
                            {
                                if (ReferenceEquals(x, run)) { x.To--; continue; }
                                if (x.From >= k) { x.From--; x.To--; }
                            }
                        }
                        Refresh(r, segs);
                        extended++; extendedM += e0 + e1; extendedMax = MathF.Max(extendedMax, MathF.Max(e0, e1));
                        if (Environment.GetEnvironmentVariable("OSM2ETS_BPROBE") is not null)
                            Console.Error.WriteLine($"EXTEND {r.Id} [{run.From}-{run.To}] {string.Join("/", run.OsmWays)} +{e0:0.0} +{e1:0.0}");
                        sp = new Span(r, run, MapWriter.ToVector(r.Points[run.From]), MapWriter.ToVector(r.Points[run.To]));
                    }
                }
                if (UseExtend)
                {
                    float half = type.HalfWidth ?? style.Width / 2 + MathF.Abs(style.ModelCenter) + 1.5f;
                    foreach (bool atStart in new[] { true, false })
                    {
                        if (!EndBlocked(r, run, atStart, half, resolve, segs)) continue;
                        // Ende lässt sich nicht weiter verlängern (z. B. an einer Kreuzung): die tiefere
                        // Straße unter dem Brückenende ein Stück unter die Brücke schieben
                        float moved = PushLower(r, run, atStart, half, resolve, segs);
                        if (moved > 0) { pushedRoads++; pushedMax = MathF.Max(pushedMax, moved); }
                        if (EndBlocked(r, run, atStart, half, resolve, segs))
                        {
                            var e = r.Points[atStart ? run.From : run.To];
                            stillBlocked.Add($"({e[0]:0},{e[2]:0})");
                        }
                    }
                }
                bool straight = Straight(sp);
                if (straight) Merge(r, run);
                else jointsMoved += DropJointsOverRoads(r, run, resolve, segs);
                res.Add(new Single(r, run, type, straight));
            }
        if (pushedRoads > 0)
            warnings.Add($"Info: {pushedRoads} tiefere Straßen unter Brückenenden vom Widerlager weggeschoben, höchstens {pushedMax:0.0} m");
        if (stillBlocked.Count > 0)
            warnings.Add($"Warnung: Widerlager an {stillBlocked.Count} Brückenenden noch zu nah an einer tieferen Straße: {string.Join(", ", stillBlocked)}");
        if (jointsMoved > 0)
            warnings.Add($"Info: {jointsMoved} Stoßstellen gekrümmter Brücken über unteren Straßen entfernt (dort setzt ETS2 ein Widerlager)");
        if (tooLong > 0)
            warnings.Add($"Info: ohne Brückenkörper: {tooLong} Brücken länger als {maxLength:0} m");
        if (extended > 0)
            warnings.Add($"Info: {extended} Brücken über schräg oder dicht am Widerlager kreuzende Straßen verlängert, " +
                         $"zusammen {extendedM:0} m, je Ende höchstens {extendedMax:0.0} m");
        return res;
    }

    /// <summary>Stärke der Widerlager an den Enden des Brückenstücks (gemessen an den shared_…-Modellen: rund 1,6 m).</summary>
    public const float Wall = 1.6f;
    /// <summary>Freier Raum zwischen Modellrand einer unteren Straße und Widerlager (m).</summary>
    public const float Clearance = 3f;
    /// <summary>Höchstens so weit wird eine Brücke je Ende verlängert (m).</summary>
    public const float MaxExtend = 15f;
    /// <summary>So viel Straße ohne Bauwerk bleibt vor bzw. hinter einer verlängerten Brücke mindestens (m).</summary>
    public const float MinApproach = 8f;
    /// <summary>Kein neues Straßenstück kürzer als das (MapWriter.MinSegment 1,25 m + Reserve).</summary>
    private const float MinPiece = 1.6f;
    /// <summary>Wie <see cref="MinApproach"/>, wenn die Zufahrt am Anfang bzw. Ende der Straße liegt
    /// (Wegbrücke über die A-70-Rampen: Zufahrt 10,7 m bis zur Kreuzung, die verbreiterte Rampe lag
    /// sonst unter dem Damm). Aus: OSM2ETS_BRIDGE_MINAPPROACH_END=8.</summary>
    public static readonly float MinApproachAtEnd = float.TryParse(Environment.GetEnvironmentVariable("OSM2ETS_BRIDGE_MINAPPROACH_END"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mae) ? mae : 2f;
    /// <summary>Verlängerung schräg unterquerter Brücken (aus: OSM2ETS_NO_BRIDGE_EXTEND).</summary>
    public static bool UseExtend = Environment.GetEnvironmentVariable("OSM2ETS_NO_BRIDGE_EXTEND") is null;
    /// <summary>Auch untere Straßen knapp jenseits der Brückenenden zählen (aus: OSM2ETS_BRIDGE_EXTEND_INSIDE=1).</summary>
    public static bool UseExtendBeyond = Environment.GetEnvironmentVariable("OSM2ETS_BRIDGE_EXTEND_INSIDE") != "1";

    /// <summary>
    /// Die Widerlager der Körper-Modelle stehen immer quer zur Brücke (ein Railing folgt der Straße und
    /// lässt sich nicht schräg stellen). Kreuzt die untere Straße schräg, wandert sie zu den Brückenkanten
    /// hin um <paramref name="half"/>·cot α zur Seite – bei kurzen Brücken bis ans Widerlager. Die
    /// Brücke wird dann an den Enden in die Straße davor bzw. dahinter verlängert (Punkt einfügen), bis
    /// jede untere Straße an allen vier Ecken <see cref="Clearance"/> Platz hat. Gibt die Verlängerung
    /// am Anfang und am Ende zurück (m).
    /// </summary>
    private static (float Start, float End) Extend(RoadEntry r, RunEntry run, float half, Func<RunEntry, ResolvedStyle> resolve,
                                                   List<(RoadEntry R, RunEntry Run, Vector3 A, Vector3 B)> segs)
    {
        float lo = float.MaxValue, hi = float.MinValue, acc = 0;
        int k0 = run.From, k1 = run.To - 1;
        for (int k = run.From; k < run.To; k++)
        {
            var p = MapWriter.ToVector(r.Points[k]); var q = MapWriter.ToVector(r.Points[k + 1]);
            var p2 = new Vector2(p.X, p.Z); var q2 = new Vector2(q.X, q.Z);
            float sl = Vector2.Distance(p2, q2);
            if (sl < 1e-3f) continue;
            var d1 = (q2 - p2) / sl;
            // erstes/letztes Stück: auch untere Straßen knapp VOR bzw. HINTER dem Brückenende (deren
            // Linie kreuzt die Zufahrt, ihr Modell reicht aber unter die Brücke) – sonst steht die
            // Zufahrt samt Damm auf der Fahrbahn (verbreiterte Rampe unter der Wegbrücke, A 70)
            float tMin = UseExtendBeyond && k == k0 ? -MaxExtend / sl : 0f;
            float tMax = UseExtendBeyond && k == k1 ? 1 + MaxExtend / sl : 1f;
            foreach (var (o, orun, a, b) in segs)
            {
                if (ReferenceEquals(o, r) || orun.Tunnel) continue;
                var a2 = new Vector2(a.X, a.Z); var b2 = new Vector2(b.X, b.Z);
                if ((b2 - a2).LengthSquared() < 1e-6f) continue;
                var ab2 = b2 - a2;
                if (MathF.Abs(d1.X * ab2.Y - d1.Y * ab2.X) < 1e-4f * ab2.Length()) continue;   // parallel
                Cross(p2, q2, a2, b2, out float t, out float u);
                if (u < 0 || u > 1 || t < tMin || t > tMax || float.IsNaN(t)) continue;
                if ((t < 0 || t > 1) && orun.Bridge) continue;   // jenseits des Endes: andere Brücke zählt nicht
                float yb = p.Y + (q.Y - p.Y) * t, yl = a.Y + (b.Y - a.Y) * u;
                if (yb - yl < Underpasses.MinClear) continue;
                var d2 = Vector2.Normalize(b2 - a2);
                var st = resolve(orun);
                float hw = st.Width / 2 + MathF.Abs(st.ModelCenter);
                float need = (hw + Clearance) / Underpasses.SinOf(d1, d2) + Wall + half * MathF.Abs(Underpasses.CotOf(d1, d2));
                float s = acc + sl * t;
                lo = MathF.Min(lo, s - need);
                hi = MathF.Max(hi, s + need);
            }
            acc += sl;
        }
        if (lo == float.MaxValue) return (0, 0);
        float e0 = ExtendStart(r, run, MathF.Min(MathF.Max(0, -lo), MaxExtend));
        float e1 = ExtendEnd(r, run, MathF.Min(MathF.Max(0, hi - acc), MaxExtend));
        return (e0, e1);
    }

    /// <summary>Rückt den Anfang des Brückenlaufs um bis zu e Meter in den Lauf davor (ohne Bauwerk,
    /// davon bleiben mindestens <see cref="MinApproach"/>). Gibt die Verlängerung zurück.</summary>
    private static float ExtendStart(RoadEntry r, RunEntry run, float e)
    {
        if (e < 0.3f) return 0;
        var prev = r.Runs.FirstOrDefault(x => x.To == run.From && x.To > x.From);
        if (prev is null || prev.Bridge || prev.Tunnel) return 0;
        float avail = 0;
        for (int k = prev.From; k < prev.To; k++) avail += Dist2(r.Points[k], r.Points[k + 1]);
        // am Straßenanfang (Knoten) genügt ein kurzes Stück – die Zufahrt endet dort ohnehin
        e = MathF.Min(e, avail - (prev.From == 0 ? MinApproachAtEnd : MinApproach));
        if (e < 0.3f) return 0;
        // Abschnitt j..j+1, in dem die neue Grenze liegt (back = Abstand vor Punkt j+1)
        float acc = 0;
        int j = run.From - 1;
        while (j > prev.From && acc + Dist2(r.Points[j], r.Points[j + 1]) < e) { acc += Dist2(r.Points[j], r.Points[j + 1]); j--; }
        float len = Dist2(r.Points[j], r.Points[j + 1]), back = e - acc;
        if (back < MinPiece) { SetBoundary(prev, run, j + 1); return acc; }
        if (len - back < MinPiece && j > prev.From) { SetBoundary(prev, run, j); return acc + len; }
        InsertPoint(r, j + 1, Lerp(r.Points[j], r.Points[j + 1], 1 - back / len));
        SetBoundary(prev, run, j + 1);
        return e;
    }

    /// <summary>Wie <see cref="ExtendStart"/> am Ende des Brückenlaufs.</summary>
    private static float ExtendEnd(RoadEntry r, RunEntry run, float e)
    {
        if (e < 0.3f) return 0;
        var next = r.Runs.FirstOrDefault(x => x.From == run.To && x.To > x.From);
        if (next is null || next.Bridge || next.Tunnel) return 0;
        float avail = 0;
        for (int k = next.From; k < next.To; k++) avail += Dist2(r.Points[k], r.Points[k + 1]);
        e = MathF.Min(e, avail - (next.To == r.Points.Count - 1 ? MinApproachAtEnd : MinApproach));
        if (e < 0.3f) return 0;
        // Abschnitt j..j+1, in dem die neue Grenze liegt (fwd = Abstand hinter Punkt j)
        float acc = 0;
        int j = run.To;
        while (j < next.To - 1 && acc + Dist2(r.Points[j], r.Points[j + 1]) < e) { acc += Dist2(r.Points[j], r.Points[j + 1]); j++; }
        float len = Dist2(r.Points[j], r.Points[j + 1]), fwd = e - acc;
        if (fwd < MinPiece) { SetBoundary(run, next, j); return acc; }
        if (len - fwd < MinPiece && j + 1 < next.To) { SetBoundary(run, next, j + 1); return acc + len; }
        InsertPoint(r, j + 1, Lerp(r.Points[j], r.Points[j + 1], fwd / len));
        SetBoundary(run, next, j + 1);
        return e;
    }

    /// <summary>Grenze zwischen zwei aufeinanderfolgenden Läufen auf Punkt k legen.</summary>
    private static void SetBoundary(RunEntry before, RunEntry after, int k)
    {
        before.To = k;
        after.From = k;
    }

    /// <summary>Punkt an Stelle at einfügen; Laufgrenzen ab dort rücken um eins.</summary>
    private static void InsertPoint(RoadEntry r, int at, double[] p)
    {
        r.Points.Insert(at, p);
        foreach (var x in r.Runs)
        {
            if (x.From >= at) x.From++;
            if (x.To >= at) x.To++;
        }
    }

    private static double[] Lerp(double[] a, double[] b, float t) =>
        [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];

    private static float Dist2(double[] a, double[] b) =>
        (float)Math.Sqrt(Math.Pow(b[0] - a[0], 2) + Math.Pow(b[2] - a[2], 2));

    /// <summary>Überquert die Brücke eine andere Straße mindestens MinClear tiefer?</summary>
    private static bool OverRoad(RoadEntry r, RunEntry run, List<(RoadEntry R, Vector3 A, Vector3 B)> segs)
    {
        for (int k = run.From; k < run.To; k++)
        {
            var p = MapWriter.ToVector(r.Points[k]); var q = MapWriter.ToVector(r.Points[k + 1]);
            foreach (var (o, a, b) in segs)
            {
                if (ReferenceEquals(o, r)) continue;
                if (!Cross(new(p.X, p.Z), new(q.X, q.Z), new(a.X, a.Z), new(b.X, b.Z), out float t, out float u)) continue;
                float yb = p.Y + (q.Y - p.Y) * t, yl = a.Y + (b.Y - a.Y) * u;
                if (yb - yl >= Underpasses.MinClear) return true;
            }
        }
        return false;
    }

    private static bool Straight(Span s)
    {
        for (int k = s.Run.From + 1; k < s.Run.To; k++)
        {
            var p = MapWriter.ToVector(s.R.Points[k]);
            var w = new Vector2(p.X, p.Z) - s.A2;
            float t = Math.Clamp(Vector2.Dot(w, s.Dir) / s.Len, 0, 1);
            var c = s.A2 + s.Dir * (t * s.Len);
            if (Vector2.Distance(new Vector2(p.X, p.Z), c) > MaxChordDeviation) return false;
            if (MathF.Abs(p.Y - (s.A.Y + (s.B.Y - s.A.Y) * t)) > 0.5f) return false;
        }
        return true;
    }

    /// <summary>
    /// ETS2 setzt an JEDEM Ende eines Straßenstücks mit Körper-Railing ein Widerlager, auch zwischen
    /// zwei Stücken derselben Brücke (Wegbrücke über die Rampe am Dreieck Bayreuth/Kulmbach: Block
    /// mitten auf der Fahrbahn). Gekrümmte Brücken bleiben mehrteilig, aber kein innerer Punkt darf
    /// über einer unteren Straße (Modellbreite + Widerlager + Abstand) liegen.
    /// </summary>
    private static int DropJointsOverRoads(RoadEntry r, RunEntry run, Func<RunEntry, ResolvedStyle> resolve,
                                           List<(RoadEntry R, RunEntry Run, Vector3 A, Vector3 B)> segs)
    {
        int removed = 0;
        for (int k = run.To - 1; k > run.From; k--)
        {
            var p = MapWriter.ToVector(r.Points[k]);
            var p2 = new Vector2(p.X, p.Z);
            var pn = MapWriter.ToVector(r.Points[k + 1]);
            var dd = new Vector2(pn.X - p.X, pn.Z - p.Z);
            bool over = dd.LengthSquared() > 1e-6f && FootBlocked(r, p, Vector2.Normalize(dd), JointHalf, -Wall, Wall, resolve, segs);
            if (!over) continue;
            // nur, wenn die Höhe dort kaum fällt (sonst fehlt über der Kuppe Lichtraum)
            var pa = MapWriter.ToVector(r.Points[k - 1]); var pb = MapWriter.ToVector(r.Points[k + 1]);
            float la = Vector2.Distance(new(pa.X, pa.Z), p2), lb = Vector2.Distance(p2, new(pb.X, pb.Z));
            float yc = pa.Y + (pb.Y - pa.Y) * la / MathF.Max(la + lb, 1e-3f);
            if (p.Y - yc > 0.05f)
            {
                // Kuppe: Stoß stattdessen entlang der Brücke an eine freie Stelle schieben
                if (MoveJoint(r, k, resolve, segs)) removed++;
                continue;
            }
            r.Points.RemoveAt(k);
            foreach (var x in r.Runs)
            {
                if (ReferenceEquals(x, run)) { x.To--; continue; }
                if (x.From >= k) { x.From--; x.To--; }
            }
            removed++;
        }
        return removed;
    }

    public const float MaxExtendFoot = 30f, FootStep = 2f, FootClear = 1f, FootOut = -Wall - 1f, FootIn = Wall + Clearance - FootClear - 0.05f;
    /// <summary>Liegt die Fläche des Widerlagers am Anfang/Ende des Laufs (quer über die Brückenbreite,
    /// <see cref="Wall"/> tief) auf oder dicht an einer mindestens 2,5 m tieferen Straße?</summary>
    private static bool EndBlocked(RoadEntry r, RunEntry run, bool atStart, float half, Func<RunEntry, ResolvedStyle> resolve,
                                   List<(RoadEntry R, RunEntry Run, Vector3 A, Vector3 B)> segs)
    {
        int k = atStart ? run.From : run.To, k2 = atStart ? run.From + 1 : run.To - 1;
        var e = MapWriter.ToVector(r.Points[k]); var o = MapWriter.ToVector(r.Points[k2]);
        var d = new Vector2(o.X - e.X, o.Z - e.Z);
        if (d.LengthSquared() < 1e-6f) return false;
        // d zeigt in die Brücke. Fläche großzügig: die Widerlager-Blöcke reichen im Spiel unter die
        // Brücke hinein (Kreuzungsarm 6 m innerhalb des Brückenendes lag noch unter dem Block)
        return FootBlocked(r, e, Vector2.Normalize(d), half, FootOut, FootIn, resolve, segs);
    }

    private static bool MoveJoint(RoadEntry r, int k, Func<RunEntry, ResolvedStyle> resolve,
                                  List<(RoadEntry R, RunEntry Run, Vector3 A, Vector3 B)> segs)
    {
        var a = r.Points[k - 1]; var p = r.Points[k]; var b = r.Points[k + 1];
        float la = Dist2(a, p), lb = Dist2(p, b);
        for (float dlt = 2; dlt <= 20; dlt += 2)
            foreach (float sg in new[] { 1f, -1f })
            {
                float off = sg * dlt;
                double[] q;
                if (off > 0) { if (off > lb - MinPiece) continue; q = Lerp(p, b, off / lb); }
                else { if (-off > la - MinPiece) continue; q = Lerp(p, a, -off / la); }
                var qv = MapWriter.ToVector(q);
                var nb = MapWriter.ToVector(off > 0 ? b : p);
                var na = MapWriter.ToVector(off > 0 ? p : a);
                var dd = new Vector2(nb.X - na.X, nb.Z - na.Z);
                if (dd.LengthSquared() < 1e-6f) continue;
                if (FootBlocked(r, qv, Vector2.Normalize(dd), JointHalf, -Wall, Wall, resolve, segs)) continue;
                // Höhe über der Straße (alter Stoß) halten: der verschobene Punkt wird so hoch gesetzt,
                // dass die Fahrbahn an der alten Stelle nicht tiefer liegt (sonst sank die Brücke unter
                // die Mindest-Durchfahrtshöhe, Lkw blieb hängen)
                double need = off > 0 ? a[1] + (p[1] - a[1]) * (la + off) / la
                                      : b[1] + (p[1] - b[1]) * (lb - off) / lb;
                if (need > q[1]) q[1] = need;
                r.Points[k] = q;
                return true;
            }
        return false;
    }

    public const string JunctionLineId = "junction@lane";
    /// <summary>Halbe Breite um eine Spurlinie eines Kreuzungs-Prefabs (Mitte und Ränder sind Linien).</summary>
    public const float JunctionLineHalf = 1.5f;
    public const float MaxPush = 10f;

    /// <summary>
    /// Schiebt Straßen (keine Brücken, keine Prefab-Linien), die das Widerlager am Brückenende berühren,
    /// quer zu ihrer Richtung unter die Brücke (weg vom Widerlager): Plateau über die Brückenbreite,
    /// weich bis zu den Straßenenden (z. B. ab der Ausfahrt), die Enden bleiben fest. Schritte von 0,5 m bis
    /// <see cref="MaxPush"/>. Gibt die größte Verschiebung zurück.
    /// </summary>
    private static float PushLower(RoadEntry r, RunEntry run, bool atStart, float half, Func<RunEntry, ResolvedStyle> resolve,
                                   List<(RoadEntry R, RunEntry Run, Vector3 A, Vector3 B)> segs)
    {
        int k = atStart ? run.From : run.To, k2 = atStart ? run.From + 1 : run.To - 1;
        var e = MapWriter.ToVector(r.Points[k]); var o = MapWriter.ToVector(r.Points[k2]);
        var u = new Vector2(o.X - e.X, o.Z - e.Z);
        if (u.LengthSquared() < 1e-6f) return 0;
        u = Vector2.Normalize(u);                          // in die Brücke
        var e2 = new Vector2(e.X, e.Z);
        var cands = segs.Where(t => !ReferenceEquals(t.R, r) && t.R.Id != JunctionLineId && !t.R.Id.StartsWith("prefab@")
                                    && !t.Run.Bridge && !t.Run.Tunnel && MathF.Max(t.A.Y, t.B.Y) <= e.Y - 2.5f
                                    && DistSeg(e2, new(t.A.X, t.A.Z), new(t.B.X, t.B.Z)) < half + 25f)
                        .Select(t => t.R).Distinct().ToList();
        float best = 0;
        bool dbg = Environment.GetEnvironmentVariable("OSM2ETS_BPROBE") is not null;
        if (dbg) Console.Error.WriteLine($"PUSH {r.Id} end ({e.X:0},{e.Z:0}) cands {string.Join(",", cands.Select(c => c.Id))}");
        foreach (var lr in cands)
        {
            // nächster Punkt der Straße zum Brückenende
            int n = lr.Points.Count;
            var s = new float[n];
            for (int i = 1; i < n; i++) s[i] = s[i - 1] + Dist2(lr.Points[i - 1], lr.Points[i]);
            int ci = 0; float cd = float.MaxValue;
            for (int i = 0; i < n; i++) { float d = Vector2.Distance(new((float)lr.Points[i][0], (float)lr.Points[i][2]), e2); if (d < cd) { cd = d; ci = i; } }
            float s0 = s[ci], plateau = half + 4f;
            if (s0 < plateau + 10f || s[^1] - s0 < plateau + 10f) { if (dbg) Console.Error.WriteLine($"  {lr.Id} near end s0 {s0:0} len {s[^1]:0}"); continue; }   // zu nah an den Straßenenden
            // Bogen über die ganze Straße bis zu ihren Enden verteilen (z. B. von der Ausfahrt bis zur Brücke),
            // nicht als S-Kurve kurz vor dem Bauwerk
            float tapA = s0 - plateau, tapB = s[^1] - s0 - plateau;
            float W(int i)
            {
                float ds = s[i] - s0, tap = ds < 0 ? tapA : tapB, a = MathF.Abs(ds);
                float w = a <= plateau ? 1 : MathF.Max(0, 1 - (a - plateau) / tap);
                return w * w * (3 - 2 * w);
            }
            // Brücken/Tunnel der tieferen Straße nicht mitschieben
            bool touchesStructure = false;
            for (int i = 0; i < n && !touchesStructure; i++)
                if (W(i) > 0 && lr.Runs.Any(x => (x.Bridge || x.Tunnel) && x.From <= i && i <= x.To)) touchesStructure = true;
            if (touchesStructure) { if (dbg) Console.Error.WriteLine($"  {lr.Id} structure"); continue; }
            var orig = lr.Points.Select(p => (double[])p.Clone()).ToList();
            float done = 0;
            for (float amt = 0.5f; amt <= MaxPush + 0.01f; amt += 0.5f)
            {
                for (int i = 0; i < n; i++)
                {
                    float w = W(i);
                    if (i == 0 || i == n - 1 || w <= 0) continue;
                    // quer zur Straße, Richtung in die Brücke
                    var a = lr.Points[Math.Max(i - 1, 0)]; var b = lr.Points[Math.Min(i + 1, n - 1)];
                    var dir = new Vector2((float)(b[0] - a[0]), (float)(b[2] - a[2]));
                    if (dir.LengthSquared() < 1e-6f) continue;
                    dir = Vector2.Normalize(dir);
                    var nrm = new Vector2(-dir.Y, dir.X);
                    if (Vector2.Dot(nrm, u) < 0) nrm = -nrm;
                    lr.Points[i] = [orig[i][0] + nrm.X * amt * w, orig[i][1], orig[i][2] + nrm.Y * amt * w];
                }
                Refresh(lr, segs);
                done = amt;
                if (!EndBlocked(r, run, atStart, half, resolve, segs)) break;
            }
            if (EndBlocked(r, run, atStart, half, resolve, segs) && done >= MaxPush)
            {
                if (dbg) Console.Error.WriteLine($"  {lr.Id} no effect");
                // half nichts: zurücksetzen
                for (int i = 0; i < n; i++) lr.Points[i] = orig[i];
                Refresh(lr, segs);
                continue;
            }
            best = MathF.Max(best, done);
            if (!EndBlocked(r, run, atStart, half, resolve, segs)) break;
        }
        return best;
    }

    private static void Refresh(RoadEntry lr, List<(RoadEntry R, RunEntry Run, Vector3 A, Vector3 B)> segs)
    {
        segs.RemoveAll(t => ReferenceEquals(t.R, lr));
        for (int i = 0; i < lr.Points.Count - 1; i++)
            if (lr.Runs.FirstOrDefault(x => x.From <= i && i < x.To) is { } run)
                segs.Add((lr, run, MapWriter.ToVector(lr.Points[i]), MapWriter.ToVector(lr.Points[i + 1])));
    }

    private static float DistSeg(Vector2 q, Vector2 a, Vector2 b)
    {
        var ab = b - a; float l2 = ab.LengthSquared();
        float t = l2 > 0 ? Math.Clamp(Vector2.Dot(q - a, ab) / l2, 0, 1) : 0;
        return Vector2.Distance(q, a + ab * t);
    }

    /// <summary>Halbe Breite der Widerlager-Fläche an Stoßstellen (breitester Körper, dlc_no_6).</summary>
    public const float JointHalf = 6.6f;

    private static bool FootBlocked(RoadEntry r, Vector3 e, Vector2 d, float half, float t0, float t1, Func<RunEntry, ResolvedStyle> resolve,
                                    List<(RoadEntry R, RunEntry Run, Vector3 A, Vector3 B)> segs)
    {
        var e2 = new Vector2(e.X, e.Z);
        var nrm = new Vector2(-d.Y, d.X);
        foreach (var (or, orun, a, b) in segs)
        {
            if (ReferenceEquals(or, r) || orun.Tunnel || orun.Bridge) continue;
            if (MathF.Max(a.Y, b.Y) > e.Y - 2.5f) continue;
            var a2 = new Vector2(a.X, a.Z); var ab = new Vector2(b.X, b.Z) - a2;
            float l2 = ab.LengthSquared();
            if (l2 < 1e-6f) continue;
            // Modellrand je Seite (rechts = in Fahrtrichtung rechts, wie LaneCentering)
            float limL, limR;
            if (or.Id == JunctionLineId) limL = limR = JunctionLineHalf + FootClear;
            else
            {
                var st = resolve(orun);
                limL = st.Width / 2 - st.ModelCenter + FootClear;
                limR = st.Width / 2 + st.ModelCenter + FootClear;
            }
            var abn = Vector2.Normalize(ab); var rightN = new Vector2(-abn.Y, abn.X);
            for (float sx = -half; sx <= half + 0.01f; sx += 1f)
                for (float t = t0; t <= t1 + 0.01f; t += 0.8f)
                {
                    var q = e2 + nrm * sx + d * t;
                    float u = Math.Clamp(Vector2.Dot(q - a2, ab) / l2, 0, 1);
                    var c = a2 + ab * u;
                    float lim = Vector2.Dot(q - c, rightN) >= 0 ? limR : limL;
                    if (Vector2.Distance(q, c) < lim)
                    {
                        if (Environment.GetEnvironmentVariable("OSM2ETS_BPROBE2") is not null)
                            Console.Error.WriteLine($"    blocked by {or.Id} ({a.X:0.0},{a.Y:0.0},{a.Z:0.0})-({b.X:0.0},{b.Z:0.0}) at sx {sx:0.0} t {t:0.0} lim {lim:0.0}");
                        return true;
                    }
                }
        }
        return false;
    }

    /// <summary>Brückenlauf zu einem Abschnitt zusammenlegen; gibt dessen Index zurück.</summary>
    private static int Merge(RoadEntry r, RunEntry run)
    {
        int from = run.From, drop = run.To - run.From - 1;
        if (drop <= 0) return from;
        r.Points.RemoveRange(from + 1, drop);
        foreach (var x in r.Runs)
        {
            if (ReferenceEquals(x, run)) { x.To = from + 1; continue; }
            if (x.From > from) { x.From -= drop; x.To -= drop; }
        }
        return from;
    }

    private static bool Cross(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1, out float t, out float u)
    {
        t = u = 0;
        var r = a1 - a0; var s = b1 - b0;
        float den = r.X * s.Y - r.Y * s.X;
        if (MathF.Abs(den) < 1e-8f) return false;
        var w = b0 - a0;
        t = (w.X * s.Y - w.Y * s.X) / den;
        u = (w.X * r.Y - w.Y * r.X) / den;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1;
    }
}
