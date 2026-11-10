// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Fahrspuren gesetzter Prefabs in Weltkoordinaten (aus den Navigationskurven der .ppd),
/// damit das Füllgelände bis an die echte Prefab-Fläche reicht. Die Lage wird aus den
/// Knoten bestimmt: ppd-Knoten (lokal) ↔ Prefab-Knoten (Welt), starre 2D-Abbildung,
/// passende Reihenfolge (ChangeOrigin dreht sie) durch Ausprobieren.
/// </summary>
public static class PrefabFootprint
{
    private static readonly Dictionary<Prefab, PrefabDescriptor> Ppd = [];
    public static int Unfitted;

    public static void Register(Prefab pf, PrefabDescriptor ppd) => Ppd[pf] = ppd;
    public static void Reset() { Ppd.Clear(); Unfitted = 0; }

    /// <summary>
    /// Wie <see cref="All"/>, mit Höhe: Ebene durch die Prefab-Knoten (das Prefab ist starr
    /// geneigt) plus die lokale Spurhöhe der .ppd. Früher inverse Abstandsgewichtung der
    /// Knotenhöhen – bei zwei Knoten weit von linear (ein Drittel des Wegs: 80 % Gewicht auf dem
    /// nahen Knoten), auf 2,5 % Gefälle bis 0,2 m zu hoch: Füllgelände durch den Prefab-Asphalt.
    /// </summary>
    /// <summary>Spurhöhe relativ zu den Knoten (aus: OSM2ETS_LANE_ABSY=1).</summary>
    public static bool UseNodeRelative = Environment.GetEnvironmentVariable("OSM2ETS_LANE_ABSY") != "1";

    public static List<Vector3> All3(Map map)
    {
        var res = new List<Vector3>();
        foreach (var (pf, ppd) in Ppd.ToList())
        {
            if (!map.MapItems.ContainsKey(pf.Uid)) continue;
            float side = Interchanges.ShoulderOf(pf.Model);
            // Kreuzungs-Prefabs (kein Anschluss-Kit): Spurmitten ±JunctionSide (4,5 m) dazu, damit die
            // Kreisscheiben die ganze Fahrbahn samt Rand decken – sonst lag Füllgelände an den
            // Armen über dem Asphalt (mod_ger_67 an der A 70, x 1743, z −2505)
            // dazu tiefer: die Modellfläche fällt zu den Rändern und Ecken hin ab (Quergefälle), mit
            // nur 0,18 m Abstand zur Knotenebene kam die Erde in Innenecken durch (mod_ger_67, x 1768, z −2540)
            float drop = 0f;
            if (side <= 0) { side = JunctionSide; drop = JunctionDrop; }
            var pts = LanesOf(pf, ppd, side);
            if (pts is null) continue;
            var (h0, g, o) = Plane(pf);
            // Die Ebene geht durch die Knoten; Spurhöhen relativ zur mittleren Knotenhöhe der .ppd
            // (sonst zählt z. B. bei mod_ger_29 – alle Knoten 0,22 m über Null – der Versatz doppelt
            // und das Gelände darf 0,2 m über der Fahrbahn liegen)
            float ny = UseNodeRelative ? ppd.Nodes.Average(k => k.Position.Y) : 0f;
            foreach (var (p, ly) in pts)
                res.Add(new Vector3(p.X, h0 + Vector2.Dot(g, p - o) + ly - ny - drop, p.Y));
        }
        return res;
    }

    /// <summary>
    /// Neigungsebene eines Prefabs aus seinen Knoten: Höhe = h0 + g·(p − o). Bei zwei Knoten
    /// nur längs der Verbindung (quer eben), sonst kleinste Quadrate.
    /// </summary>
    public static (float H0, Vector2 G, Vector2 O) Plane(Prefab pf)
    {
        var n = pf.Nodes.Select(k => k.Position).ToArray();
        var o = new Vector2(n.Average(v => v.X), n.Average(v => v.Z));
        float h0 = n.Average(v => v.Y);
        if (n.Length < 2) return (h0, Vector2.Zero, o);
        if (n.Length == 2)
        {
            var d = new Vector2(n[1].X - n[0].X, n[1].Z - n[0].Z);
            float l2 = d.LengthSquared();
            return l2 < 1e-6f ? (h0, Vector2.Zero, o) : (h0, d * ((n[1].Y - n[0].Y) / l2), o);
        }
        // h − h0 = gx·dx + gz·dz (Normalgleichungen)
        double sxx = 0, sxz = 0, szz = 0, sxh = 0, szh = 0;
        foreach (var v in n)
        {
            double dx = v.X - o.X, dz = v.Z - o.Y, dh = v.Y - h0;
            sxx += dx * dx; sxz += dx * dz; szz += dz * dz; sxh += dx * dh; szh += dz * dh;
        }
        double det = sxx * szz - sxz * sxz;
        if (Math.Abs(det) < 1e-6) return (h0, Vector2.Zero, o);
        return (h0, new Vector2((float)((sxh * szz - szh * sxz) / det), (float)((szh * sxx - sxh * sxz) / det)), o);
    }

    public static int PitchFixed;

    /// <summary>
    /// Knotendrehungen aller Prefabs: Gierung bleibt, Neigung = Gefälle der Prefab-Ebene in
    /// Knotenrichtung. Das Spiel setzt ein Prefab nach der Drehung seines Ursprungsknotens; bei
    /// zwei aneinanderhängenden Prefabs (Aufweitung → Teilung) behielt der gemeinsame Knoten die
    /// Drehung aus TruckLib mit umgekehrter Neigung – das Teilungs-Prefab kippte im Spiel nach
    /// oben, seine Ausgänge lagen 1,9 m höher als geschrieben, die angeschlossenen Straßen samt
    /// Gelände mit ihnen (Wände entlang Rampe und Hauptfahrbahn hinter der Ausfahrt).
    /// </summary>
    public static void FixPitch(Map map)
    {
        PitchFixed = 0;
        foreach (var pf in map.MapItems.Values.OfType<Prefab>().ToList())
        {
            if (pf.Nodes.Count < 2) continue;
            var (_, g, _) = Plane(pf);
            foreach (var nd in pf.Nodes)
            {
                var f = Vector3.Transform(-Vector3.UnitZ, nd.Rotation);
                var fh = new Vector2(f.X, f.Z);
                if (fh.LengthSquared() < 1e-8f) continue;
                fh = Vector2.Normalize(fh);
                float want = Vector2.Dot(g, fh);
                float have = f.Y / MathF.Sqrt(f.X * f.X + f.Z * f.Z);
                if (MathF.Abs(want - have) < 1e-3f) continue;
                nd.Rotation = TruckLib.MathEx.GetNodeRotation(Vector3.Normalize(new Vector3(fh.X, want, fh.Y)));
                PitchFixed++;
            }
        }
    }

    /// <summary>
    /// Autobahn-Anschluss-Prefabs: zusätzliche Spurpunkte so weit neben jeder Spur (m). Ihr Modell
    /// hat neben der äußeren Spur einen Seitenstreifen; mit nur 3 m um die Spurmitte lag das
    /// Gelände dort bis 0,8 m über dem Rand (A 9, Aufweitung vor der Ausfahrt zur A 70). Beim
    /// dreispurigen Satz reichten 2,5 m nicht (Rand bis 0,9 m darüber); 4,5 m bei hw2 gäben an
    /// der A 70 (x = 4234, z = -1841) neue Wände.
    /// </summary>
    public const float ShoulderOffset = 2.5f, ShoulderOffset3 = 4.5f;
    /// <summary>Zusätzliche Tiefe des Füllgeländes unter Kreuzungs-Prefabs (m; OSM2ETS_JUNCTION_DROP).</summary>
    public static float JunctionDrop = float.TryParse(Environment.GetEnvironmentVariable("OSM2ETS_JUNCTION_DROP"), System.Globalization.CultureInfo.InvariantCulture, out var jd) ? jd : 0.3f;
    /// <summary>Seitlicher Zusatz für Kreuzungs-Prefabs (m; OSM2ETS_JUNCTION_SIDE, 0 = aus).</summary>
    public static float JunctionSide = float.TryParse(Environment.GetEnvironmentVariable("OSM2ETS_JUNCTION_SIDE"), System.Globalization.CultureInfo.InvariantCulture, out var js) ? js : 4.5f;

    private static List<(Vector2 P, float LocalY)>? LanesOf(Prefab pf, PrefabDescriptor ppd, float side = 0f)
    {
        if (!Fit(pf, ppd, out var rot, out var tr)) return null;
        var pts = new List<(Vector2, float)>();
        (Vector2, float) W(Vector3 l) => (Rot(rot, new Vector2(l.X, l.Z)) + tr, l.Y);
        foreach (var c in ppd.NavCurves)
        {
            var a = c.StartPosition; var b = c.EndPosition;
            float len = Vector3.Distance(a, b);
            var ta = Dir(c.StartRotation, b - a) * len;
            var tb = Dir(c.EndRotation, b - a) * len;
            int n = Math.Max(2, (int)(len / 2f) + 1);
            for (int i = 0; i <= n; i++)
            {
                float t = (float)i / n, t2 = t * t, t3 = t2 * t;
                var p = (2 * t3 - 3 * t2 + 1) * a + (t3 - 2 * t2 + t) * ta + (-2 * t3 + 3 * t2) * b + (t3 - t2) * tb;
                pts.Add(W(p));
                if (side > 0)
                {
                    var d = (6 * t2 - 6 * t) * a + (3 * t2 - 4 * t + 1) * ta + (-6 * t2 + 6 * t) * b + (3 * t2 - 2 * t) * tb;
                    var q = new Vector3(-d.Z, 0, d.X);
                    if (q.LengthSquared() > 1e-8f)
                    {
                        q = Vector3.Normalize(q) * side;
                        pts.Add(W(p + q)); pts.Add(W(p - q));
                    }
                }
            }
        }
        return pts;
    }

    /// <summary>Spurpunkte nur der Anschluss-Prefabs (Autobahn-Ausfahrten/-Einfahrten).</summary>
    public static List<Vector2> KitLanes(Map map)
    {
        var res = new List<Vector2>();
        foreach (var (pf, ppd) in Ppd.ToList())
        {
            if (!map.MapItems.ContainsKey(pf.Uid)) continue;
            float side = Interchanges.ShoulderOf(pf.Model);
            if (side <= 0) continue;
            var pts = LanesOf(pf, ppd, side);
            if (pts is null) continue;
            res.AddRange(pts.Select(p => p.P));
        }
        return res;
    }

    public static List<Vector2> All(Map map)
    {
        var pts = new List<Vector2>();
        Unfitted = 0;
        foreach (var (pf, ppd) in Ppd)
        {
            if (!map.MapItems.ContainsKey(pf.Uid)) continue;
            if (!Fit(pf, ppd, out var rot, out var tr)) { Unfitted++; continue; }
            Vector2 W(Vector3 l) => Rot(rot, new Vector2(l.X, l.Z)) + tr;
            foreach (var c in ppd.NavCurves)
            {
                var a = c.StartPosition; var b = c.EndPosition;
                float len = Vector3.Distance(a, b);
                var ta = Dir(c.StartRotation, b - a) * len;
                var tb = Dir(c.EndRotation, b - a) * len;
                int n = Math.Max(2, (int)(len / 2f) + 1);
                for (int i = 0; i <= n; i++)
                {
                    float t = (float)i / n, t2 = t * t, t3 = t2 * t;
                    var p = (2 * t3 - 3 * t2 + 1) * a + (t3 - 2 * t2 + t) * ta + (-2 * t3 + 3 * t2) * b + (t3 - t2) * tb;
                    pts.Add(W(p));
                }
            }
        }
        return pts;
    }

    // Fahrtrichtung aus der Rotation; Vorzeichen so, dass sie grob zum Kurvenende zeigt
    private static Vector3 Dir(Quaternion q, Vector3 chord)
    {
        var d = Vector3.Transform(-Vector3.UnitZ, q);
        if (Vector3.Dot(d, chord) < 0) d = -d;
        return d.LengthSquared() > 1e-6f ? Vector3.Normalize(d) : Vector3.Normalize(chord + new Vector3(1e-6f));
    }

    private static Vector2 Rot(float ang, Vector2 v) =>
        new(v.X * MathF.Cos(ang) - v.Y * MathF.Sin(ang), v.X * MathF.Sin(ang) + v.Y * MathF.Cos(ang));

    private static bool Fit(Prefab pf, PrefabDescriptor ppd, out float rot, out Vector2 tr)
    {
        rot = 0; tr = Vector2.Zero;
        int n = ppd.Nodes.Count;
        if (n < 2 || pf.Nodes.Count != n) return false;
        float bestErr = float.MaxValue;
        for (int shift = 0; shift < n; shift++)
        {
            var L = new Vector2[n]; var Wp = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                var l = ppd.Nodes[i].Position; L[i] = new Vector2(l.X, l.Z);
                var w = pf.Nodes[(i - shift + n) % n].Position; Wp[i] = new Vector2(w.X, w.Z);
            }
            var cl = L.Aggregate(Vector2.Zero, (s, v) => s + v) / n;
            var cw = Wp.Aggregate(Vector2.Zero, (s, v) => s + v) / n;
            float sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                var a = L[i] - cl; var b = Wp[i] - cw;
                sxx += a.X * b.X + a.Y * b.Y;
                sxy += a.X * b.Y - a.Y * b.X;
            }
            float ang = MathF.Atan2(sxy, sxx);
            var t = cw - Rot(ang, cl);
            float err = 0;
            for (int i = 0; i < n; i++) err = MathF.Max(err, Vector2.Distance(Rot(ang, L[i]) + t, Wp[i]));
            // Knotenreihenfolge der .ppd bevorzugen: bei Prefabs, deren Knoten auf einer Linie liegen
            // (z. B. mod_ger_38, beide Knoten bei x = 0), passt die um 180° gedrehte Zuordnung
            // ebenso gut – dann lägen die Spuren gespiegelt
            if (err < bestErr - (shift == 0 ? 0f : 0.1f)) { bestErr = err; rot = ang; tr = t; }
        }
        return bestErr < 1.5f;
    }
}
