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
            var pts = LanesOf(pf, ppd);
            if (pts is null) continue;
            var (h0, g, o) = Plane(pf);
            // Die Ebene geht durch die Knoten; Spurhöhen relativ zur mittleren Knotenhöhe der .ppd
            // (sonst zählt z. B. bei mod_ger_29 – alle Knoten 0,22 m über Null – der Versatz doppelt
            // und das Gelände darf 0,2 m über der Fahrbahn liegen)
            float ny = UseNodeRelative ? ppd.Nodes.Average(k => k.Position.Y) : 0f;
            foreach (var (p, ly) in pts)
                res.Add(new Vector3(p.X, h0 + Vector2.Dot(g, p - o) + ly - ny, p.Y));
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

    private static List<(Vector2 P, float LocalY)>? LanesOf(Prefab pf, PrefabDescriptor ppd)
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
            }
        }
        return pts;
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
            if (err < bestErr) { bestErr = err; rot = ang; tr = t; }
        }
        return bestErr < 1.5f;
    }
}
