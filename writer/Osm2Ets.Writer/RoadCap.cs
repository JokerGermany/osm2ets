// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;

namespace Osm2Ets.Writer;

/// <summary>
/// Obergrenze für das Füllgelände nahe Fahrbahnen. Das Geländeraster (10 m) und die groben
/// Füllquads (bis 16 m) bilden einen Einschnitt nicht ab: liegt eine Straße tiefer als ihre
/// Umgebung (z. B. die B 85 unter der A 70), läge das Füllgelände an ihren Rändern oder quer
/// über ihr höher als die Fahrbahn und deckte sie zu. Bis <see cref="Flat"/> Meter neben dem
/// Fahrbahnrand darf das Füllgelände höchstens auf Fahrbahnhöhe liegen, danach steigt die
/// Grenze mit <see cref="Slope"/> an. Sichtbar ist dort meist das Straßengelände, das darüber liegt.
/// Brücken und Tunnel zählen nicht (darunter liegt Gelände auf Bodenhöhe).
/// </summary>
public sealed class RoadCap
{
    public const float Flat = 8f, Slope = 0.5f, Cell = 25f;
    private readonly List<(Vector3 A, Vector3 B, float Hw, float Drop)> segs = [];
    /// <summary>Unter Brückenfahrbahnen höchstens so tief unter der Fahrbahn (Überbau).</summary>
    public const float DeckDrop = 1.2f;
    private readonly Dictionary<(int, int), List<int>> index = [];

    public static RoadCap Build(List<RoadEntry> roads, Dictionary<RoadEntry, ResolvedStyle[]> styles,
                                List<(Vector3[] Nodes, float HalfWidth)>? prefabs)
    {
        var c = new RoadCap();
        foreach (var r in roads)
        {
            if (!styles.TryGetValue(r, out var st)) continue;
            for (int k = 0; k < r.Points.Count - 1; k++)
            {
                var run = r.Runs.FirstOrDefault(x => x.From <= k && k < x.To);
                if (run is null || run.Tunnel) continue;
                var s = st[Math.Min(k, st.Length - 1)];
                var a = MapWriter.ToVector(r.Points[k]); var b = MapWriter.ToVector(r.Points[k + 1]);
                // Modellmitte berücksichtigen (asymmetrische Modelle): Breite großzügig.
                // Brücken: Gelände darunter auf Bodenhöhe, aber nicht durch den Überbau hindurch
                // (an den Widerlagern läge das Raster sonst auf Fahrbahnhöhe) – nur unter der Fahrbahn selbst.
                if (run.Bridge) c.Add(a, b, s.Width / 2 + MathF.Abs(s.ModelCenter), DeckDrop, flat: 1f);
                else c.Add(a, b, s.Width / 2 + MathF.Abs(s.ModelCenter), 0f, Flat);
            }
        }
        if (prefabs is not null)
            foreach (var (nodes, hw) in prefabs)
            {
                var ctr = nodes.Aggregate(Vector3.Zero, (x, y) => x + y) / nodes.Length;
                foreach (var n in nodes) c.Add(n, ctr, hw, 0f, Flat);
            }
        return c;
    }

    private void Add(Vector3 a, Vector3 b, float hw, float drop, float flat)
    {
        int i = segs.Count;
        segs.Add((a, b, hw + flat - Flat, drop));
        float pad = hw + Flat + 20;
        int x0 = (int)MathF.Floor((MathF.Min(a.X, b.X) - pad) / Cell), x1 = (int)MathF.Floor((MathF.Max(a.X, b.X) + pad) / Cell);
        int z0 = (int)MathF.Floor((MathF.Min(a.Z, b.Z) - pad) / Cell), z1 = (int)MathF.Floor((MathF.Max(a.Z, b.Z) + pad) / Cell);
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                if (!index.TryGetValue((x, z), out var l)) index[(x, z)] = l = [];
                l.Add(i);
            }
    }

    public static int Lowered;

    /// <summary>Höchste erlaubte Geländehöhe (vor dem Absenken um FillDrop); +∞ fern von Straßen.</summary>
    public float At(float x, float z)
    {
        if (!index.TryGetValue(((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell)), out var l)) return float.PositiveInfinity;
        float cap = float.PositiveInfinity;
        var p = new Vector2(x, z);
        foreach (var i in l)
        {
            var (a, b, hw, drop) = segs[i];
            var a2 = new Vector2(a.X, a.Z); var ab = new Vector2(b.X, b.Z) - a2;
            float l2 = ab.LengthSquared();
            float u = l2 > 1e-6f ? Math.Clamp(Vector2.Dot(p - a2, ab) / l2, 0, 1) : 0;
            float d = Vector2.Distance(p, a2 + ab * u);
            float beyond = d - hw - Flat;
            if (beyond > 20) continue;
            float y = a.Y + (b.Y - a.Y) * u - drop + MathF.Max(0, beyond) * Slope;
            if (drop > 0 && beyond > 0) continue;   // Brücke: nur direkt unter dem Überbau
            cap = MathF.Min(cap, y);
        }
        return cap;
    }

    /// <summary>Gelände für das Füllgelände: Raster, nahe Straßen höchstens wie oben.</summary>
    public float Height(TerrainGridEntry grid, float x, float z)
    {
        float h = grid.HeightAt(x, z);
        if (float.IsNaN(h)) return h;
        float c = At(x, z);
        if (c < h) { Interlocked.Increment(ref Lowered); return c; }
        return h;
    }
}
