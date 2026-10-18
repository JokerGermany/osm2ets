// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;

namespace Osm2Ets.Writer;

/// <summary>
/// Obergrenze für das Füllgelände über Fahrbahnen. Das Geländeraster (10 m) und die groben
/// Füllquads (bis 16 m) bilden einen Einschnitt nicht ab: liegt eine Straße tiefer als ihre
/// Umgebung (z. B. die B 85 unter der A 70), läge das Füllgelände über ihr und deckte sie zu.
/// Innerhalb der Modellfläche (asymmetrisch, mit Modellmitte) plus <see cref="Margin"/> Meter
/// darf das Füllgelände höchstens auf Fahrbahnhöhe liegen, danach steigt die Grenze mit
/// <see cref="Slope"/> an. Unter Brücken: höchstens <see cref="DeckDrop"/> unter der Fahrbahn
/// (nur knapp, sonst Gruben unter kleinen Brücken und zwischen Zwillingsbrücken).
/// </summary>
public sealed class RoadCap
{
    public static float Margin = Env("OSM2ETS_CAP_MARGIN", 2f);
    public static float Slope = Env("OSM2ETS_CAP_SLOPE", 0.7f);
    public const float DeckDrop = 0.3f, BridgeMargin = 0.5f, BridgeSlope = 0.5f, Cell = 25f, Reach = 12f, LaneHalf = 3f;
    /// <summary>Spurpunkte der Prefabs mit Höhe (vor dem Füllgelände gesetzt).</summary>
    public static List<Vector3>? PrefabLanes;

    private static float Env(string n, float d) =>
        float.TryParse(Environment.GetEnvironmentVariable(n), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;

    // Segment: Anfang/Ende, halbe Modellbreite, Modellmitte (rechts +), Absenkung, Brücke
    private readonly List<(Vector3 A, Vector3 B, float Hw, float Center, float Drop, bool Bridge)> segs = [];
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
                c.Add(a, b, s.Width / 2, s.ModelCenter, run.Bridge ? DeckDrop : 0f, run.Bridge);
            }
        }
        // Prefabs: Spurpunkte (alle ~2 m) mit halber Spurbreite + Rand
        if (PrefabLanes is { } lanes)
            foreach (var p in lanes) c.Add(p, p + new Vector3(0.01f, 0, 0), LaneHalf, 0f, 0f, false);
        else if (prefabs is not null)
            foreach (var (nodes, hw) in prefabs)
            {
                var ctr = nodes.Aggregate(Vector3.Zero, (x, y) => x + y) / nodes.Length;
                foreach (var n in nodes) c.Add(n, ctr, hw, 0f, 0f, false);
            }
        return c;
    }

    private void Add(Vector3 a, Vector3 b, float hw, float center, float drop, bool bridge)
    {
        int i = segs.Count;
        segs.Add((a, b, hw, center, drop, bridge));
        float pad = hw + MathF.Abs(center) + Margin + Reach;
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
    private static readonly Vector2? Probe = Environment.GetEnvironmentVariable("OSM2ETS_CAPPROBE") is { } e
        ? new Vector2(float.Parse(e.Split(',')[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(e.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture)) : null;

    /// <summary>Höchste erlaubte Geländehöhe (vor dem Absenken um FillDrop); +∞ fern von Straßen.</summary>
    public float At(float x, float z)
    {
        if (!index.TryGetValue(((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell)), out var l)) return float.PositiveInfinity;
        float cap = float.PositiveInfinity;
        var p = new Vector2(x, z);
        foreach (var i in l)
        {
            var (a, b, hw, center, drop, bridge) = segs[i];
            var a2 = new Vector2(a.X, a.Z); var ab = new Vector2(b.X, b.Z) - a2;
            float l2 = ab.LengthSquared();
            if (l2 < 1e-8f) continue;
            float u = Vector2.Dot(p - a2, ab) / l2;
            float uc = Math.Clamp(u, 0, 1);
            var dir = ab / MathF.Sqrt(l2);
            var right = new Vector2(-dir.Y, dir.X);
            // seitlicher Abstand vom Modellrand (0 = auf der Fahrbahn), längs: über das Segmentende hinaus
            float lat = MathF.Abs(Vector2.Dot(p - a2, right) - center) - hw;
            float lon = (u < 0 ? -u : u > 1 ? u - 1 : 0) * MathF.Sqrt(l2);
            float outside = MathF.Max(0, MathF.Max(lat, lon));
            float y = a.Y + (b.Y - a.Y) * uc - drop;
            if (bridge)
            {
                // knapp unter dem Überbau, daneben sanft zurück aufs Raster (keine Stufe unter der Kante)
                float beyondB = outside - BridgeMargin;
                if (beyondB > Reach) continue;
                y += MathF.Max(0, beyondB) * BridgeSlope;
            }
            else
            {
                float beyond = outside - Margin;
                if (beyond > Reach) continue;
                y += MathF.Max(0, beyond) * Slope;
            }
            if (Probe is { } pr && Vector2.Distance(pr, p) < 4f && y < 1e6f)
                Console.Error.WriteLine($"CAP at ({x:0.0},{z:0.0}): seg {a} -> {b} hw {hw:0.0} c {center:0.0} bridge {bridge} outside {outside:0.0} cap {y:0.00}");
            cap = MathF.Min(cap, y);
        }
        return cap;
    }

    /// <summary>Gelände für das Füllgelände: Raster, über Fahrbahnen höchstens wie oben.</summary>
    public float Height(TerrainGridEntry grid, float x, float z)
    {
        float h = grid.HeightAt(x, z);
        if (float.IsNaN(h)) return h;
        float c = At(x, z);
        if (c < h) { Interlocked.Increment(ref Lowered); return c; }
        return h;
    }
}
