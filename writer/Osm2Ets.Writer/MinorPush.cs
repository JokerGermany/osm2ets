using System.Numerics;

namespace Osm2Ets.Writer;

/// <summary>
/// Nebenstraßen (Wege, Zufahrten, kleine Straßen) seitlich von Autobahnen, Rampen und
/// Anschluss-Prefabs wegschieben, wenn ihre Fahrbahnen sich überdecken – etwa nachdem eine Rampe
/// in einem Bogen in ihr Prefab geführt wurde. Nur nicht verbundene Straßen auf gleicher Höhe;
/// angeschlossene Enden bleiben liegen (Verschiebung läuft zu ihnen hin aus).
/// Aus: OSM2ETS_NO_MINOR_PUSH=1.
/// </summary>
public static class MinorPush
{
    public static bool Enabled = Environment.GetEnvironmentVariable("OSM2ETS_NO_MINOR_PUSH") != "1";
    public const float Gap = 0.5f;        // Luft zwischen den Modellrändern (m)
    public const float MaxPush = 12f;     // mehr nicht (sonst eher falsche Zuordnung)
    public const float Taper = 40f;       // Ein-/Auslauf der Verschiebung entlang der Straße (m)
    public static float MaxDz = float.TryParse(Environment.GetEnvironmentVariable("OSM2ETS_PUSH_MAXDZ"), System.Globalization.CultureInfo.InvariantCulture, out var md) ? md : 6f;        // nur bei Höhenunterschied darunter (OSM2ETS_PUSH_MAXDZ)
    public const float Slope = 1.5f;      // Böschung zwischen den Rändern: so viele m Breite je m Höhe

    public const float MinParallel = 0.87f;  // |cos| des Winkels zur großen Straße (≤ 30°)
    public static int Moved;

    private static bool IsMajor(RoadEntry r) =>
        r.Runs.Any(x => x.Highway is "motorway" or "motorway_link" or "trunk" or "trunk_link");

    private static Vector2 P(double[] p) => new((float)p[0], (float)p[2]);

    public static void Apply(List<RoadEntry> roads, Func<RunEntry, (float HalfWidth, float Center)> style,
                             List<(Vector3[] Nodes, float HalfWidth)> prefabs, List<string> warnings)
    {
        Moved = 0;
        if (!Enabled) return;
        // Hindernisse: Segmente großer Straßen (Mitte des Modells) und Prefab-Sterne
        var obs = new List<(Vector2 A, Vector2 B, float Hw, float Y, RoadEntry? R)>();
        foreach (var r in roads.Where(IsMajor))
            for (int i = 0; i < r.Points.Count - 1; i++)
            {
                var run = r.Runs.FirstOrDefault(x => x.From <= i && i < x.To) ?? r.Runs[^1];
                var (hw, c) = style(run);
                var a = P(r.Points[i]); var b = P(r.Points[i + 1]);
                var d = b - a; if (d.LengthSquared() < 1e-6f) continue;
                var sh = new Vector2(-d.Y, d.X) / d.Length() * c;
                obs.Add((a + sh, b + sh, hw, (float)(r.Points[i][1] + r.Points[i + 1][1]) / 2, r));
            }
        foreach (var (nodes, hw) in prefabs)
        {
            var c = nodes.Aggregate(Vector3.Zero, (s, v) => s + v) / nodes.Length;
            foreach (var n in nodes) obs.Add((new Vector2(n.X, n.Z), new Vector2(c.X, c.Z), hw, (n.Y + c.Y) / 2, null));
        }
        // Gitter
        const float cell = 40f;
        var grid = new Dictionary<(int, int), List<int>>();
        for (int k = 0; k < obs.Count; k++)
        {
            var o = obs[k]; float pad = o.Hw + 15;
            for (int x = (int)MathF.Floor((MathF.Min(o.A.X, o.B.X) - pad) / cell); x <= (int)MathF.Floor((MathF.Max(o.A.X, o.B.X) + pad) / cell); x++)
                for (int z = (int)MathF.Floor((MathF.Min(o.A.Y, o.B.Y) - pad) / cell); z <= (int)MathF.Floor((MathF.Max(o.A.Y, o.B.Y) + pad) / cell); z++)
                    (grid.TryGetValue((x, z), out var l) ? l : grid[(x, z)] = []).Add(k);
        }
        var nodeOf = (RoadEntry r) => new[] { r.Start, r.End }.Where(e => e?.Kind == "node" && e.OsmNode is long).Select(e => e!.OsmNode!.Value).ToHashSet();
        float maxShift = 0;
        foreach (var r in roads)
        {
            if (IsMajor(r) || r.Points.Count < 2) continue;
            var mine = nodeOf(r);
            int n = r.Points.Count;
            var push = new Vector2[n];
            bool any = false;
            // Seite je großer Straße einheitlich wählen (Mehrheit der nahen Punkte), sonst würden
            // einzelne Punkte auf die andere Seite gedrückt und der Weg kreuzte die Straße
            var sideSum = new Dictionary<object, float>();
            for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < n; i++)
            {
                var p = P(r.Points[i]);
                var run = r.Runs.FirstOrDefault(x => x.From <= Math.Min(i, n - 2) && Math.Min(i, n - 2) < x.To) ?? r.Runs[^1];
                var (hwMe, _) = style(run);
                var key = ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell));
                if (!grid.TryGetValue(key, out var cand)) continue;
                var tg = P(r.Points[Math.Min(i + 1, n - 1)]) - P(r.Points[Math.Max(i - 1, 0)]);
                if (tg.LengthSquared() < 1e-6f) continue;
                tg = Vector2.Normalize(tg);
                Vector2 best = Vector2.Zero;
                foreach (var k in cand)
                {
                    var o = obs[k];
                    if (o.R is not null && nodeOf(o.R).Overlaps(mine)) continue;     // angeschlossen
                    float dz = MathF.Abs(o.Y - (float)r.Points[i][1]);
                    if (dz > MaxDz) continue;
                    var ab = o.B - o.A; float l2 = ab.LengthSquared(); if (l2 < 1e-6f) continue;
                    // nur nebeneinander laufende Straßen; Kreuzungen (Unter-/Überführung, in dieser
                    // Stufe weggelassene Querung) nicht verbiegen
                    if (o.R is not null && MathF.Abs(Vector2.Dot(tg, ab / MathF.Sqrt(l2))) < MinParallel) continue;
                    float u = Math.Clamp(Vector2.Dot(p - o.A, ab) / l2, 0, 1);
                    var q = o.A + ab * u; var dv = p - q; float d = dv.Length();
                    // bei Höhenunterschied Platz für eine Böschung (1 : Slope) lassen, sonst entsteht eine Wand
                    float need = o.Hw + hwMe + Gap + Slope * dz - d;
                    if (need <= 0 || d < 1e-3f) continue;
                    if (o.R is null && Vector2.Distance(p, o.A) < 1f) continue;
                    object id = (object?)o.R ?? k;
                    var nrm = new Vector2(-ab.Y, ab.X) / MathF.Sqrt(l2);
                    float lat = Vector2.Dot(dv, nrm);
                    if (pass == 0) { sideSum[id] = (sideSum.TryGetValue(id, out var ss) ? ss : 0) + MathF.Sign(lat); continue; }
                    float side = sideSum.TryGetValue(id, out var sv) && sv != 0 ? MathF.Sign(sv) : MathF.Sign(lat);
                    // Abstand auf der gewählten Seite: liegt der Punkt drüben, muss er ganz herüber
                    float needSide = o.Hw + hwMe + Gap + Slope * dz - side * lat;
                    if (needSide <= 0) continue;
                    var v = nrm * side * needSide;
                    if (v.Length() > best.Length()) best = v;
                }
                if (pass == 0) continue;
                if (best.Length() > MaxPush) best = Vector2.Zero;
                push[i] = best; any |= best != Vector2.Zero;
            }
            if (!any) continue;
            // Hüllkurve über die Bogenlänge; an angeschlossenen Enden auf 0 auslaufen
            var s = new float[n];
            for (int i = 1; i < n; i++) s[i] = s[i - 1] + Vector2.Distance(P(r.Points[i - 1]), P(r.Points[i]));
            var sm = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 v = Vector2.Zero;
                for (int j = 0; j < n; j++)
                {
                    if (push[j] == Vector2.Zero) continue;
                    float f = 1 - MathF.Abs(s[i] - s[j]) / Taper;
                    if (f <= 0) continue;
                    var c = push[j] * f;
                    if (c.Length() > v.Length()) v = c;
                }
                float fixA = r.Start?.Kind == "node" ? Math.Clamp(s[i] / Taper, 0, 1) : 1;
                float fixB = r.End?.Kind == "node" ? Math.Clamp((s[^1] - s[i]) / Taper, 0, 1) : 1;
                sm[i] = v * MathF.Min(fixA, fixB);
            }
            for (int i = 0; i < n; i++)
            {
                r.Points[i][0] += sm[i].X; r.Points[i][2] += sm[i].Y;
                maxShift = MathF.Max(maxShift, sm[i].Length());
            }
            Moved++;
        }
        if (Moved > 0) warnings.Add($"Info: {Moved} Nebenstraßen von Autobahnen/Prefabs weggeschoben, höchstens {maxShift:0.0} m");
    }
}
