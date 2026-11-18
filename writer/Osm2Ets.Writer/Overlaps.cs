// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Osm2Ets.Writer;

/// <summary>
/// Regel: Fahrbahnflächen dürfen sich in der Draufsicht nur überlagern, wenn
/// mindestens eine davon Brücke oder Tunnel ist (und dann mit Höhenabstand).
/// Fläche = Mittellinie ± halbe Breite aus mapping.jsonc; Prefabs als Fläche um
/// ihre Knoten.
///
/// TrimBranches: Straßenenden an Knoten ohne Prefab und ohne durchgehende
/// Verbindung (Abzweige) werden so weit gekürzt, dass sie die andere Fahrbahn
/// nicht mehr überdecken. Sie enden dann am Fahrbahnrand – ohne Prefab bleiben sie
/// unverbunden, aber sie liegen nicht mehr übereinander.
///
/// Report: listet alle verbleibenden Verstöße (für den Bericht und als GeoJSON).
/// </summary>
public static class Overlaps
{
    public const float Tolerance = 0.3f;      // m, Berührung an Fahrbahnrändern ist erlaubt
    public const float MinClearance = 4.5f;   // m, Höhenabstand bei Brücke/Tunnel
    public const float MaxTrim = 60f;          // m, weiter wird ein Abzweig nicht gekürzt

    public sealed record Violation(string A, string B, Vector3 At, float Overlap, float HeightDiff, string Kind);

    private readonly record struct Seg(RoadEntry? Road, int Index, Vector3 A, Vector3 B, float HalfWidth, bool Structure);

    // ------------------------------------------------------------------ Abzweige kürzen

    /// <summary>Modellmitte rechts der Straßenlinie je Lauf (für asymmetrische Modelle).</summary>
    public static Func<RunEntry, float> CenterOf = _ => 0f;

    private static Vector2 Right(Vector2 a, Vector2 b)
    {
        var d = b - a;
        return d.LengthSquared() > 1e-8f ? Vector2.Normalize(new Vector2(-d.Y, d.X)) : Vector2.Zero;
    }

    public static int TrimBranches(List<RoadEntry> roads, List<(RoadEntry From, RoadEntry To)> pairs,
                                   Func<RunEntry, float> halfWidth, List<string> warnings)
    {
        // An einem Knoten darf nur ein durchgehendes Paar bleiben (ohne Prefab kreuzen
        // sich sonst zwei Fahrbahnen ebenerdig). Das wichtigere Paar bleibt.
        float Rank(RoadEntry r) => (r.Role == "core" ? 100 : r.Role == "link" ? 50 : 0)
                                   + halfWidth(r.Runs[r.Runs.Count / 2]);
        long? NodeOf(RoadEntry a) => a.End?.Kind == "node" ? a.End.OsmNode : null;
        foreach (var grp in pairs.Where(p => NodeOf(p.From) is not null).GroupBy(p => NodeOf(p.From)!.Value).ToList())
        {
            if (grp.Count() < 2) continue;
            var keep = grp.MaxBy(p => Rank(p.From) + Rank(p.To));
            foreach (var p in grp.Where(p => p != keep).ToList())
            {
                pairs.Remove(p);
                warnings.Add($"Knoten {grp.Key}: zweite durchgehende Verbindung {p.From.Id} → {p.To.Id} gelöst (ebenerdige Kreuzung ohne Prefab)");
            }
        }
        var connected = new HashSet<(RoadEntry, bool)>();
        foreach (var (a, b) in pairs) { connected.Add((a, false)); connected.Add((b, true)); }

        var byNode = new Dictionary<long, List<(RoadEntry R, bool AtStart)>>();
        foreach (var r in roads)
            foreach (var atStart in new[] { true, false })
            {
                var e = atStart ? r.Start : r.End;
                if (e?.Kind == "node" && e.OsmNode is long id)
                {
                    if (!byNode.TryGetValue(id, out var l)) byNode[id] = l = [];
                    l.Add((r, atStart));
                }
            }

        int trimmed = 0;
        foreach (var (node, ends) in byNode)
        {
            if (ends.Count < 2) continue;
            // Rang: Kern vor Anschluss, dann breiter vor schmaler; Abzweige = nicht durchgehend verbundene Enden
            foreach (var br in ends.Where(e => !connected.Contains((e.R, e.AtStart))))
            {
                var others = ends.Where(e => e.R != br.R).Select(e => e.R).Distinct().ToList();
                if (others.Count == 0) continue;
                var run = br.AtStart ? br.R.Runs[0] : br.R.Runs[^1];
                if (run.Bridge || run.Tunnel) continue;
                float hw = halfWidth(run);
                // so lange Punkte am Ende entfernen bzw. das Endstück verkürzen, bis der
                // Endpunkt (mit halber Breite) die anderen Fahrbahnen nicht mehr berührt
                float need = 0;
                foreach (var o in others)
                    need = MathF.Max(need, NeededTrim(br.R, br.AtStart, o, hw, halfWidth));
                if (need <= Tolerance) continue;
                if (need > MaxTrim || ArcLength(br.R) < need + 10)
                {
                    warnings.Add($"Knoten {node}: Abzweig {br.R.Id} müsste um {need:0} m gekürzt werden – zu kurz, bleibt überlagert");
                    continue;
                }
                CutEnd(br.R, br.AtStart, need);
                // Ende ist jetzt frei: nicht mehr als Knotenende behandeln
                var cut = new EndInfo { Kind = "trimmed", OsmNode = node };
                if (br.AtStart) br.R.Start = cut; else br.R.End = cut;
                trimmed++;
            }
        }
        return trimmed;
    }

    /// <summary>Gesperrte Zufahrten an Autobahn/Rampe kürzen bzw. mit Übergangsspur versehen. Läuft erst
    /// nach dem Auseinanderrücken der Richtungsfahrbahnen (CarriagewaySeparation), damit die Lage der
    /// Autobahn stimmt (vorher verschob sich die Autobahn danach noch um bis zu ~2 m zur Zufahrt hin).</summary>
    public static int TrimStubs(List<RoadEntry> roads, List<(RoadEntry From, RoadEntry To)> pairs,
                                Func<RunEntry, float> halfWidth, List<string> warnings)
    {
        int trimmed = 0;
        var byNode = new Dictionary<long, List<(RoadEntry R, bool AtStart)>>();
        foreach (var r0 in roads)
            foreach (var st in new[] { true, false })
            {
                var e0 = st ? r0.Start : r0.End;
                if (e0?.Kind is "node" or "trimmed" && e0.OsmNode is long id)
                {
                    if (!byNode.TryGetValue(id, out var l)) byNode[id] = l = [];
                    l.Add((r0, st));
                }
            }
        // Gesperrte Zufahrten (access=no|private), die frei an einer Autobahn/Rampe enden (keine
        // Kreuzung, Fahrbahn bleibt ungeteilt): bis an den Fahrbahnrand zurücknehmen
        var major = roads.Where(r => r.Runs.Any(x => x.Highway is "motorway" or "motorway_link" or "trunk" or "trunk_link")).ToList();
        var drop = new HashSet<RoadEntry>();
        foreach (var r in roads)
            foreach (var atStart in new[] { true, false })
            {
                var end = atStart ? r.Start : r.End;
                var run = atStart ? r.Runs[0] : r.Runs[^1];
                if (run.Access is null || major.Contains(r)) continue;
                // an einem Knoten, an dem auch eine andere Straße endet: echte Einmündung, bleibt
                if (end?.Kind == "node" && end.OsmNode is long en && byNode.TryGetValue(en, out var at) && at.Count > 1) continue;
                var p = P(atStart ? r.Points[0] : r.Points[^1]);
                float hw = halfWidth(run);
                // nur Fahrbahnen auf gleicher Höhe (nicht die Brücke darüber / den Tunnel darunter)
                float py = (float)(atStart ? r.Points[0] : r.Points[^1])[1];
                var near = roads.Where(o => o != r && !drop.Contains(o) && Touches(p, hw + 2f, o, halfWidth) && SameLevelNear(p, py, o)).ToList();
                if (near.Count == 0) continue;
                float need = 0;
                // Winkel der Zufahrt zur Autobahn: steil (≥ 45°) → rechtwinklig 0,3 m in den Modellrand;
                // flach → dort abschneiden, wo sie weit genug von der Autobahn weg ist, und mit einem
                // geraden Stück unter ShallowTurnDeg bis an den Rand führen (wie eine Einmündung auf den
                // Standstreifen, ohne Spur entlang der Autobahn)
                var dirEnd = EndDirFar(r, atStart, 15f);
                var tgM = NearestDir(near[0], p);
                bool shallow = dirEnd != Vector2.Zero && tgM != Vector2.Zero && MathF.Abs(Vector2.Dot(dirEnd, tgM)) > 0.7f;
                if (shallow)
                {
                    var target = near.Where(major.Contains).DefaultIfEmpty(near[0]).MinBy(x => DistToRoad(x, p))!;
                    if (ShallowEnd(r, atStart, target, halfWidth, out float cut))
                    {
                        LiftEnd(r, atStart, near, perpendicular: false, lift: TransitionLift);
                        warnings.Add($"Info: gesperrte Zufahrt {r.Id} schräg an den Autobahnrand geführt ({cut:0} m gekürzt) bei ({p.X:0},{p.Y:0})");
                        trimmed++;
                        continue;
                    }
                }
                foreach (var o in near)
                    need = MathF.Max(need, NeededTrim(r, atStart, o, -SteepOverlap, halfWidth));
                if (need > MaxTrim || ArcLength(r) < need + 3)
                {
                    // zu kurz: ganz weglassen statt auf der Fahrbahn liegen lassen – nur wenn das
                    // andere Ende frei ist (sonst hinge eine Kreuzung daran)
                    { drop.Add(r); warnings.Add($"Gesperrte Zufahrt {r.Id} weggelassen bei ({p.X:0},{p.Y:0}) – zu kurz zum Kürzen"); }
                    continue;
                }
                CutEnd(r, atStart, need);
                LiftEnd(r, atStart, near, perpendicular: !shallow, lift: shallow ? TransitionLift : 0.02f);
                warnings.Add($"Info: gesperrte Zufahrt {r.Id} am Fahrbahnrand um {need:0} m gekürzt bei ({p.X:0},{p.Y:0})");
                trimmed++;
            }
        if (drop.Count > 0)
        {
            roads.RemoveAll(drop.Contains);
            pairs.RemoveAll(p => drop.Contains(p.From) || drop.Contains(p.To));
            warnings.Add($"Info: {drop.Count} gesperrte Zufahrten an der Autobahn zu kurz zum Kürzen – weggelassen");
        }
        return trimmed;
    }

    /// <summary>So weit (m) ragt eine steil auftreffende gesperrte Zufahrt in den Modellrand der Autobahn.</summary>
    public const float SteepOverlap = 0.3f;
    /// <summary>Flache Zufahrt: Winkel (Grad) des geraden Endstücks zur Autobahn.</summary>
    public const float ShallowTurnDeg = 50f;
    /// <summary>Flache Zufahrt: Länge (m) des geraden Endstücks.</summary>
    public const float ShallowTurnLen = 10f;
    /// <summary>Flache Zufahrt: Mitte ihres Endes so weit (m) innerhalb des Modellrands der Autobahn.</summary>
    public const float ShallowOverlap = 0.3f;
    /// <summary>Höchstens so viel (m) der Zufahrt wird für das Endstück weggenommen.</summary>
    public const float ShallowMaxCut = 90f;
    /// <summary>Endstück flacher Zufahrten so viel (m) über der Autobahn (sonst scheint Gelände durch).</summary>
    public const float TransitionLift = 0.005f;
    /// <summary>Höchste Steigung (Anteil) beim Angleichen eines Zufahrtsendes an die Autobahnhöhe.</summary>
    public const double LiftGrade = 0.05;
    public static int Transitions;

    private static float DistToRoad(RoadEntry o, Vector2 p)
    {
        float best = float.MaxValue;
        for (int i = 0; i < o.Points.Count - 1; i++) best = MathF.Min(best, DistPointSeg(p, P(o.Points[i]), P(o.Points[i + 1])));
        return best;
    }

    /// <summary>Lage eines Punkts zur Achse der Straße o: Abstand, Abstand des Modellrands auf seiner
    /// Seite (halbe Breite ± Modellversatz), Fußpunkt, Richtung und Höhe der Achse dort.</summary>
    private static (float Dist, float Edge, Vector2 Foot, Vector2 Tg, float Y) AxisRel(RoadEntry o, Vector2 q, Func<RunEntry, float> halfWidth)
    {
        (float, float, Vector2, Vector2, float) res = (float.MaxValue, 0, q, Vector2.Zero, 0);
        for (int i = 0; i < o.Points.Count - 1; i++)
        {
            var a = P(o.Points[i]); var b = P(o.Points[i + 1]);
            var ab = b - a; float l2 = ab.LengthSquared(); if (l2 < 1e-6f) continue;
            float t = Math.Clamp(Vector2.Dot(q - a, ab) / l2, 0, 1);
            var f = a + ab * t; float d = Vector2.Distance(q, f);
            if (d >= res.Item1) continue;
            var run = o.Runs.FirstOrDefault(x => x.From <= i && i < x.To) ?? o.Runs[0];
            float side = Vector2.Dot(q - f, Right(a, b)) >= 0 ? 1 : -1;
            res = (d, halfWidth(run) + side * CenterOf(run), f, ab / MathF.Sqrt(l2),
                   (float)(o.Points[i][1] + (o.Points[i + 1][1] - o.Points[i][1]) * t));
        }
        return res;
    }

    /// <summary>Flach auftreffende gesperrte Zufahrt (Annäherung unter 45°): an der Stelle abschneiden,
    /// an der sie ShallowTurnLen·sin(ShallowTurnDeg) außerhalb des Modellrands der Autobahn liegt, und von
    /// dort mit einem geraden Stück unter ShallowTurnDeg in Fahrtrichtung bis an den Rand führen (Mitte
    /// ShallowOverlap m innerhalb). Ergebnis: Einmündung schräg auf den Standstreifen, keine Spur entlang
    /// der Autobahn. false, wenn die Zufahrt dafür nicht weit genug von der Autobahn wegführt.</summary>
    private static bool ShallowEnd(RoadEntry r, bool atStart, RoadEntry o, Func<RunEntry, float> halfWidth, out float cut)
    {
        cut = 0;
        float sinA = MathF.Sin(ShallowTurnDeg * MathF.PI / 180f), cosA = MathF.Cos(ShallowTurnDeg * MathF.PI / 180f);
        // stumpfe Ecke des quer abgeschnittenen Endes soll den Rand erreichen: keine Erdlücke
        float ov = halfWidth(atStart ? r.Runs[0] : r.Runs[^1]) * cosA + ShallowOverlap;
        float needOut = ShallowTurnLen * sinA - ov;   // Abstand außerhalb des Rands am Knickpunkt
        int n = r.Points.Count;
        Vector2 Pt(int k) => P(atStart ? r.Points[k] : r.Points[n - 1 - k]);
        float Out(Vector2 q) { var a = AxisRel(o, q, halfWidth); return a.Dist - a.Edge; }
        float acc = 0, prevOut = Out(Pt(0));
        float? arc = null;
        for (int k = 1; k < n; k++)
        {
            var q0 = Pt(k - 1); var q1 = Pt(k);
            float seg = Vector2.Distance(q0, q1);
            float o1 = Out(q1);
            if (o1 >= needOut)
            {
                float t = o1 - prevOut > 1e-3f ? Math.Clamp((needOut - prevOut) / (o1 - prevOut), 0, 1) : 1;
                arc = acc + seg * t;
                break;
            }
            acc += seg; prevOut = o1;
            if (acc > ShallowMaxCut) break;
        }
        if (arc is not float a1 || a1 > ShallowMaxCut || ArcLength(r) < a1 + 5) return false;
        CutEnd(r, atStart, a1);
        // Knickpunkt (neues Ende) und Fahrtrichtung dorthin
        var e = atStart ? r.Points[0] : r.Points[^1];
        var p1 = P(e);
        var prev = P(atStart ? r.Points[1] : r.Points[^2]);
        var rel = AxisRel(o, p1, halfWidth);
        var nOut = p1 - rel.Foot; if (nOut.LengthSquared() < 1e-4f || rel.Tg == Vector2.Zero) return false;
        nOut = Vector2.Normalize(nOut);
        float sg = Vector2.Dot(p1 - prev, rel.Tg) >= 0 ? 1 : -1;
        var u = rel.Tg * (sg * cosA) - nOut * sinA;
        var end = p1 + u * ShallowTurnLen;
        // auf den Rand nachführen (Autobahn kann gebogen sein)
        var re = AxisRel(o, end, halfWidth);
        var ne = end - re.Foot;
        if (ne.LengthSquared() > 1e-4f) end += Vector2.Normalize(ne) * ((re.Edge - ov) - re.Dist);
        var add = new double[] { end.X, e[1], end.Y };   // Höhe setzt LiftEnd
        if (atStart)
        {
            r.Points.Insert(0, add);
            foreach (var run in r.Runs) { if (run != r.Runs[0]) run.From += 1; run.To += 1; }
        }
        else
        {
            r.Points.Add(add);
            r.Runs[^1].To += 1;
        }
        cut = a1;
        Transitions++;
        return true;
    }

    /// <summary>Freies Ende einer gesperrten Zufahrt auf die Höhe der Fahrbahn heben, in die es ragt
    /// (sonst Stufe am Autobahnrand); über 15 m davor angleichen.</summary>
    private static void LiftEnd(RoadEntry r, bool atStart, List<RoadEntry> near, bool perpendicular = true, float lift = 0.02f)
    {
        var e = atStart ? r.Points[0] : r.Points[^1];
        var p = P(e);
        float best = float.MaxValue, oy = float.NaN; Vector2 foot = p;
        foreach (var o in near)
            for (int i = 0; i < o.Points.Count - 1; i++)
            {
                var a = P(o.Points[i]); var b = P(o.Points[i + 1]);
                float d = DistPointSeg(p, a, b);
                if (d < best)
                {
                    best = d; oy = (float)((o.Points[i][1] + o.Points[i + 1][1]) / 2);
                    var ab = b - a; float l2 = ab.LengthSquared();
                    foot = l2 > 0 ? a + ab * Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0, 1) : a;
                }
            }
        if (float.IsNaN(oy)) return;
        // steil auftreffende Zufahrt (≥ 37°): letzte 10 m gerade und rechtwinklig auf die Autobahn;
        // flach auftreffende (Einfädelstreifen-artig) behält ihre OSM-Linie
        bool steep = false;
        {
            int nb0 = atStart ? 1 : r.Points.Count - 2;
            var dl = p - P(r.Points[nb0]); var tgv = foot - p;
            if (dl.LengthSquared() > 1e-4f && best > 1e-3f)
            {
                // Winkel zwischen Zufahrt und Autobahn: Anteil der Annäherung senkrecht zur Autobahn
                float c = MathF.Abs(Vector2.Dot(Vector2.Normalize(dl), Vector2.Normalize(tgv)));
                steep = perpendicular && c >= 0.6f;
            }
        }
        if (steep)
        {
            // letztes Stück gerade: Zwischenpunkte innerhalb von 10 m vor dem Ende entfernen (sonst ein
            // spitzer Knick direkt an der Sperre, das Straßenstück wird zum verdrehten Keil)
            const float straight = 10f;
            while (r.Points.Count > 2)
            {
                int k = atStart ? 1 : r.Points.Count - 2;
                if (Vector2.Distance(P(r.Points[k]), p) >= straight) break;
                r.Points.RemoveAt(k);
                foreach (var run in r.Runs)
                {
                    if (run.From > k) run.From--;
                    if (run.To >= k && run.To > 0) run.To--;
                }
            }
            // rechtwinklig auf die Autobahn zulaufen: Endkante parallel zum Fahrbahnrand, kein Keil
            // (eine schräge Endkante ragte auf einer Seite aus dem Randstreifen heraus)
            var outN = p - foot;
            if (outN.LengthSquared() > 1e-4f && r.Points.Count >= 2)
            {
                outN = Vector2.Normalize(outN);
                int nb = atStart ? 1 : r.Points.Count - 2;
                var q = p + outN * straight;
                // nur wenn der Nachbarpunkt dadurch nicht hinter dem neuen Punkt liegt (Haken)
                if (Vector2.Dot(P(r.Points[nb]) - q, outN) > 0)
                {
                    int at = atStart ? 1 : r.Points.Count - 1;
                    r.Points.Insert(at, new double[] { q.X, e[1], q.Y });   // Höhe hebt die Angleichung unten
                    foreach (var run in r.Runs)
                    {
                        if (run.From >= at && !(atStart && run.From == 0)) run.From++;
                        if (run.To >= at) run.To++;
                    }
                    // Punkte direkt hinter dem neuen Punkt (< 6 m) entfernen: sonst ein kurzes Reststück
                    // mit hartem Knick statt einer sanften Kurve
                    while (r.Points.Count > 3)
                    {
                        int k = atStart ? 2 : r.Points.Count - 3;
                        if (Vector2.Distance(P(r.Points[k]), q) >= 6f) break;
                        r.Points.RemoveAt(k);
                        foreach (var run in r.Runs)
                        {
                            if (run.From > k) run.From--;
                            if (run.To >= k && run.To > 0) run.To--;
                        }
                    }
                }
            }
        }
        double dy = oy + lift - e[1];
        if (Math.Abs(dy) < 0.02) return;
        // volle Höhe schon 10 m vor dem Ende, angeglichen über die 20 m davor – bei großem Höhenunterschied
        // länger, höchstens LiftGrade Steigung (sonst eine steile Rampe zum Autobahndamm hinauf)
        const double full = 10;
        double blend = Math.Max(20, Math.Abs(dy) / LiftGrade);
        int n = r.Points.Count; double acc = 0;
        for (int k = 0; k < n; k++)
        {
            int i = atStart ? k : n - 1 - k;
            if (k > 0) { int j = atStart ? i - 1 : i + 1; acc += Vector2.Distance(P(r.Points[i]), P(r.Points[j])); }
            if (acc >= full + blend) break;
            r.Points[i][1] += dy * (acc <= full ? 1 : 1 - (acc - full) / blend);
        }
    }

    private static Vector2 EndDir(RoadEntry r, bool atStart)
    {
        var a = P(atStart ? r.Points[0] : r.Points[^1]);
        var b = P(atStart ? r.Points[Math.Min(1, r.Points.Count - 1)] : r.Points[Math.Max(0, r.Points.Count - 2)]);
        var d = a - b; return d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) : Vector2.Zero;
    }

    /// <summary>Richtung zum Ende, gemittelt über die letzten <paramref name="dist"/> m.</summary>
    private static Vector2 EndDirFar(RoadEntry r, bool atStart, float dist)
    {
        var a = P(atStart ? r.Points[0] : r.Points[^1]);
        int n = r.Points.Count; float acc = 0; Vector2 b = a;
        for (int k = 1; k < n; k++)
        {
            var q = P(atStart ? r.Points[k] : r.Points[n - 1 - k]);
            acc += Vector2.Distance(q, b); b = q;
            if (acc >= dist) break;
        }
        var d = a - b; return d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) : Vector2.Zero;
    }

    private static Vector2 NearestDir(RoadEntry o, Vector2 p)
    {
        float best = float.MaxValue; Vector2 t = Vector2.Zero;
        for (int i = 0; i < o.Points.Count - 1; i++)
        {
            var a = P(o.Points[i]); var b = P(o.Points[i + 1]);
            float d = DistPointSeg(p, a, b);
            if (d < best && (b - a).LengthSquared() > 1e-6f) { best = d; t = Vector2.Normalize(b - a); }
        }
        return t;
    }

    private static bool SameLevelNear(Vector2 p, float py, RoadEntry o)
    {
        int best = -1; float bd = float.MaxValue;
        for (int i = 0; i < o.Points.Count - 1; i++)
        {
            float d = DistPointSeg(p, P(o.Points[i]), P(o.Points[i + 1]));
            if (d < bd) { bd = d; best = i; }
        }
        if (best < 0) return false;
        var run = o.Runs.FirstOrDefault(x => x.From <= best && best < x.To) ?? o.Runs[0];
        // Brücke/Tunnel nur über die Höhe ausschließen (eine Brücke auf gleicher Höhe zählt)
        float oy = (float)((o.Points[best][1] + o.Points[best + 1][1]) / 2);
        return MathF.Abs(oy - py) < 3f;
    }

    /// <summary>Wie weit (Bogenlänge) das Ende von r gekürzt werden muss, damit es o nicht überdeckt.</summary>
    private static float NeededTrim(RoadEntry r, bool atStart, RoadEntry o, float hw, Func<RunEntry, float> halfWidth, bool symmetric = false)
    {
        var pts = atStart ? r.Points : Enumerable.Reverse(r.Points).ToList();
        float acc = 0;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var a = P(pts[i]); var b = P(pts[i + 1]);
            float len = Vector2.Distance(a, b);
            // in 1-m-Schritten prüfen
            for (float t = 0; t < len; t += 1f)
            {
                var p = Vector2.Lerp(a, b, len > 0 ? t / len : 0);
                // Richtung am Ende: von r weg zum Knoten hin egal – rechts bezogen auf Fahrtrichtung von r
                var dirA = atStart ? a : b; var dirB = atStart ? b : a;
                var pc = symmetric ? p : p + Right(dirA, dirB) * CenterOf(atStart ? r.Runs[0] : r.Runs[^1]);
                if (!Touches(pc, hw, o, halfWidth, symmetric)) return acc + t;
            }
            acc += len;
            if (acc > MaxTrim + 1) return acc;
        }
        return acc;
    }

    private static bool Touches(Vector2 p, float hw, RoadEntry o, Func<RunEntry, float> halfWidth, bool symmetric = false)
    {
        for (int i = 0; i < o.Points.Count - 1; i++)
        {
            var run = o.Runs.FirstOrDefault(x => x.From <= i && i < x.To) ?? o.Runs[0];
            var oa = P(o.Points[i]); var ob = P(o.Points[i + 1]);
            // symmetric: Modellrand beidseitig mit dem größeren Abstand (halbe Breite + |Versatz|) –
            // für gesperrte Zufahrten, die nicht auf den Randstreifen ragen dürfen
            var sh = symmetric ? Vector2.Zero : Right(oa, ob) * CenterOf(run);
            float d = DistPointSeg(p, oa + sh, ob + sh);
            float hwo = halfWidth(run) + (symmetric ? 2 * MathF.Abs(CenterOf(run)) : 0);   // + Verschiebung durch LaneCentering danach
            if (d < hw + hwo - Tolerance) return true;
        }
        return false;
    }

    internal static void CutEnd(RoadEntry r, bool atStart, float dist)
    {
        if (!atStart) Connector.Reverse(r);
        // jetzt am Anfang kürzen: neuen Startpunkt auf dem Segment einsetzen
        double acc = 0;
        int k = 0;
        while (k < r.Points.Count - 1)
        {
            double s = Dist(r.Points[k], r.Points[k + 1]);
            if (acc + s > dist) break;
            acc += s; k++;
        }
        var a = r.Points[k]; var b = r.Points[Math.Min(k + 1, r.Points.Count - 1)];
        double seg = Dist(a, b);
        double t = seg > 0 ? (dist - acc) / seg : 0;
        var np = new[] { a[0] + t * (b[0] - a[0]), a[1] + t * (b[1] - a[1]), a[2] + t * (b[2] - a[2]) };
        var rest = r.Points.Skip(k + 1).ToList();
        // zu kurzes erstes Stück vermeiden (Spielminimum 1,25 m)
        if (rest.Count > 1 && Dist(np, rest[0]) < 2.0) rest.RemoveAt(0);
        int removed = r.Points.Count - 1 - rest.Count;   // weggefallene Abschnitte vor dem neuen ersten
        r.Points = new List<double[]> { np }.Concat(rest).ToList();
        int segs = r.Points.Count - 1;
        r.Runs = r.Runs.Select(x => { x.From = Math.Max(0, x.From - removed); x.To = Math.Min(segs, x.To - removed); return x; })
                       .Where(x => x.To > x.From).ToList();
        r.Runs[0].From = 0;
        r.Runs[^1].To = segs;
        if (!atStart) Connector.Reverse(r);
    }

    // ------------------------------------------------------------------ Prüfung

    public static List<Violation> Report(List<RoadEntry> roads, Dictionary<RoadEntry, ResolvedStyle[]> styles,
                                         List<(RoadEntry From, RoadEntry To)> pairs,
                                         List<(Vector3[] Nodes, float HalfWidth)> prefabs)
    {
        var segs = new List<Seg>();
        foreach (var r in roads)
            for (int i = 0; i < r.Points.Count - 1; i++)
            {
                var run = r.Runs.FirstOrDefault(x => x.From <= i && i < x.To) ?? r.Runs[0];
                var pa = MapWriter.ToVector(r.Points[i]); var pb = MapWriter.ToVector(r.Points[i + 1]);
                var dd = pb - pa; dd.Y = 0;
                if (dd.LengthSquared() > 1e-6f && styles[r][i].ModelCenter != 0)
                {
                    dd = Vector3.Normalize(dd);
                    var sh = new Vector3(-dd.Z, 0, dd.X) * styles[r][i].ModelCenter;   // rechts in Fahrtrichtung
                    pa += sh; pb += sh;
                }
                segs.Add(new Seg(r, i, pa, pb, styles[r][i].Width / 2, run.Bridge || run.Tunnel));
            }
        foreach (var (nodes, hw) in prefabs)
        {
            var c = nodes.Aggregate(Vector3.Zero, (a, b) => a + b) / nodes.Length;
            foreach (var n in nodes) segs.Add(new Seg(null, -1, n, c, hw, false));
        }

        // Nachbarn: verbundene Paare, Stücke derselben Straße nebeneinander
        var pairSet = new HashSet<(RoadEntry, RoadEntry)>();
        foreach (var (a, b) in pairs) { pairSet.Add((a, b)); pairSet.Add((b, a)); }

        const float cell = 50f;
        var grid = new Dictionary<(int, int), List<int>>();
        for (int k = 0; k < segs.Count; k++)
        {
            var s = segs[k];
            float pad = s.HalfWidth + 10;
            int x0 = (int)MathF.Floor((MathF.Min(s.A.X, s.B.X) - pad) / cell), x1 = (int)MathF.Floor((MathF.Max(s.A.X, s.B.X) + pad) / cell);
            int z0 = (int)MathF.Floor((MathF.Min(s.A.Z, s.B.Z) - pad) / cell), z1 = (int)MathF.Floor((MathF.Max(s.A.Z, s.B.Z) + pad) / cell);
            for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++)
            {
                if (!grid.TryGetValue((x, z), out var l)) grid[(x, z)] = l = [];
                l.Add(k);
            }
        }

        var found = new Dictionary<(string, string), Violation>();
        var seen = new HashSet<(int, int)>();
        foreach (var l in grid.Values)
            for (int i = 0; i < l.Count; i++)
            for (int j = i + 1; j < l.Count; j++)
            {
                int a = Math.Min(l[i], l[j]), b = Math.Max(l[i], l[j]);
                if (!seen.Add((a, b))) continue;
                var s = segs[a]; var o = segs[b];
                if (s.Road is null && o.Road is null) continue;
                if (s.Road is not null && s.Road == o.Road && Math.Abs(s.Index - o.Index) <= 1) continue;
                if (s.Road is not null && o.Road is not null && pairSet.Contains((s.Road, o.Road))
                    && SharesEnd(s, o)) continue;
                // Straße und Prefab, an das sie angeschlossen ist
                if ((s.Road is null || o.Road is null) && SharesEndPoint(s, o)) continue;
                var (d, pa, pb) = SegSeg(P2(s.A), P2(s.B), P2(o.A), P2(o.B));
                float overlap = s.HalfWidth + o.HalfWidth - d;
                if (overlap <= Tolerance) continue;
                float hs = HeightAt(s, pa), ho = HeightAt(o, pb);
                float dz = MathF.Abs(hs - ho);
                bool structure = s.Structure || o.Structure;
                if (structure && dz >= MinClearance) continue;
                string kind = structure ? "Brücke/Tunnel mit zu wenig Höhenabstand"
                            : dz >= MinClearance ? "höhenfrei, aber nicht als Brücke/Tunnel markiert"
                            : "Fahrbahnen überlagern sich";
                string na = s.Road?.Id ?? "Prefab", nb = o.Road?.Id ?? "Prefab";
                var key = string.CompareOrdinal(na, nb) < 0 ? (na, nb) : (nb, na);
                var v = new Violation(key.Item1, key.Item2, new Vector3(pa.X, hs, pa.Y), overlap, dz, kind);
                if (!found.TryGetValue(key, out var old) || old.Overlap < overlap) found[key] = v;
            }
        return found.Values.OrderByDescending(v => v.Overlap).ToList();
    }

    /// <summary>Straßen mit genauen Modellrändern (zugewandte Seite, bis kurz vor den fremden Rand), siehe MapWriter.</summary>
    public static bool ExactRoads;

    /// <summary>
    /// Geländestreifen dürfen keine fremde Fahrbahn auf anderer Höhe überdecken (Regel wie
    /// bei Fahrbahnen; auch Brücken/Tunnel bekommen darüber/darunter kein Gelände).
    /// Kürzt das Gelände der betroffenen Seite bis zum Rand der fremden Fahrbahn.
    /// Gibt die Zahl der gekürzten Abschnitte zurück.
    /// </summary>
    public static int EnforceTerrain(List<RoadEntry> roads, Dictionary<RoadEntry, ResolvedStyle[]> styles,
                                     List<(Vector3[] Nodes, float HalfWidth)> prefabs, float minDz = 0.3f)
    {
        var segs = new List<Seg>();
        foreach (var r in roads)
            for (int i = 0; i < r.Points.Count - 1; i++)
                segs.Add(new Seg(r, i, MapWriter.ToVector(r.Points[i]), MapWriter.ToVector(r.Points[i + 1]),
                                 styles[r][i].Width / 2, false));
        foreach (var (nodes, hw) in prefabs)
        {
            var c = nodes.Aggregate(Vector3.Zero, (a, b) => a + b) / nodes.Length;
            foreach (var n in nodes) segs.Add(new Seg(null, -1, n, c, hw, false));
        }
        const float cell = 50f;
        var grid = new Dictionary<(int, int), List<int>>();
        for (int k = 0; k < segs.Count; k++)
        {
            var sg = segs[k];
            float pad = sg.HalfWidth + (sg.Road is null ? 0 : MathF.Max(styles[sg.Road][sg.Index].TerrainLeft, styles[sg.Road][sg.Index].TerrainRight));
            int x0 = (int)MathF.Floor((MathF.Min(sg.A.X, sg.B.X) - pad) / cell), x1 = (int)MathF.Floor((MathF.Max(sg.A.X, sg.B.X) + pad) / cell);
            int z0 = (int)MathF.Floor((MathF.Min(sg.A.Z, sg.B.Z) - pad) / cell), z1 = (int)MathF.Floor((MathF.Max(sg.A.Z, sg.B.Z) + pad) / cell);
            for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++)
            {
                if (!grid.TryGetValue((x, z), out var l)) grid[(x, z)] = l = [];
                l.Add(k);
            }
        }
        int changed = 0;
        for (int a = 0; a < segs.Count; a++)
        {
            var sg = segs[a];
            if (sg.Road is null) continue;
            var st = styles[sg.Road][sg.Index];
            float left = st.TerrainLeft, right = st.TerrainRight;
            if (left <= 0 && right <= 0) continue;
            var dir = P2(sg.B) - P2(sg.A);
            if (dir.LengthSquared() < 1e-6f) continue;
            dir = Vector2.Normalize(dir);
            var cand = new HashSet<int>();
            foreach (var t in new[] { 0f, 0.5f, 1f })
            {
                var q = Vector2.Lerp(P2(sg.A), P2(sg.B), t);
                if (grid.TryGetValue(((int)MathF.Floor(q.X / cell), (int)MathF.Floor(q.Y / cell)), out var l))
                    foreach (var k in l) cand.Add(k);
            }
            foreach (var b in cand)
            {
                if (b == a) continue;
                var o = segs[b];
                if (o.Road is not null && o.Road == sg.Road && Math.Abs(o.Index - sg.Index) <= 1) continue;
                var (d, pa, pb) = SegSeg(P2(sg.A), P2(sg.B), P2(o.A), P2(o.B));
                // je Spalte gekürzt (TerrainFill.ClampColumns): Straßen und Prefab-Spuren regelt das
                // Straßengelände selbst (die groben Prefab-Strecken Knoten → Mitte kürzten ganze Stücke)
                if (ExactRoads && TerrainFill.ClampColumns) continue;
                if (o.Road is not null && ExactRoads)
                {
                    // Mit genauen Modellrändern: nur die zugewandte Seite und nur bis knapp vor den
                    // fremden Modellrand kürzen. Früher (halbe Breiten, 1 m Rand, bei Überdeckung beide
                    // Seiten auf 0) blieb zwischen zwei unterschiedlich hohen Fahrbahnen ein Streifen
                    // ohne Straßengelände – das tiefere Füllgelände wurde sichtbar (Wand am Fahrbahnrand).
                    if (d < 0.01f) continue;   // Stoß an Stoß (gemeinsamer Knoten)
                    var odv = P2(o.B) - P2(o.A);
                    if (odv.LengthSquared() < 1e-6f) continue;
                    var oRight = Vector2.Normalize(new Vector2(-odv.Y, odv.X));
                    var sideV = pb - pa;
                    bool onRight = (-dir.Y * sideV.X + dir.X * sideV.Y) >= 0;
                    var (eLs, eRs) = TerrainClearance.Edges(st);
                    var (eLo, eRo) = TerrainClearance.Edges(styles[o.Road][o.Index]);
                    float ownE = onRight ? eRs : eLs;
                    float othE = Vector2.Dot(pa - pb, oRight) >= 0 ? eRo : eLo;
                    if (d >= ownE + MathF.Min(onRight ? right : left, 40f) + othE) continue;
                    if (MathF.Abs(HeightAt(sg, pa) - HeightAt(o, pb)) < minDz) continue;
                    float freeE = MathF.Max(0, d - ownE - othE - TerrainClearance.EdgeClear);
                    if (onRight) right = MathF.Min(right, freeE); else left = MathF.Min(left, freeE);
                    continue;
                }
                // weiter entfernte Straßen begrenzt schon TerrainClearance (Gelände endet dort)
                float reach = sg.HalfWidth + MathF.Min(MathF.Max(left, right), 40f) + o.HalfWidth;
                if (d >= reach) continue;
                // parallele Nachbarfahrbahn (Mittelstreifen): Abstand regelt TerrainClearance
                if (o.Road is not null)
                {
                    var od = P2(o.B) - P2(o.A);
                    if (od.LengthSquared() > 1e-6f && MathF.Abs(Vector2.Dot(Vector2.Normalize(od), dir)) >= 0.9f && d >= sg.HalfWidth + o.HalfWidth) continue;
                }
                float dz = MathF.Abs(HeightAt(sg, pa) - HeightAt(o, pb));
                if (dz < minDz) continue;   // gleiche Höhe: Gelände darf anschließen
                var side = pb - pa;
                bool isRight = side.LengthSquared() < 1e-6f || (-dir.Y * side.X + dir.X * side.Y) >= 0;
                float free = MathF.Max(0, d - sg.HalfWidth - o.HalfWidth - 1f);
                if (d < sg.HalfWidth + o.HalfWidth) { left = 0; right = 0; continue; } // überdeckt direkt
                if (isRight) right = MathF.Min(right, free); else left = MathF.Min(left, free);
            }
            if (left < st.TerrainLeft - 0.01f || right < st.TerrainRight - 0.01f)
            {
                styles[sg.Road][sg.Index] = st with { TerrainLeft = MathF.Round(left, 1), TerrainRight = MathF.Round(right, 1) };
                changed++;
            }
        }
        return changed;
    }

    public static void WriteGeoJson(string path, List<Violation> vs)
    {
        var sb = new StringBuilder("{\"type\":\"FeatureCollection\",\"features\":[\n");
        for (int i = 0; i < vs.Count; i++)
        {
            var v = vs[i];
            sb.Append(CultureInfo.InvariantCulture, $"{{\"type\":\"Feature\",\"properties\":{{\"a\":\"{v.A}\",\"b\":\"{v.B}\",\"kind\":\"{v.Kind}\",\"overlap_m\":{v.Overlap:0.0},\"dz_m\":{v.HeightDiff:0.0}}},");
            // Spielkoordinaten (x, -z) – zum Vergleich mit roads.geojson im selben System gedacht
            sb.Append(CultureInfo.InvariantCulture, $"\"geometry\":{{\"type\":\"Point\",\"coordinates\":[{v.At.X:0.0},{-v.At.Z:0.0}]}}}}");
            sb.Append(i + 1 < vs.Count ? ",\n" : "\n");
        }
        sb.Append("]}\n");
        File.WriteAllText(path, sb.ToString());
    }

    // ------------------------------------------------------------------ Geometrie

    private static bool SharesEnd(Seg s, Seg o) =>
        new[] { s.A, s.B }.Any(p => new[] { o.A, o.B }.Any(q => Vector2.Distance(P2(p), P2(q)) < 0.5f))
        || IsEndSeg(s) || IsEndSeg(o);

    private static bool IsEndSeg(Seg s) => s.Road is not null && (s.Index == 0 || s.Index == s.Road.Points.Count - 2);

    private static bool SharesEndPoint(Seg s, Seg o)
    {
        var road = s.Road is not null ? s : o;
        var pf = s.Road is null ? s : o;
        // angeschlossene Straße: ihre ersten/letzten beiden Stücke (Knoten + gerader
        // Einlauf) liegen zwangsläufig im groben Prefab-Umriss
        var r = road.Road!;
        int n = r.Points.Count - 1;
        var first = new Vector2((float)r.Points[0][0], (float)r.Points[0][2]);
        var last = new Vector2((float)r.Points[^1][0], (float)r.Points[^1][2]);
        if (road.Index <= 1 && Vector2.Distance(P2(pf.A), first) < 1f) return true;
        if (road.Index >= n - 2 && Vector2.Distance(P2(pf.A), last) < 1f) return true;
        if (!IsEndSeg(road)) return false;
        return Vector2.Distance(P2(pf.A), P2(road.A)) < 1f || Vector2.Distance(P2(pf.A), P2(road.B)) < 1f;
    }

    private static float HeightAt(Seg s, Vector2 p)
    {
        var a = P2(s.A); var b = P2(s.B);
        float l2 = Vector2.DistanceSquared(a, b);
        float t = l2 > 0 ? Math.Clamp(Vector2.Dot(p - a, b - a) / l2, 0, 1) : 0;
        return s.A.Y + t * (s.B.Y - s.A.Y);
    }

    private static (float D, Vector2 Pa, Vector2 Pb) SegSeg(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        if (Intersect(a, b, c, d, out var x)) return (0, x, x);
        var cands = new[]
        {
            (DistPointSeg(a, c, d), a, Closest(c, d, a)),
            (DistPointSeg(b, c, d), b, Closest(c, d, b)),
            (DistPointSeg(c, a, b), Closest(a, b, c), c),
            (DistPointSeg(d, a, b), Closest(a, b, d), d),
        };
        var m = cands.MinBy(t => t.Item1);
        return (m.Item1, m.Item2, m.Item3);
    }

    private static bool Intersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out Vector2 x)
    {
        x = default;
        var r = b - a; var s = d - c;
        float den = r.X * s.Y - r.Y * s.X;
        if (MathF.Abs(den) < 1e-9f) return false;
        var w = c - a;
        float t = (w.X * s.Y - w.Y * s.X) / den;
        float u = (w.X * r.Y - w.Y * r.X) / den;
        if (t < 0 || t > 1 || u < 0 || u > 1) return false;
        x = a + t * r;
        return true;
    }

    private static Vector2 Closest(Vector2 a, Vector2 b, Vector2 p)
    {
        var ab = b - a;
        float l2 = ab.LengthSquared();
        if (l2 == 0) return a;
        return a + Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0, 1) * ab;
    }

    private static float DistPointSeg(Vector2 p, Vector2 a, Vector2 b) => Vector2.Distance(p, Closest(a, b, p));
    private static Vector2 P(double[] p) => new((float)p[0], (float)p[2]);
    private static Vector2 P2(Vector3 v) => new(v.X, v.Z);
    private static double Dist(double[] a, double[] b) =>
        Math.Sqrt(Math.Pow(b[0] - a[0], 2) + Math.Pow(b[1] - a[1], 2) + Math.Pow(b[2] - a[2], 2));
    private static float ArcLength(RoadEntry r)
    {
        double acc = 0;
        for (int i = 0; i < r.Points.Count - 1; i++) acc += Dist(r.Points[i], r.Points[i + 1]);
        return (float)acc;
    }
}
