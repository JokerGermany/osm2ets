// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using TruckLib;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Flächiges Gelände zwischen den Straßen: Terrain-Items (Geländestreifen ohne
/// Fahrbahn) in West-Ost-Reihen. Höhe entlang der Reihe aus dem Geländeraster des
/// Zwischenformats; quer dazu ist jeder Streifen eben (ETS2-Profil), deshalb können
/// zwischen zwei Reihen am Hang kleine Stufen entstehen (bei 40 m Abstand und 4 %
/// Gefälle etwa 0,8 m).
/// Wo Straßen, ihre Geländestreifen oder Prefabs liegen, bleibt das Feld frei.
/// </summary>
public static class TerrainFill
{
    public const float RowSpacing = 40f;   // Abstand der Reihen (m)
    public const float Overlap = 2f;       // jede Seite reicht so weit über die Nachbarreihe (keine Lücken)
    public const float Sample = 5f;        // Abtastung entlang der Reihe (m)
    public const float MaxPiece = 200f;    // längstes Terrain-Stück (m)
    private const float MaskCell = 4f;

    /// <summary>Höhenpunkte quer setzen (ungeprüft im Spiel); sonst ebene Streifen wie v17.</summary>
    public static bool Shape = true;
    public static int Mismatch;

    public sealed record Result(int Items, int Rows, double AreaKm2);

    public static Result Build(Map map, TerrainGridEntry grid, List<RoadEntry> roads,
                               Dictionary<RoadEntry, ResolvedStyle[]> styles,
                               List<(Vector3[] Nodes, float HalfWidth)> prefabs)
    {
        float x0 = (float)grid.Origin[0], z0 = (float)grid.Origin[1];
        float x1 = x0 + (float)grid.Cell * (grid.Nx - 1), z1 = z0 + (float)grid.Cell * (grid.Nz - 1);
        int mw = (int)((x1 - x0) / MaskCell) + 1, mh = (int)((z1 - z0) / MaskCell) + 1;
        var mask = new bool[mw * mh];

        void Mark(Vector2 c, float r)
        {
            int i0 = Math.Max(0, (int)((c.X - r - x0) / MaskCell)), i1 = Math.Min(mw - 1, (int)((c.X + r - x0) / MaskCell) + 1);
            int j0 = Math.Max(0, (int)((c.Y - r - z0) / MaskCell)), j1 = Math.Min(mh - 1, (int)((c.Y + r - z0) / MaskCell) + 1);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                    if (Vector2.Distance(new Vector2(x0 + i * MaskCell, z0 + j * MaskCell), c) <= r) mask[j * mw + i] = true;
        }
        // Straße + eigene Geländestreifen (seitenrichtig), 1 m Rand
        foreach (var r in roads)
        {
            var st = styles[r];
            for (int k = 0; k < r.Points.Count - 1; k++)
            {
                var a = new Vector2((float)r.Points[k][0], (float)r.Points[k][2]);
                var b = new Vector2((float)r.Points[k + 1][0], (float)r.Points[k + 1][2]);
                var dir = b - a; float len = dir.Length();
                if (len < 1e-3f) continue;
                dir /= len;
                var right = new Vector2(-dir.Y, dir.X);
                float hw = st[k].Width / 2;
                // Füllgelände darf 3 m über den Geländestreifen der Straße reichen (keine Lücke)
                float wl = hw + MathF.Max(1, st[k].TerrainLeft - 3), wr = hw + MathF.Max(1, st[k].TerrainRight - 3);
                for (float s = 0; s <= len; s += MaskCell / 2)
                {
                    var c = a + dir * s;
                    for (float o = -wl; o <= wr; o += MaskCell / 2)
                        Mark(c + right * o, MaskCell * 0.75f);
                }
            }
        }
        foreach (var (nodes, hw) in prefabs)
        {
            var c3 = nodes.Aggregate(Vector3.Zero, (s, n) => s + n) / nodes.Length;
            var c = new Vector2(c3.X, c3.Z);
            float rad = nodes.Max(n => Vector2.Distance(new Vector2(n.X, n.Z), c));
            Mark(c, rad + hw + 4);
        }

        bool Free(float x, float z)
        {
            int i = (int)MathF.Round((x - x0) / MaskCell), j = (int)MathF.Round((z - z0) / MaskCell);
            if (i < 0 || j < 0 || i >= mw || j >= mh) return false;
            return !mask[j * mw + i];
        }
        float half = RowSpacing / 2 + Overlap;
        // freie Breite nach Norden (−z, links bei Fahrt nach Osten) bzw. Süden (+z, rechts)
        float Extent(float x, float z, float sign)
        {
            float e = 0;
            for (float d = 2; d <= half; d += 2) { if (!Free(x, z + sign * d)) break; e = d; }
            return e;
        }

        var material = new Token(Road.Add(new Map(), Vector3.Zero, new Vector3(10, 0, 0), "ger1", 1, 1)
                                     .Right.Terrain.QuadData.BrushMaterials[0].Name.ToString());
        int items = 0, rows = 0; double area = 0;
        for (float z = z0 + RowSpacing / 2 + 10; z < z1 - RowSpacing / 2 - 10; z += RowSpacing)
        {
            // Läufe freier Abtastpunkte
            var run = new List<(float X, float L, float R)>();
            void Flush()
            {
                if (run.Count >= 2) items += Emit(map, grid, run, z, material, ref area);
                run.Clear();
            }
            bool any = false;
            for (float x = x0 + 10; x < x1 - 10; x += Sample)
            {
                float l = Free(x, z) ? Extent(x, z, -1) : -1, r = Free(x, z) ? Extent(x, z, +1) : -1;
                if (l < 2 || r < 2 || float.IsNaN(grid.HeightAt(x, z))) { Flush(); continue; }
                run.Add((x, l, r)); any = true;
            }
            Flush();
            if (any) rows++;
        }
        return new Result(items, rows, area / 1e6);
    }

    private static int Emit(Map map, TerrainGridEntry grid, List<(float X, float L, float R)> run, float z,
                            Token material, ref double area)
    {
        // Knoten gierig so weit wie möglich setzen: Höhe linear (±0,4 m), Breiten fast gleich,
        // höchstens MaxPiece m – spart viele Items auf flachem, freiem Feld
        float H(int i) => grid.HeightAt(run[i].X, z);
        var idx = new List<int> { 0 };
        int start = 0;
        while (start < run.Count - 1)
        {
            int best = start + 1;
            for (int end = start + 2; end < run.Count && run[end].X - run[start].X <= MaxPiece; end++)
            {
                bool ok = true;
                float lmin = float.MaxValue, lmax = 0, rmin = float.MaxValue, rmax = 0;
                for (int i = start; i <= end && ok; i++)
                {
                    float u = (run[i].X - run[start].X) / (run[end].X - run[start].X);
                    if (MathF.Abs(H(start) + (H(end) - H(start)) * u - H(i)) > 0.4f) ok = false;
                    lmin = MathF.Min(lmin, run[i].L); lmax = MathF.Max(lmax, run[i].L);
                    rmin = MathF.Min(rmin, run[i].R); rmax = MathF.Max(rmax, run[i].R);
                }
                if (!ok || lmax - lmin > 4 || rmax - rmin > 4) break;
                best = end;
            }
            idx.Add(best);
            start = best;
        }
        if (run[idx[^1]].X - run[idx[0]].X < 2) return 0;

        Terrain? t = null;
        int n = 0;
        for (int k = 0; k < idx.Count - 1; k++)
        {
            int a = idx[k], b = idx[k + 1];
            float l = float.MaxValue, r = float.MaxValue;
            for (int i = a; i <= b; i++) { l = MathF.Min(l, run[i].L); r = MathF.Min(r, run[i].R); }
            var pa = new Vector3(run[a].X, grid.HeightAt(run[a].X, z), z);
            var pb = new Vector3(run[b].X, grid.HeightAt(run[b].X, z), z);
            t = t is null ? Terrain.Add(map, pa, pb, material, l, r) : t.Append(pb, material, l, r);
            t.Boundary = false;
            t.ViewDistance = 1000;
            t.Left.Terrain.Noise = TerrainNoise.Percent0;
            t.Right.Terrain.Noise = TerrainNoise.Percent0;
            // gröberes Raster (16 m statt 4 m): flaches Feld braucht keine feinen Quads
            t.StepSize = StepSize.Meters16;
            t.Recalculate();
            if (Shape)
            {
                if (ShapeSide(t.Left.Terrain, grid, pa, pb, l, -1, 16) < 0) Mismatch++;
                if (ShapeSide(t.Right.Terrain, grid, pa, pb, r, +1, 16) < 0) Mismatch++;
            }
            map.AddSector(Map.GetSectorOfCoordinate((pa + pb) / 2));
            area += (pb.X - pa.X) * (l + r);
            n++;
        }
        return n;
    }

    /// <summary>Feste Reihenbreiten des Spiels quer zur Streifenrichtung (map-docs, „Quad Info Struct“).</summary>
    private static readonly float[] RowSizes = [1, 1, 1, 2, 6, 6, 10, 10, 10, 20, 40, 50, 50, 50, 100];

    /// <summary>Abstände der Rasterzeilen von der Mittellinie für eine Geländebreite.</summary>
    internal static List<float> RowDistances(float size)
    {
        var d = new List<float> { 0 };
        float acc = 0;
        foreach (var r in RowSizes)
        {
            if (acc >= size - 1e-3f) break;
            acc = MathF.Min(acc + r, size);
            d.Add(acc);
        }
        return d;
    }

    /// <summary>
    /// Höhenpunkte eines Streifens auf das Geländeraster legen. Laut map-docs speichert ein
    /// Offset die ABSOLUTE Lage des Rasterpunkts relativ zum Anfangsknoten (nicht eine
    /// Verschiebung). Zeilenabstände nach RowSizes, Spalten im Abstand der Schrittweite.
    /// Die Streifen laufen genau nach Osten (+x); links = Norden (−z), rechts = Süden (+z).
    /// </summary>
    internal static int ShapeSide(RoadTerrain side, TerrainGridEntry grid, Vector3 pa, Vector3 pb, float size, float zSign, float step)
    {
        var q = side.QuadData;
        q.Offsets.Clear();
        var rowsD = RowDistances(size);
        int rows = q.Rows, cols = q.Cols;
        if (rows < 1 || cols < 1 || size <= 0) return 0;
        if (rowsD.Count != rows + 1 && rowsD.Count != rows) return -1;  // Raster passt nicht zur Annahme
        float len = pb.X - pa.X;
        int n = 0;
        for (int j = 1; j < rowsD.Count; j++)
            for (int i = 0; i < cols; i++)
            {
                float x = cols > 1 ? len * i / (cols - 1) : 0;
                float z = pa.Z + zSign * rowsD[j];
                float h = grid.HeightAt(pa.X + x, z);
                if (float.IsNaN(h)) continue;
                q.Offsets.Add(new VertexData { X = (ushort)i, Y = (ushort)j, Data = new Vector3(x, h - pa.Y, zSign * rowsD[j]) });
                n++;
            }
        return n;
    }
}
