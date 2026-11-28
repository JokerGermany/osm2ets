// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using TruckLib;
using TruckLib.ScsMap;
using DColor = System.Drawing.Color;

namespace Osm2Ets.Writer;

/// <summary>
/// Wasserflächen aus OSM (Zwischenformat: water – Teiche, Becken, Seen, Schwimm- und Kneippbecken, Flussflächen).
/// <list type="bullet">
/// <item>Wasserspiegel je Fläche: <see cref="LevelBelow"/> unter dem tiefsten Punkt des Ufers (Geländeraster samt
/// Prefab-Böschungen, Straßen am Ufer zählen mit ihrer Fahrbahnhöhe); bei Flussflächen je Ort das Minimum des Ufers
/// im Umkreis von <see cref="RiverReach"/>.</item>
/// <item>Becken: Nachbearbeitung jeder Rasterhöhe (<see cref="Post"/>): innen vom Ufer mit der Böschungsneigung der
/// Art bis auf die Tiefe abgesenkt; außen bis zum Überstand der Wasserstreifen mindestens Ufer-Höhe, darüber
/// hinausragendes Gelände sanft zum Ufer geneigt (<see cref="SmoothSlope"/>).</item>
/// <item>Wasser: ebene Terrain-Streifen in Ost-West-Richtung (4 m breit, neben anderem Wasser 1 m), je Streifen
/// so lang, wie die Fläche im Streifen reicht; Material durchsichtiges Wasser, Farbe nach terrain_color.water.sii
/// (Rot = Palette, Grün = Sichttiefe in m, Blau = Schaum in cm), ohne Kollision und Detailvegetation.</item>
/// <item>Damit das Ufer der Umrisslinie folgt: feine Füllreihen um die Fläche (<see cref="TerrainFill.ExtraZoneRects"/>),
/// Straßengelände endet vor dem Wasser (<see cref="ClampRoadTerrain"/>; seine Reihen werden nach außen grob),
/// Grund mit eigenem Material und ohne Detailvegetation (<see cref="PaintBeds"/>).</item>
/// </list>
/// Annahmen (im Spiel ungeprüft): SCS baut Wasser so als ebenes Gelände mit Wassermaterial; Farbwerte und Tiefen.
/// Aus: OSM2ETS_NO_WATER=1.
/// </summary>
public static class Water
{
    public static bool Enabled = Environment.GetEnvironmentVariable("OSM2ETS_NO_WATER") != "1";

    /// <summary>Darstellung einer Art: Wassermaterial, Wasserfarbe, Material des Grunds, Tiefe (m), Neigung der
    /// Böschung unter Wasser (Höhe je Meter vom Ufer).</summary>
    public sealed record Style(string Material, DColor Color, string Bed, float Depth, float Bank);

    /// <summary>Farbe wie in terrain_color.water.sii; Alpha 0 wie die Vorgabefarbe in Editor-Karten.</summary>
    static DColor C(int r, int g, int b) => DColor.FromArgb(0, r, g, b);

    /// <summary>Materialien und Farben nach Namen gewählt (terrain_material.water.sii, terrain_material.sii,
    /// terrain_color.water.sii des Grundspiels).</summary>
    public static Style StyleOf(string kind, float area) => kind switch
    {
        // transparent water calm 01, blue_01 05m, artificial tiles 01
        "pool" => new("wtr_tran_15", C(8, 5, 0), "share_27", 1.6f, 2f),
        // clear_01 05m, pebble stones
        "kneipp" => new("wtr_tran_15", C(0, 5, 0), "29", 0.5f, 2f),
        // brown_01 01m, muddy ground
        "wastewater" => new("wtr_tran_15", C(16, 1, 0), "20", 1.5f, 0.8f),
        // transparent water small waves 01 (ohne Strömung: die Streifen laufen nicht mit dem Fluss), green_02 01m,
        // riverbed wet 01 sandy soil 01
        "river" => new("wtr_tran_20", C(25, 2, 0), "share_263", NaturalDepth(area), 0.35f),
        // green_01 05m
        "lake" or "reservoir" or "lock" => new("wtr_tran_15", C(24, 5, 0), "20", NaturalDepth(area) + 1, 0.35f),
        // Teich, Becken, Wasser ohne Angabe: green_02 01m, muddy ground
        _ => new("wtr_tran_15", C(25, 2, 0), "20", NaturalDepth(area), 0.35f),
    };

    static float NaturalDepth(float area) => Math.Clamp(1.2f + MathF.Sqrt(MathF.Max(area, 0)) / 100f, 1.2f, 4f);

    /// <summary>Wasserspiegel so weit unter dem tiefsten Uferpunkt (m).</summary>
    public const float LevelBelow = 0.15f;
    /// <summary>Gelände am Ufer so weit über dem Wasserspiegel – Füllgelände liegt noch <see cref="TerrainFill.FillDrop"/> tiefer.</summary>
    public const float ShoreAbove = 0.35f;
    /// <summary>Außen: Gelände über dem Ufer mit dieser Neigung zum Ufer hin gesenkt, ausklingend bis SmoothReach.</summary>
    public const float SmoothSlope = 0.5f, SmoothReach = 8f;
    /// <summary>Außen: Anhebung auf Ufer-Höhe klingt nach dem Überstand der Streifen über so viele Meter aus.</summary>
    public const float RimFade = 3f;
    /// <summary>Halbe Breite der Wasserstreifen; neben anderem Wasser (näher als NeighbourGap) schmal.</summary>
    public const float Wide = 2f, Narrow = 0.5f, NeighbourGap = 6f;
    /// <summary>Feine Füllreihen im Rechteck um die Fläche, so weit über sie hinaus (dazu TerrainFill.ZoneMargin).</summary>
    public const float ZoneReach = 3f;
    /// <summary>Straßengelände endet so weit vor dem Ufer, aber nicht schmaler als RoadMin (dort 1-m-Reihen).</summary>
    public const float RoadKeep = 7f, RoadMin = 3f;
    /// <summary>Straßen bis Fahrbahnrand + so viel neben dem Ufer begrenzen den Wasserspiegel.</summary>
    public const float RoadShore = 12f;
    public const float RiverReach = 150f;
    /// <summary>Becken schmaler als so viele Meter bekommen das feine 2-m-Raster.</summary>
    public const float FineBelow = 15f;
    public const float MaxPiece = 200f, RiverPiece = 50f;
    /// <summary>Abstandsraster je Fläche: Zellgröße, Rand um den Umriss; Eimer für die Suche.</summary>
    const float Cell = 1f, Margin = 16f, Bucket = 64f;

    public sealed class Body
    {
        public required string Osm { get; init; }
        public required string Kind { get; init; }
        /// <summary>[0] Außenring, danach Inseln.</summary>
        public required Vector2[][] Rings { get; init; }
        /// <summary>Segment i (Punkt i → i+1) eines Rings grenzt an anderes Wasser (kein Ufer).</summary>
        public bool[][] Internal = [];
        public Vector2 Min, Max;
        public float Area;
        public Style Style = null!;
        public float Half = Wide;
        public float Level = float.NaN;
        public int Strips;
        float x0, z0; int w, h; float[] dist = [];
        float lx0, lz0; int lw, lh; float[]? lgrid;
        const float LCell = 10f;

        public bool Valid => !float.IsNaN(Level);

        public bool Contains(Vector2 p)
        {
            if (p.X < Min.X || p.Y < Min.Y || p.X > Max.X || p.Y > Max.Y || !InRing(Rings[0], p)) return false;
            for (int r = 1; r < Rings.Length; r++) if (InRing(Rings[r], p)) return false;
            return true;
        }

        /// <summary>Wasserspiegel an (x, z): Flussflächen je Ort, sonst eben.</summary>
        public float LevelAt(float x, float z)
        {
            if (lgrid is null) return Level;
            int i = Math.Clamp((int)MathF.Round((x - lx0) / LCell), 0, lw - 1), j = Math.Clamp((int)MathF.Round((z - lz0) / LCell), 0, lh - 1);
            return lgrid[j * lw + i];
        }

        /// <summary>Vorzeichenbehafteter Abstand zum Ufer (innen positiv), bilinear aus dem 1-m-Raster;
        /// außerhalb des Rasters −∞.</summary>
        public float SignedDistance(float x, float z)
        {
            float fx = (x - x0) / Cell, fz = (z - z0) / Cell;
            int i = (int)MathF.Floor(fx), j = (int)MathF.Floor(fz);
            if (i < 0 || j < 0 || i >= w - 1 || j >= h - 1) return float.NegativeInfinity;
            float tx = fx - i, tz = fz - j;
            int k = j * w + i;
            return (dist[k] * (1 - tx) + dist[k + 1] * tx) * (1 - tz) + (dist[k + w] * (1 - tx) + dist[k + w + 1] * tx) * tz;
        }

        public IEnumerable<(Vector2 A, Vector2 B, bool Internal)> Segments()
        {
            for (int r = 0; r < Rings.Length; r++)
                for (int i = 0; i < Rings[r].Length; i++)
                    yield return (Rings[r][i], Rings[r][(i + 1) % Rings[r].Length], Internal.Length > r && Internal[r][i]);
        }

        internal void BuildRaster()
        {
            x0 = MathF.Floor(Min.X - Margin); z0 = MathF.Floor(Min.Y - Margin);
            w = (int)MathF.Ceiling((Max.X + Margin - x0) / Cell) + 1;
            h = (int)MathF.Ceiling((Max.Y + Margin - z0) / Cell) + 1;
            dist = new float[w * h];
            var shore = Segments().Where(s => !s.Internal).Select(s => (s.A, s.B)).ToList();
            if (shore.Count == 0) shore = Segments().Select(s => (s.A, s.B)).ToList();
            var xs = new List<float>();
            var near = new List<(Vector2 A, Vector2 B)>();
            for (int j = 0; j < h; j++)
            {
                float z = z0 + j * Cell;
                xs.Clear();
                foreach (var ring in Rings)
                    for (int i = 0, k = ring.Length - 1; i < ring.Length; k = i++)
                        if ((ring[i].Y > z) != (ring[k].Y > z))
                            xs.Add(ring[i].X + (z - ring[i].Y) / (ring[k].Y - ring[i].Y) * (ring[k].X - ring[i].X));
                xs.Sort();
                near.Clear();
                foreach (var s in shore)
                    if (MathF.Min(s.A.Y, s.B.Y) - Margin - 1 <= z && MathF.Max(s.A.Y, s.B.Y) + Margin + 1 >= z) near.Add(s);
                int c = 0; bool inside = false;
                for (int i = 0; i < w; i++)
                {
                    float x = x0 + i * Cell;
                    while (c < xs.Count && xs[c] <= x) { inside = !inside; c++; }
                    float best = Margin + 1;
                    var p = new Vector2(x, z);
                    foreach (var (a, b) in near)
                    {
                        if (x < MathF.Min(a.X, b.X) - best || x > MathF.Max(a.X, b.X) + best) continue;
                        float d = SegDist(p, a, b);
                        if (d < best) best = d;
                    }
                    dist[j * w + i] = inside ? best : -best;
                }
            }
        }

        /// <summary>Wasserspiegel aus Uferpunkten (Ort, Höhe).</summary>
        internal void SetLevel(List<(Vector2 P, float H)> samples)
        {
            if (samples.Count < 3) return;
            Level = samples.Min(s => s.H) - LevelBelow;
            if (Kind != "river") return;
            lx0 = Min.X - LCell; lz0 = Min.Y - LCell;
            lw = (int)MathF.Ceiling((Max.X + LCell - lx0) / LCell) + 1;
            lh = (int)MathF.Ceiling((Max.Y + LCell - lz0) / LCell) + 1;
            lgrid = new float[lw * lh];
            for (int j = 0; j < lh; j++)
                for (int i = 0; i < lw; i++)
                {
                    var p = new Vector2(lx0 + i * LCell, lz0 + j * LCell);
                    float m = float.PositiveInfinity;
                    foreach (var s in samples)
                        if (Vector2.DistanceSquared(s.P, p) <= RiverReach * RiverReach && s.H < m) m = s.H;
                    lgrid[j * lw + i] = float.IsPositiveInfinity(m) ? Level : m - LevelBelow;
                }
        }
    }

    static bool InRing(Vector2[] r, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = r.Length - 1; i < r.Length; j = i++)
            if ((r[i].Y > p.Y) != (r[j].Y > p.Y) && p.X < (r[j].X - r[i].X) * (p.Y - r[i].Y) / (r[j].Y - r[i].Y) + r[i].X)
                inside = !inside;
        return inside;
    }

    static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / MathF.Max(ab.LengthSquared(), 1e-9f), 0, 1);
        return Vector2.Distance(p, a + t * ab);
    }

    static float Signed(Vector2[] r)
    {
        float s = 0;
        for (int i = 0, j = r.Length - 1; i < r.Length; j = i++) s += (r[j].X - r[i].X) * (r[j].Y + r[i].Y);
        return s / 2;
    }

    public sealed record Result(int Bodies, int Skipped, int Strips, int NarrowBodies, double AreaM2, Dictionary<string, int> ByKind);

    public sealed class Index
    {
        public readonly List<Body> Bodies = [];
        readonly Dictionary<(int, int), List<Body>> cells = [];

        public Index(IEnumerable<WaterEntry> entries)
        {
            foreach (var e in entries)
            {
                if (e.Outer.Count < 3) continue;
                var outer = Ring(e.Outer);
                if (outer.Length < 3) continue;
                var rings = new List<Vector2[]> { outer };
                rings.AddRange(e.Inner.Select(Ring).Where(r => r.Length >= 3));
                float area = MathF.Abs(Signed(outer)) - rings.Skip(1).Sum(r => MathF.Abs(Signed(r)));
                if (area < 1) continue;
                var b = new Body
                {
                    Osm = e.Osm, Kind = e.Kind, Rings = [.. rings], Area = area, Style = StyleOf(e.Kind, area),
                    Min = new Vector2(outer.Min(p => p.X), outer.Min(p => p.Y)), Max = new Vector2(outer.Max(p => p.X), outer.Max(p => p.Y)),
                };
                Bodies.Add(b);
                for (int i = Key(b.Min.X - Margin); i <= Key(b.Max.X + Margin); i++)
                    for (int j = Key(b.Min.Y - Margin); j <= Key(b.Max.Y + Margin); j++)
                    {
                        if (!cells.TryGetValue((i, j), out var l)) cells[(i, j)] = l = [];
                        l.Add(b);
                    }
            }
            // Segmente an anderem Wasser (aneinanderstoßende Flächen) sind kein Ufer: dort keine Böschung
            foreach (var b in Bodies)
            {
                b.Internal = b.Rings.Select(r => new bool[r.Length]).ToArray();
                for (int r = 0; r < b.Rings.Length; r++)
                    for (int i = 0; i < b.Rings[r].Length; i++)
                    {
                        var a = b.Rings[r][i]; var c = b.Rings[r][(i + 1) % b.Rings[r].Length];
                        var d = c - a;
                        if (d.LengthSquared() < 1e-4f) continue;
                        var n = Vector2.Normalize(new Vector2(-d.Y, d.X)) * 0.75f;
                        var m = (a + c) / 2;
                        bool in1 = b.Contains(m + n), in2 = b.Contains(m - n);
                        if (in1 == in2) continue;
                        var outside = in1 ? m - n : m + n;
                        b.Internal[r][i] = Candidates(outside).Any(o => o != b && o.Contains(outside));
                    }
            }
            foreach (var b in Bodies) b.BuildRaster();
            // neben anderem Wasser schmale Streifen: sonst ragen sie über den Damm ins Nachbarbecken
            foreach (var b in Bodies)
                foreach (var (a, c, _) in b.Segments())
                {
                    int n = Math.Max(1, (int)(Vector2.Distance(a, c) / 2));
                    for (int k = 0; k <= n && b.Half == Wide; k++)
                    {
                        var p = Vector2.Lerp(a, c, k / (float)n);
                        if (Candidates(p).Any(o => o != b && o.SignedDistance(p.X, p.Y) > -NeighbourGap)) b.Half = Narrow;
                    }
                }
        }

        static Vector2[] Ring(List<double[]> pts)
        {
            var r = pts.Select(p => new Vector2((float)p[0], (float)p[1])).ToList();
            if (r.Count > 1 && Vector2.DistanceSquared(r[0], r[^1]) < 1e-6f) r.RemoveAt(r.Count - 1);
            return [.. r];
        }

        static int Key(float v) => (int)MathF.Floor(v / Bucket);

        IEnumerable<Body> Candidates(Vector2 p) =>
            cells.TryGetValue((Key(p.X), Key(p.Y)), out var l) ? l : [];

        /// <summary>Fläche, in der p am tiefsten innen liegt (höchster Abstand), samt Abstand; sonst die nächste.</summary>
        public (Body? Body, float Dist) At(float x, float z)
        {
            Body? best = null; float bd = float.NegativeInfinity;
            if (!cells.TryGetValue((Key(x), Key(z)), out var l)) return (null, bd);
            foreach (var b in l)
            {
                float d = b.SignedDistance(x, z);
                if (d > bd) { bd = d; best = b; }
            }
            return (best, bd);
        }

        /// <summary>Abstand zum nächsten Wasser (innen positiv), für die Bäume.</summary>
        public float Distance(Vector2 p) => At(p.X, p.Y).Dist;

        /// <summary>Wasserspiegel aller Flächen aus den Rasterhöhen am Ufer (alle 2 m) und den Fahrbahnhöhen von
        /// Straßen (ohne Brücken und Tunnel), die bis Fahrbahnrand + <see cref="RoadShore"/> ans Ufer reichen.</summary>
        public void ComputeLevels(Func<float, float, float> height, List<RoadEntry> roads, Dictionary<RoadEntry, ResolvedStyle[]> styles)
        {
            const float RCell = 32f;
            var rp = new Dictionary<(int, int), List<(Vector2 P, float Y, float Reach)>>();
            foreach (var r in roads)
            {
                if (r.Role == "rail" || !styles.TryGetValue(r, out var st) || st.Length == 0) continue;
                for (int k = 0; k + 1 < r.Points.Count; k++)
                {
                    var run = r.Runs.FirstOrDefault(x => x.From <= k && k < x.To);
                    if (run is { Bridge: true } || run is { Tunnel: true }) continue;
                    var a = new Vector3((float)r.Points[k][0], (float)r.Points[k][1], (float)r.Points[k][2]);
                    var b = new Vector3((float)r.Points[k + 1][0], (float)r.Points[k + 1][1], (float)r.Points[k + 1][2]);
                    float reach = st[Math.Min(k, st.Length - 1)].Width / 2 + RoadShore;
                    int n = Math.Max(1, (int)(Vector3.Distance(a, b) / 4));
                    for (int s = 0; s <= n; s++)
                    {
                        var q = Vector3.Lerp(a, b, s / (float)n);
                        var key = ((int)MathF.Floor(q.X / RCell), (int)MathF.Floor(q.Z / RCell));
                        if (!rp.TryGetValue(key, out var l)) rp[key] = l = [];
                        l.Add((new Vector2(q.X, q.Z), q.Y, reach));
                    }
                }
            }
            foreach (var b in Bodies)
            {
                var samples = new List<(Vector2 P, float H)>();
                foreach (var (a, c, _) in b.Segments())
                {
                    int n = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(a, c) / 2));
                    for (int k = 0; k < n; k++)
                    {
                        var p = Vector2.Lerp(a, c, k / (float)n);
                        float hh = height(p.X, p.Y);
                        if (float.IsNaN(hh)) continue;
                        int gi = (int)MathF.Floor(p.X / RCell), gj = (int)MathF.Floor(p.Y / RCell);
                        for (int i = gi - 1; i <= gi + 1; i++)
                            for (int j = gj - 1; j <= gj + 1; j++)
                                if (rp.TryGetValue((i, j), out var l))
                                    foreach (var (q, y, reach) in l)
                                        if (y - 0.5f < hh && Vector2.DistanceSquared(p, q) < reach * reach) hh = y - 0.5f;
                        samples.Add((p, hh));
                    }
                }
                b.SetLevel(samples);
            }
        }

        /// <summary>Rasterhöhe h an (x, z) nach den Becken: innen abgesenkt, außen Ufer-Höhe und Neigung zum Ufer.</summary>
        public float Post(float x, float z, float h)
        {
            if (!cells.TryGetValue((Key(x), Key(z)), out var l)) return h;
            Body? inside = null; float din = 0;
            foreach (var b in l)
            {
                if (!b.Valid) continue;
                float d = b.SignedDistance(x, z);
                if (d > din) { din = d; inside = b; }
            }
            if (inside is not null)
            {
                float lv = inside.LevelAt(x, z);
                float bed = MathF.Max(lv + ShoreAbove - inside.Style.Bank * din, lv - inside.Style.Depth);
                return MathF.Min(h, bed);
            }
            float raise = 0, floor = float.NegativeInfinity;
            foreach (var b in l)
            {
                if (!b.Valid) continue;
                float a = -b.SignedDistance(x, z);
                if (float.IsPositiveInfinity(a)) continue;
                float shore = b.LevelAt(x, z) + ShoreAbove;
                float full = 2 * b.Half + 0.5f;            // Überstand der Wasserstreifen über den Umriss
                if (a <= full) floor = MathF.Max(floor, shore);
                float wr = a <= full ? 1 : MathF.Max(0, 1 - (a - full) / RimFade);
                if (wr > 0 && shore > h) raise = MathF.Max(raise, wr * (shore - h));
            }
            float h1 = h + raise, cut = 0;
            foreach (var b in l)
            {
                if (!b.Valid) continue;
                float a = -b.SignedDistance(x, z);
                if (a >= SmoothReach) continue;
                float cap = b.LevelAt(x, z) + ShoreAbove + SmoothSlope * a;
                if (h1 > cap) cut = MathF.Max(cut, (1 - a / SmoothReach) * (h1 - cap));
            }
            return MathF.Max(h1 - cut, floor);
        }

        /// <summary>Rechtecke um die Flächen mit Wasserspiegel (+ <see cref="ZoneReach"/>) – für die feinen Füllreihen;
        /// Fine: 2-m- statt 4-m-Raster längs (neben anderem Wasser: Dämme von 2–5 m; schmale Becken unter
        /// <see cref="FineBelow"/> m; Schwimm- und Kneippbecken mit steilen Wänden).</summary>
        public List<(Vector2 Min, Vector2 Max, bool Fine)> ZoneRects() =>
            Bodies.Where(b => b.Valid).Select(b => (b.Min - new Vector2(ZoneReach), b.Max + new Vector2(ZoneReach),
                b.Half < Wide || b.Kind is "pool" or "kneipp" || MathF.Min(b.Max.X - b.Min.X, b.Max.Y - b.Min.Y) < FineBelow)).ToList();

        /// <summary>Straßengelände endet <see cref="RoadKeep"/> vor dem Ufer (nicht schmaler als <see cref="RoadMin"/>):
        /// seine Reihen werden nach außen 6–10 m breit, seine Spalten sind 5–15 m lang – zu grob für ein Ufer. Dort
        /// übernimmt das Füllgelände mit feinen Reihen. Rückgabe: Anzahl gekürzter Seiten.</summary>
        public int ClampRoadTerrain(List<RoadEntry> roads, Dictionary<RoadEntry, ResolvedStyle[]> styles)
        {
            int n = 0;
            foreach (var r in roads)
            {
                if (!styles.TryGetValue(r, out var st)) continue;
                for (int k = 0; k + 1 < r.Points.Count && k < st.Length; k++)
                {
                    var s = st[k];
                    if (s.TerrainLeft <= RoadMin && s.TerrainRight <= RoadMin) continue;
                    var a = new Vector2((float)r.Points[k][0], (float)r.Points[k][2]);
                    var b = new Vector2((float)r.Points[k + 1][0], (float)r.Points[k + 1][2]);
                    if (Vector2.DistanceSquared(a, b) < 1e-4f) continue;
                    var dir = Vector2.Normalize(b - a);
                    var right = new Vector2(-dir.Y, dir.X);
                    var (el, er) = TerrainClearance.Edges(s);
                    float left = s.TerrainLeft, rgt = s.TerrainRight;
                    foreach (var t in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
                    {
                        var p = Vector2.Lerp(a, b, t);
                        if (rgt > RoadMin) rgt = MathF.Min(rgt, Free(p, right, er, rgt));
                        if (left > RoadMin) left = MathF.Min(left, Free(p, -right, el, left));
                    }
                    left = left < s.TerrainLeft ? MathF.Max(left, RoadMin) : left;
                    rgt = rgt < s.TerrainRight ? MathF.Max(rgt, RoadMin) : rgt;
                    if (left < s.TerrainLeft) n++;
                    if (rgt < s.TerrainRight) n++;
                    if (left < s.TerrainLeft || rgt < s.TerrainRight) st[k] = s with { TerrainLeft = left, TerrainRight = rgt };
                }
            }
            return n;
        }

        /// <summary>Breite ab dem Fahrbahnrand, bis zu der das Gelände RoadKeep vom Wasser entfernt bleibt.</summary>
        float Free(Vector2 p, Vector2 n, float edge, float size)
        {
            for (float s = 0; s <= size; s += 1)
            {
                var q = p + n * (edge + s);
                if (At(q.X, q.Y).Dist > -RoadKeep) return MathF.Max(0, s - 1);
            }
            return size;
        }

        /// <summary>Wasserstreifen setzen (nach dem Füllgelände). Nur Flächen mit Wasserspiegel und nur, wo das
        /// Geländeraster reicht.</summary>
        public Result Emit(Map map, TerrainGridEntry grid)
        {
            float gx0 = (float)grid.Origin[0] + 10, gz0 = (float)grid.Origin[1] + 10;
            float gx1 = (float)(grid.Origin[0] + grid.Cell * (grid.Nx - 1)) - 10, gz1 = (float)(grid.Origin[1] + grid.Cell * (grid.Nz - 1)) - 10;
            int strips = 0, bodies = 0, skipped = 0, narrow = 0; double area = 0;
            var byKind = new Dictionary<string, int>();
            bool dbg = Environment.GetEnvironmentVariable("OSM2ETS_DEBUG") is not null;
            foreach (var b in Bodies)
            {
                if (!b.Valid) { skipped++; continue; }
                var mat = new Token(b.Style.Material);
                float hh = b.Half;
                int before = strips;
                for (float zl = b.Min.Y - 0.01f; zl < b.Max.Y; zl += 2 * hh)
                {
                    float zc = zl + hh;
                    if (zc < gz0 || zc > gz1) continue;
                    foreach (var (xa0, xb0) in Projection(b.Rings, zl, zl + 2 * hh))
                    {
                        float xa = MathF.Max(xa0 - 0.25f, gx0), xb = MathF.Min(xb0 + 0.25f, gx1);
                        if (xb - xa < 0.5f) continue;
                        float maxLen = b.Kind == "river" ? RiverPiece : MaxPiece;
                        int pieces = Math.Max(1, (int)MathF.Ceiling((xb - xa) / maxLen));
                        for (int k = 0; k < pieces; k++)
                        {
                            float xp = xa + (xb - xa) * k / pieces, xq = xa + (xb - xa) * (k + 1) / pieces;
                            var pa = new Vector3(xp, b.LevelAt(xp, zc), zc);
                            var pb = new Vector3(xq, b.LevelAt(xq, zc), zc);
                            var t = Terrain.Add(map, pa, pb, mat, hh, hh);
                            t.Boundary = false;
                            t.IgnoreCutPlanes = true;
                            t.Collision = false;
                            t.ViewDistance = 1000;
                            t.StepSize = StepSize.Meters16;
                            foreach (var side in new[] { t.Left, t.Right })
                            {
                                side.DetailVegetation = false;
                                side.Terrain.Noise = TerrainNoise.Percent0;
                            }
                            t.Recalculate();
                            foreach (var side in new[] { t.Left, t.Right })
                                side.Terrain.QuadData.BrushColors[0] = b.Style.Color;
                            map.AddSector(Map.GetSectorOfCoordinate((pa + pb) / 2));
                            strips++;
                            area += (xq - xp) * 2 * hh;
                        }
                    }
                }
                b.Strips = strips - before;
                if (b.Strips == 0) { skipped++; continue; }
                bodies++;
                if (hh < Wide) narrow++;
                byKind[b.Kind] = byKind.GetValueOrDefault(b.Kind) + 1;
                if (dbg) Console.Error.WriteLine($"WATER {b.Osm} {b.Kind} {b.Area:0} m² Spiegel {b.Level:0.00} Tiefe {b.Style.Depth:0.0} Streifen {b.Strips} ({2 * hh:0} m) {b.Min.X:0},{b.Min.Y:0}");
            }
            return new Result(bodies, skipped, strips, narrow, area, byKind);
        }

        /// <summary>Grund bemalen: Quads der Straßen- und Füllgelände mit der Mitte im Wasser bekommen das Material
        /// des Grunds, bis 1,5 m vom Ufer keine Detailvegetation. Vor der Bodenbedeckung (die bemalte Quads
        /// auslässt). Rückgabe: (Quads mit Grund, Quads ohne Detailvegetation).</summary>
        public (int Bed, int NoGrass) PaintBeds(Map map)
        {
            int bed = 0, noGrass = 0;
            foreach (var it in map.MapItems.Values)
            {
                switch (it)
                {
                    case Road r:
                        Paint(r.Left.Terrain, r.Node.Position, ref bed, ref noGrass);
                        Paint(r.Right.Terrain, r.Node.Position, ref bed, ref noGrass);
                        break;
                    case Terrain t:
                        Paint(t.Left.Terrain, t.Node.Position, ref bed, ref noGrass);
                        Paint(t.Right.Terrain, t.Node.Position, ref bed, ref noGrass);
                        break;
                }
            }
            return (bed, noGrass);
        }

        void Paint(RoadTerrain side, Vector3 origin, ref int bed, ref int noGrass)
        {
            var q = side.QuadData;
            if (side.Size <= 0 || q.Offsets.Count == 0 || q.Quads.Count != q.Cols * q.Rows) return;
            // grob: liegt die Seite überhaupt in einem Eimer mit Wasser?
            bool any = false;
            foreach (var o in q.Offsets)
            {
                var p = origin + o.Data;
                if (cells.ContainsKey((Key(p.X), Key(p.Z)))) { any = true; break; }
            }
            if (!any) return;
            var at = new Dictionary<(int, int), Vector3>(q.Offsets.Count);
            foreach (var o in q.Offsets) at[(o.X, o.Y)] = origin + o.Data;
            for (int i = 0; i < q.Cols; i++)
                for (int j = 0; j < q.Rows; j++)
                {
                    if (!at.TryGetValue((i, j), out var p00) || !at.TryGetValue((i + 1, j), out var p10) ||
                        !at.TryGetValue((i, j + 1), out var p01) || !at.TryGetValue((i + 1, j + 1), out var p11)) continue;
                    var c = (p00 + p10 + p01 + p11) / 4;
                    var (b, d) = At(c.X, c.Z);
                    if (b is null || !b.Valid || d < -1.5f) continue;
                    int k = i * q.Rows + j;
                    var quad = q.Quads[k];
                    if (!quad.NoDetailVegetation) { quad.NoDetailVegetation = true; noGrass++; }
                    if (d > 0 && quad.MainMaterial == 0)
                    {
                        var tok = new Token(b.Style.Bed);
                        int idx = q.BrushMaterials.FindIndex(m => m.Name == tok);
                        if (idx < 0 && q.BrushMaterials.Count < 16) { q.BrushMaterials.Add(new Material(tok)); idx = q.BrushMaterials.Count - 1; }
                        if (idx > 0) { quad.MainMaterial = (Nibble)idx; bed++; }
                    }
                    q.Quads[k] = quad;
                }
        }
    }

    /// <summary>x-Intervalle, in denen die Fläche (Außenring ohne Inseln) im Band zl ≤ z ≤ zh liegt: Schnitt mit
    /// den beiden Bandrändern plus die x-Bereiche aller Randsegmente im Band (genau die Projektion).</summary>
    internal static List<(float A, float B)> Projection(Vector2[][] rings, float zl, float zh)
    {
        var iv = new List<(float A, float B)>();
        foreach (float z in new[] { zl, zh })
        {
            var xs = new List<float>();
            foreach (var ring in rings)
                for (int i = 0, k = ring.Length - 1; i < ring.Length; k = i++)
                    if ((ring[i].Y > z) != (ring[k].Y > z))
                        xs.Add(ring[i].X + (z - ring[i].Y) / (ring[k].Y - ring[i].Y) * (ring[k].X - ring[i].X));
            xs.Sort();
            for (int i = 0; i + 1 < xs.Count; i += 2) iv.Add((xs[i], xs[i + 1]));
        }
        foreach (var ring in rings)
            for (int i = 0; i < ring.Length; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Length];
                if (MathF.Max(a.Y, b.Y) < zl || MathF.Min(a.Y, b.Y) > zh) continue;
                float t0 = 0, t1 = 1;
                if (MathF.Abs(b.Y - a.Y) > 1e-6f)
                {
                    float ta = (zl - a.Y) / (b.Y - a.Y), tb = (zh - a.Y) / (b.Y - a.Y);
                    t0 = MathF.Max(0, MathF.Min(ta, tb)); t1 = MathF.Min(1, MathF.Max(ta, tb));
                    if (t0 > t1) continue;
                }
                float xa = a.X + (b.X - a.X) * t0, xb = a.X + (b.X - a.X) * t1;
                iv.Add((MathF.Min(xa, xb), MathF.Max(xa, xb)));
            }
        iv.Sort((p, q) => p.A.CompareTo(q.A));
        var merged = new List<(float A, float B)>();
        foreach (var (a, b) in iv)
        {
            if (merged.Count > 0 && a <= merged[^1].B + 0.5f) merged[^1] = (merged[^1].A, MathF.Max(merged[^1].B, b));
            else merged.Add((a, b));
        }
        return merged;
    }
}
