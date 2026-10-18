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

    /// <summary>Wie <see cref="All"/>, mit Höhe (aus den Knotenhöhen des Prefabs, inverse Abstandsgewichtung).</summary>
    public static List<Vector3> All3(Map map)
    {
        var res = new List<Vector3>();
        foreach (var (pf, ppd) in Ppd.ToList())
        {
            if (!map.MapItems.ContainsKey(pf.Uid)) continue;
            var pts = LanesOf(pf, ppd);
            if (pts is null) continue;
            var nodes = pf.Nodes.Select(n => n.Position).ToArray();
            foreach (var p in pts)
            {
                double ws = 0, wy = 0;
                foreach (var n in nodes)
                {
                    double d = Math.Max(1, Vector2.Distance(p, new Vector2(n.X, n.Z)));
                    double w = 1 / (d * d); ws += w; wy += w * n.Y;
                }
                res.Add(new Vector3(p.X, (float)(wy / ws), p.Y));
            }
        }
        return res;
    }

    private static List<Vector2>? LanesOf(Prefab pf, PrefabDescriptor ppd)
    {
        if (!Fit(pf, ppd, out var rot, out var tr)) return null;
        var pts = new List<Vector2>();
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
