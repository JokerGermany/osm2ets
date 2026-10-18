// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Schmales Straßengelände (Mittelstreifen) neigen: liegt die parallele Nachbarfahrbahn
/// tiefer, fällt das Gelände von der eigenen Fahrbahn bis auf deren Höhe ab (Böschung),
/// statt flach zu enden und eine senkrechte Stufe sichtbar zu lassen. Höhenpunkte wie
/// Variante B (osm_ttest4): lokal entlang der Straße, Abstand ab Straßenmitte.
/// </summary>
public static class MedianSlopes
{
    public const float MaxSide = 12f;      // nur schmale Seiten (Mittelstreifen)
    public const float MinDrop = 0.2f;

    private readonly record struct Seg(RoadEntry Road, Vector3 A, Vector3 B, float Hw);

    public static int Apply(List<RoadEntry> roads, Dictionary<RoadEntry, ResolvedStyle[]> styles,
                            Dictionary<RoadEntry, Road> first, Dictionary<RoadEntry, Road> last)
    {
        var segs = new List<Seg>();
        var grid = new Dictionary<(int, int), List<int>>();
        const float cell = 50f;
        foreach (var r in roads)
            for (int i = 0; i < r.Points.Count - 1; i++)
            {
                var a = V(r.Points[i]); var b = V(r.Points[i + 1]);
                int k = segs.Count; segs.Add(new Seg(r, a, b, styles[r][i].Width / 2));
                for (int gx = (int)MathF.Floor((MathF.Min(a.X, b.X) - 30) / cell); gx <= (int)MathF.Floor((MathF.Max(a.X, b.X) + 30) / cell); gx++)
                for (int gz = (int)MathF.Floor((MathF.Min(a.Z, b.Z) - 30) / cell); gz <= (int)MathF.Floor((MathF.Max(a.Z, b.Z) + 30) / cell); gz++)
                {
                    if (!grid.TryGetValue((gx, gz), out var l)) grid[(gx, gz)] = l = [];
                    l.Add(k);
                }
            }

        // Höhe der nächsten anderen, etwa parallelen Fahrbahn an Punkt p (NaN = keine)
        float Neighbour(RoadEntry self, Vector2 p, Vector2 dir, float maxDist)
        {
            if (!grid.TryGetValue(((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell)), out var l)) return float.NaN;
            float best = float.MaxValue, h = float.NaN;
            foreach (var k in l)
            {
                var s = segs[k];
                if (s.Road == self) continue;
                var a = new Vector2(s.A.X, s.A.Z); var b = new Vector2(s.B.X, s.B.Z);
                var ab = b - a; float l2 = ab.LengthSquared(); if (l2 < 1e-4f) continue;
                if (MathF.Abs(Vector2.Dot(Vector2.Normalize(ab), dir)) < 0.9f) continue;
                float t = Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0, 1);
                float d = Vector2.Distance(a + ab * t, p) - s.Hw;
                if (d < best && d < maxDist) { best = d; h = s.A.Y + (s.B.Y - s.A.Y) * t; }
            }
            return h;
        }

        int n = 0;
        foreach (var r in roads)
        {
            if (!first.TryGetValue(r, out var it)) continue;
            float hw = styles[r][0].Width / 2;
            for (var rd = it; rd is not null; rd = rd.ForwardNode.ForwardItem as Road)
            {
                foreach (var (side, sign) in new[] { (rd.Left.Terrain, -1f), (rd.Right.Terrain, 1f) })
                    if (side.Size > 0 && side.Size <= MaxSide && Shape(r, side, rd.Node.Position, rd.ForwardNode.Position, hw, sign)) n++;
                if (rd == last[r]) break;
            }
        }
        return n;

        bool Shape(RoadEntry self, RoadTerrain side, Vector3 pa, Vector3 pb, float hw, float zSign)
        {
            var q = side.QuadData;
            int rows = q.Rows, cols = q.Cols;
            var rowsD = TerrainFill.RowDistances(side.Size);
            if (rows < 1 || cols < 1 || (rowsD.Count != rows + 1 && rowsD.Count != rows)) return false;
            var f = new Vector2(pb.X - pa.X, pb.Z - pa.Z); float len = f.Length(); if (len < 1e-3f) return false;
            f /= len; var rn = new Vector2(-f.Y, f.X);
            float size = side.Size;
            var offs = new List<VertexData>();
            bool any = false;
            for (int i = 0; i <= cols; i++)
            {
                float x = len * i / cols;
                float own = pa.Y + (pb.Y - pa.Y) * i / cols;
                var edge = new Vector2(pa.X, pa.Z) + f * x + rn * (zSign * (hw + size));
                float nh = Neighbour(self, edge, f, size + 3);
                if (float.IsNaN(nh) || own - nh < MinDrop) nh = own; else any = true;
                for (int j = 1; j < rowsD.Count; j++)
                {
                    float h = own + (nh - own) * rowsD[j] / size;
                    offs.Add(new VertexData { X = (ushort)i, Y = (ushort)j, Data = new Vector3(x, h - pa.Y, zSign * (hw + rowsD[j])) });
                }
            }
            if (!any) return false;
            q.Offsets.Clear();
            q.Offsets.AddRange(offs);
            return true;
        }
    }

    private static Vector3 V(double[] p) => new((float)p[0], (float)p[1], (float)p[2]);
}
