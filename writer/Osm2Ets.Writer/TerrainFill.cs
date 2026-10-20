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
    /// <summary>So weit neben der Fahrbahn bleibt das Füllgelände weg (Rest überlappt das Straßengelände).</summary>
    public const float MaskTerrain = 2f;
    /// <summary>So weit reicht das Füllgelände unter das Straßengelände (m).</summary>
    public const float FillOverlap = 8f;
    public static bool FillUnderRoads = Environment.GetEnvironmentVariable("OSM2ETS_FILL_MASKROADS") is null;
    /// <summary>Höhenstaffel: Straße &gt; Straßengelände (−5 cm) &gt; Füllgelände (−15 cm), kein Flimmern bei Überlappung.</summary>
    public const float RoadTerrainDrop = 0.05f, FillDrop = 0.15f;
    public static float NearFlat = EnvF("OSM2ETS_RT_FLAT", 3f), NearBlend = EnvF("OSM2ETS_RT_BLEND", 10f);
    /// <summary>Schmale Seiten mit gestauchtem Verlauf (aus: OSM2ETS_RT_NOCOMPRESS).</summary>
    public static bool CompressNarrow = Environment.GetEnvironmentVariable("OSM2ETS_RT_NOCOMPRESS") is null;
    /// <summary>Größte mittlere Neigung des Straßengeländes ab dem flachen Streifen (0 = aus).</summary>
    public static float MaxSlope = EnvF("OSM2ETS_RT_MAXSLOPE", 1.0f);
    public static int SlopeLimited;
    /// <summary>In Zwickeln (andere Straße näher als GoreDist): kurzer Übergang.</summary>
    public static float GoreFlat = 1.5f, GoreBlend = 2.5f, GoreDist = 14f, GoreFull = 8f;
    /// <summary>Nur bei kleinem Höhenunterschied Raster–Fahrbahn (Zwickel auf einer Ebene);
    /// sonst (Damm, Einschnitt, Brückenrampe) entstünde eine Kante.</summary>
    public static float GoreMaxDh = 1.0f;
    /// <summary>Zwischen zwei Straßen: Gelände als gerade Rampe von der eigenen zur fremden Fahrbahnhöhe,
    /// wenn sich beide höchstens so viel unterscheiden (sonst Raster). Aus: OSM2ETS_RT_NOGAP.</summary>
    public static float GapMaxDh = 3f;
    public static bool GapRamp = Environment.GetEnvironmentVariable("OSM2ETS_RT_NOGAP") is null;
    public static int GapRamped;
    /// <summary>Je Spalte kürzen: Reihen enden kurz vor dem Modell einer anderen Straße (an
    /// zusammenlaufenden Fahrbahnen ist die Lücke je Spalte verschieden). Aus: OSM2ETS_RT_NOCLAMP.</summary>
    public static bool ClampColumns = Environment.GetEnvironmentVariable("OSM2ETS_RT_NOCLAMP") is null;
    public static int ColumnsClamped;
    private static float EnvF(string n, float d) =>
        float.TryParse(Environment.GetEnvironmentVariable(n), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
    /// <summary>Füllstücke verketten (alt, OSM2ETS_FILL_CHAIN=1) – siehe Emit.</summary>
    /// <summary>Füllgelände nahe Fahrbahnen nicht höher als die Fahrbahn (aus: OSM2ETS_FILL_NOCAP).</summary>
    public static bool UseCap = Environment.GetEnvironmentVariable("OSM2ETS_FILL_NOCAP") is null;
    private static RoadCap? Cap;
    /// <summary>Obergrenze aus den geschriebenen Fahrbahnen und dem Straßengelände (siehe <see cref="CoverCap"/>).</summary>
    private static CoverCap? CoverLimit;
    private static float FH(TerrainGridEntry grid, float x, float z)
    {
        if (CoverLimit is null) return Cap is null ? grid.HeightAt(x, z) : Cap.Height(grid, x, z);
        // Raster (Prefab-Boden), dann Fahrbahnen/Straßengelände (CoverCap), zuletzt Brücken- und
        // Prefab-Decke – die darf eine Böschung nach unten nicht überstimmen
        float h = Cap is null ? grid.HeightAt(x, z) : Cap.Base(grid, x, z);
        if (float.IsNaN(h)) return h;
        h = CoverLimit.Limit(x, z, h - FillDrop) + FillDrop;
        return Cap is null ? h : Cap.Ceil(x, z, h);
    }
    /// <summary>Alle gesetzten Füllstücke (für <see cref="CoverCap.Settle"/>).</summary>
    private static readonly List<Terrain> placedFill = [];
    public static bool Chain = Environment.GetEnvironmentVariable("OSM2ETS_FILL_CHAIN") is not null;
    /// <summary>Mittellinie (Zeile 0) mit eigenen Höhenpunkten belegen, wie in der SCS-Karte
    /// (dort ~7,5 % aller Offsets in Zeile 0). Aus: OSM2ETS_FILL_NOROW0.</summary>
    public static bool Row0 = Environment.GetEnvironmentVariable("OSM2ETS_FILL_NOROW0") is null;
    public const float PrefabLaneRadius = 2f;
    public const float FineSpacing = 1f;   // Reihenabstand für die Reste neben Straßen (m)
    public const float Overlap = 2f;       // jede Seite reicht so weit über die Nachbarreihe (keine Lücken)
    public const float Sample = 2.5f;       // Abtastung entlang der Reihe (m)
    public const float MaxPiece = 200f;    // längstes Terrain-Stück (m)
    private const float MaskCell = 2f;

    /// <summary>Höhenpunkte quer setzen (ungeprüft im Spiel); sonst ebene Streifen wie v17.</summary>
    public static bool Shape = Environment.GetEnvironmentVariable("OSM2ETS_NOSHAPE") is null;
    /// <summary>Straßengelände breit (bis zur Nachbarstraße) und an das Höhenraster angepasst.</summary>
    public static bool WideRoadTerrain = Environment.GetEnvironmentVariable("OSM2ETS_NARROW") is null;
    /// <summary>Annahme: Offsets des Straßengeländes zählen ab Straßenmitte (true) oder ab Geländebeginn (false).</summary>
    public static bool RoadEdgeInData = false;
    public static int Mismatch;
    /// <summary>Seiten, bei denen das Raster eine Zeile mehr hat als angenommen (äußerste Zeile bleibt unverformt).</summary>
    public static int RowShort;
    public static double Uncovered;

    public sealed record Result(int Items, int Rows, double AreaKm2);

    public static Result Build(Map map, TerrainGridEntry grid, List<RoadEntry> roads,
                               Dictionary<RoadEntry, ResolvedStyle[]> styles,
                               List<(Vector3[] Nodes, float HalfWidth)> prefabs,
                               List<Vector2>? prefabLanes = null, CoverCap? cover = null)
    {
        CoverLimit = cover;
        placedFill.Clear();
        CoverCap.ConeLowered = 0; CoverCap.ConeRaised = 0; CoverCap.CoverLowered = 0;
        // mit CoverCap begrenzen Fahrbahnen das Füllgelände über ihre tatsächliche Oberfläche;
        // RoadCap nur noch für Brücken (Überbau) und Prefabs
        Cap = UseCap ? RoadCap.Build(roads, styles, prefabs, roadsToo: cover is null) : null;
        RoadCap.Lowered = 0; RoadCap.Raised = 0;
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
        // breit: Fahrbahnen aus den tatsächlich geschriebenen Straßenstücken (Hermite-Kurve)
        if (WideRoadTerrain && !FillUnderRoads)
        {
            var widthOf = new Dictionary<string, float>();
            foreach (var st in styles.Values) foreach (var x in st) widthOf[x.RoadType] = MathF.Max(widthOf.GetValueOrDefault(x.RoadType), x.Width);
            foreach (var rd in map.MapItems.Values.OfType<Road>())
            {
                float hw = widthOf.GetValueOrDefault(rd.RoadType.ToString(), 8f) / 2;
                var a = new Vector2(rd.Node.Position.X, rd.Node.Position.Z); var b = new Vector2(rd.ForwardNode.Position.X, rd.ForwardNode.Position.Z);
                float L = Vector2.Distance(a, b); if (L < 1e-3f) continue;
                Vector2 T(Quaternion q) { var v = Vector3.Transform(-Vector3.UnitZ, q); var t = new Vector2(v.X, v.Z); if (t.LengthSquared() < 1e-6f) t = b - a; t = Vector2.Normalize(t); return Vector2.Dot(t, b - a) < 0 ? -t : t; }
                var ta = T(rd.Node.Rotation); var tb = T(rd.ForwardNode.Rotation);
                for (float s2 = 0; s2 <= L; s2 += MaskCell / 2)
                {
                    float t = s2 / L, t2 = t * t, t3 = t2 * t;
                    var c = (2*t3-3*t2+1)*a + (t3-2*t2+t)*L*ta + (-2*t3+3*t2)*b + (t3-t2)*L*tb;
                    Mark(c, hw);
                }
            }
        }
        // Straße + eigene Geländestreifen (seitenrichtig), 1 m Rand
        // Füllgelände liegt 15 cm unter Straße und Straßengelände und darf darunter durchlaufen –
        // auch unter Brücken: dort liegt das Raster auf Bodenhöhe (Brücken zählen im Raster nicht).
        // Nur mit OSM2ETS_FILL_MASKBRIDGES werden Brücken/Tunnel wie früher ausgespart.
        if (WideRoadTerrain && FillUnderRoads && Environment.GetEnvironmentVariable("OSM2ETS_FILL_MASKBRIDGES") is not null)
            foreach (var r in roads)
                foreach (var run in r.Runs.Where(x => x.Bridge || x.Tunnel))
                    for (int k = run.From; k < run.To && k < r.Points.Count - 1; k++)
                    {
                        var a = new Vector2((float)r.Points[k][0], (float)r.Points[k][2]);
                        var b = new Vector2((float)r.Points[k + 1][0], (float)r.Points[k + 1][2]);
                        float L = Vector2.Distance(a, b);
                        for (float s2 = 0; s2 <= L; s2 += MaskCell / 2) Mark(Vector2.Lerp(a, b, L > 0 ? s2 / L : 0), styles[r][k].Width / 2 + 3);
                    }
        foreach (var r in WideRoadTerrain ? [] : roads)
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
                float cap = WideRoadTerrain ? 0 : MaskTerrain;   // breit: Straßengelände nicht doppelt belegen
                float wl = hw + MathF.Min(MathF.Max(0, st[k].TerrainLeft - 6), cap), wr = hw + MathF.Min(MathF.Max(0, st[k].TerrainRight - 6), cap);   // 6 m Überlappung
                for (float s = 0; s <= len; s += MaskCell / 2)
                {
                    var c = a + dir * s;
                    for (float o = -wl; o <= wr; o += MaskCell / 2)
                        Mark(c + right * o, MaskCell * 0.75f);
                }
            }
        }
        // breit: tatsächlich geschriebenes Straßengelände aus den Höhenpunkten in die Maske
        // (statt aus den geplanten Breiten – die stimmen an Knoten/Kurven nicht immer)
        if (WideRoadTerrain && !FillUnderRoads)
            foreach (var rd in map.MapItems.Values.OfType<Road>())
                foreach (var side in new[] { rd.Left.Terrain, rd.Right.Terrain })
                    MarkSide(rd, side);
        void MarkSide(Road rd, RoadTerrain side)
        {
            var q = side.QuadData;
            if (q.Offsets.Count == 0) return;
            var rowsD = RowDistances(side.Size);
            var v = new Dictionary<(int, int), Vector2>();
            foreach (var o in q.Offsets) v[(o.X, o.Y)] = new Vector2(rd.Node.Position.X + o.Data.X, rd.Node.Position.Z + o.Data.Z);
            int jmax = 1;
            for (int j = 1; j < rowsD.Count; j++) if (rowsD[j] <= side.Size - FillOverlap) jmax = j;
            // Zeile 0 ≈ Fahrbahnrand: Zeile 1 nach innen fortsetzen genügt, Fahrbahn markiert die Straßenmaske
            for (int j = 1; j < jmax; j++)
                for (int i = 0; i < q.Cols; i++)
                    if (v.TryGetValue((i, j), out var p00) && v.TryGetValue((i + 1, j), out var p10) &&
                        v.TryGetValue((i + 1, j + 1), out var p11) && v.TryGetValue((i, j + 1), out var p01))
                        MarkQuad(p00, p10, p11, p01);
        }
        void MarkQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float minx = MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X)), maxx = MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X));
            float minz = MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y)), maxz = MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y));
            int i0 = Math.Max(0, (int)((minx - x0) / MaskCell)), i1 = Math.Min(mw - 1, (int)((maxx - x0) / MaskCell) + 1);
            int j0 = Math.Max(0, (int)((minz - z0) / MaskCell)), j1 = Math.Min(mh - 1, (int)((maxz - z0) / MaskCell) + 1);
            static float Cr(Vector2 p, Vector2 q, Vector2 r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
            bool InTri(Vector2 p, Vector2 t0, Vector2 t1, Vector2 t2)
            {
                float d1 = Cr(t0, t1, p), d2 = Cr(t1, t2, p), d3 = Cr(t2, t0, p);
                return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
            }
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    var pt = new Vector2(x0 + i * MaskCell, z0 + j * MaskCell);
                    if (InTri(pt, a, b, c) || InTri(pt, a, c, d)) mask[j * mw + i] = true;
                }
        }
        // Prefabs: wo die Fahrspuren bekannt sind, nur diese (je Punkt 4 m Radius);
        // sonst grob Strecken Knoten → Mitte
        if (WideRoadTerrain && FillUnderRoads) { }   // auch unter Prefabs durchlaufen (15 cm tiefer)
        else if (prefabLanes is { Count: > 0 })
            foreach (var c in prefabLanes) Mark(c, PrefabLaneRadius);
        else
        foreach (var (nodes, hw) in prefabs)
        {
            var c3 = nodes.Aggregate(Vector3.Zero, (s, n) => s + n) / nodes.Length;
            var c = new Vector2(c3.X, c3.Z);
            foreach (var n in nodes)
            {
                var a = new Vector2(n.X, n.Z);
                float len = Vector2.Distance(a, c);
                for (float s2 = 0; s2 <= len; s2 += MaskCell / 2)
                    Mark(Vector2.Lerp(a, c, len > 0 ? s2 / len : 0), hw);
            }
            Mark(c, hw);
        }

        bool Free(float x, float z)
        {
            int i = (int)MathF.Round((x - x0) / MaskCell), j = (int)MathF.Round((z - z0) / MaskCell);
            if (i < 0 || j < 0 || i >= mw || j >= mh) return false;
            return !mask[j * mw + i];
        }
        float half = RowSpacing / 2 + Overlap;
        float spacing = RowSpacing;
        // freie Breite nach Norden (−z, links bei Fahrt nach Osten) bzw. Süden (+z, rechts)
        float Extent(float x, float z, float sign)
        {
            float e = 0;
            for (float d = 1; d <= half; d += 1) { if (!Free(x, z + sign * d)) break; e = d; }
            return e;
        }

        var material = new Token(Road.Add(new Map(), Vector3.Zero, new Vector3(10, 0, 0), "ger1", 1, 1)
                                     .Right.Terrain.QuadData.BrushMaterials[0].Name.ToString());
        int items = 0, rows = 0; double area = 0;
        var covered = new bool[mw * mh];
        var full = new bool[mw * mh];   // tatsächlich bedeckt (ohne Rücknahme), für die feinen Reihen
        bool Covered(float x, float z)
        {
            int i = (int)MathF.Round((x - x0) / MaskCell), j = (int)MathF.Round((z - z0) / MaskCell);
            return i < 0 || j < 0 || i >= mw || j >= mh || covered[j * mw + i];
        }
        void Mark2(bool[] arr, float xa, float xb, float zn, float zs)
        {
            int i0 = Math.Max(0, (int)MathF.Ceiling((xa - x0) / MaskCell)), i1 = Math.Min(mw - 1, (int)MathF.Floor((xb - x0) / MaskCell));
            int j0 = Math.Max(0, (int)MathF.Ceiling((zn - z0) / MaskCell)), j1 = Math.Min(mh - 1, (int)MathF.Floor((zs - z0) / MaskCell));
            for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++) arr[j * mw + i] = true;
        }
        bool Full(float x, float z)
        {
            int i = (int)MathF.Round((x - x0) / MaskCell), j = (int)MathF.Round((z - z0) / MaskCell);
            return i < 0 || j < 0 || i >= mw || j >= mh || full[j * mw + i];
        }
        void Cover(float xa, float xb, float zn, float zs)
        {
            int i0 = Math.Max(0, (int)((xa - x0) / MaskCell)), i1 = Math.Min(mw - 1, (int)((xb - x0) / MaskCell));
            int j0 = Math.Max(0, (int)((zn - z0) / MaskCell)), j1 = Math.Min(mh - 1, (int)((zs - z0) / MaskCell));
            for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++) covered[j * mw + i] = true;
        }
        // Durchgang 0: Reihen alle RowSpacing m. Weitere Durchgänge mit versetzten Reihen füllen
        // nur noch, was frei UND noch nicht bedeckt ist (z. B. neben Straßen, deren Mittellinie
        // eine Hauptreihe blockiert hat).
        // letzter Durchgang: feine Reihen (FineSpacing) für die Reste neben Straßen (Treppenstufen)
        foreach (float offset in new[] { 0f, RowSpacing / 2, RowSpacing / 4, 3 * RowSpacing / 4, -1f })
        {
        if (offset < 0) { spacing = FineSpacing; half = FineSpacing / 2 + 1; }
        for (float z = z0 + RowSpacing / 2 + 10 + Math.Max(offset, 0); z < z1 - RowSpacing / 2 - 10; z += spacing)
        {
            var run = new List<(float X, float L, float R)>();
            void Flush()
            {
                if (run.Count >= 2)
                {
                    // Bedeckung nach den tatsächlich gesetzten Stücken (Breite = Minimum je Stück)
                    items += Emit(map, grid, run, z, material, ref area, (xa, xb, l, r) =>
                    {
                        Cover(xa, xb, z - l + 2, z + r - 2);
                        Mark2(full, xa, xb, z - l, z + r);
                    });
                }
                run.Clear();
            }
            bool any = false;
            for (float x = x0 + 10; x < x1 - 10; x += Sample)
            {
                bool ok = Free(x, z) && (offset == 0 || !Covered(x, z)) && (offset >= 0 || !Full(x, z));
                float l = ok ? Extent(x, z, -1) : -1, r = ok ? Extent(x, z, +1) : -1;
                if (l < 1 || r < 1 || float.IsNaN(grid.HeightAt(x, z))) { Flush(); continue; }
                run.Add((x, l, r)); any = true;
            }
            Flush();
            if (any) rows++;
        }
        }
        if (Environment.GetEnvironmentVariable("OSM2ETS_MASKDUMP") is { } md)
        {
            var pp = md.Split(',').Select(float.Parse).ToArray();   // x,z,halbe Größe
            int ci = (int)((pp[0] - x0) / MaskCell), cj = (int)((pp[1] - z0) / MaskCell), r = (int)(pp[2] / MaskCell);
            using var w = new StreamWriter("maskdump.pgm");
            w.Write($"P2 {2 * r} {2 * r} 2\n");
            for (int j = cj - r; j < cj + r; j++)
            {
                for (int i = ci - r; i < ci + r; i++)
                {
                    int k = j * mw + i; bool ok = i >= 0 && j >= 0 && i < mw && j < mh;
                    w.Write(!ok ? "0 " : mask[k] ? "1 " : full[k] ? "2 " : "0 ");
                }
                w.Write("\n");
            }
        }
        cover?.Settle(placedFill);
        int free = 0, open = 0;
        for (int k = 0; k < mask.Length; k++) if (!mask[k]) { free++; if (!full[k]) open++; }
        Uncovered = free > 0 ? open / (double)free : 0;
        return new Result(items, rows, area / 1e6);
    }

    private static int Emit(Map map, TerrainGridEntry grid, List<(float X, float L, float R)> run, float z,
                            Token material, ref double area, Action<float, float, float, float> placed)
    {
        // Knoten gierig so weit wie möglich setzen: Höhe linear (±0,4 m), Breiten fast gleich,
        // höchstens MaxPiece m – spart viele Items auf flachem, freiem Feld
        var hs = new float[run.Count];
        for (int i = 0; i < run.Count; i++) hs[i] = FH(grid, run[i].X, z);
        float H(int i) => hs[i];
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
            placed(run[a].X, run[b].X, l, r);
            var pa = new Vector3(run[a].X, H(a) - FillDrop, z);
            var pb = new Vector3(run[b].X, H(b) - FillDrop, z);
            // Jedes Stück eigenständig (nicht per Append verkettet): TruckLib mittelt an einem
            // gemeinsamen Knoten die Richtung beider Nachbarstücke. Wechselt die Steigung dort
            // stark (Böschung neben Straße), wölbt sich die Mittellinie des Stücks als Hermite-
            // Kurve meterhoch auf – der „Keil“ auf der A 70 an der B 85 (bis ~3 m über der Fahrbahn).
            // Eigenständig zeigt jeder Knoten entlang der eigenen Sehne.
            t = Chain ? (t is null ? Terrain.Add(map, pa, pb, material, l, r) : t.Append(pb, material, l, r))
                      : Terrain.Add(map, pa, pb, material, l, r);
            t.Boundary = false;
            t.IgnoreCutPlanes = true;   // Prefabs schneiden sonst Gelände in ihrer Umgebung weg
            t.ViewDistance = 1000;
            t.Left.Terrain.Noise = TerrainNoise.Percent0;
            t.Right.Terrain.Noise = TerrainNoise.Percent0;
            placedFill.Add(t);
            // gröberes Raster (16 m statt 4 m): flaches Feld braucht keine feinen Quads
            // kurze Stücke brauchen feinere Schritte, sonst nur eine Rasterspalte (= unsichtbar)
            float plen = pb.X - pa.X;
            // kurze Stücke: Standard-Schrittweite und eben (ohne Höhenpunkte) – mit eigenen
            // Höhenpunkten wurden sie im Editor teils nicht gezeichnet
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
        if (rowsD.Count == rows) RowShort++;
        // QuadData.Cols/Rows zählen Quads; die Eckpunkte laufen 0..Cols bzw. 0..Rows
        // (so auch in der SCS-Originalkarte). Früher fehlte die letzte Spalte → Wand am Stückende.
        for (int j = Row0 ? 0 : 1; j < rowsD.Count; j++)
            for (int i = 0; i <= cols; i++)
            {
                float x = len * i / cols;
                float z = pa.Z + zSign * rowsD[j];
                float h = FH(grid, pa.X + x, z);
                if (float.IsNaN(h)) continue;
                q.Offsets.Add(new VertexData { X = (ushort)i, Y = (ushort)j, Data = new Vector3(x, h - FillDrop - pa.Y, zSign * rowsD[j]) });
                n++;
            }
        return n;
    }

    /// <summary>
    /// Wie ShapeSide, aber für das Gelände eines Straßenstücks beliebiger Richtung.
    /// Annahme (ungeprüft): Offsets im lokalen System des Stücks (x entlang der Sehne,
    /// z quer, links negativ). Höhe wird am Weltpunkt neben der Straße abgefragt.
    /// </summary>
    internal static int ShapeRoadSide(RoadTerrain side, TerrainGridEntry grid, Vector3 pa, Vector3 pb, float size, float zSign, float edge, Vector2? tA = null, Vector2? tB = null, Func<float, float, float, (float Dist, float Height, bool Bridge)>? other = null, Vector3? dA = null, Vector3? dB = null, Func<Vector2, Vector2, float, float>? rayFree = null)
    {
        var q = side.QuadData;
        q.Offsets.Clear();
        if (size <= 0) return 0;
        var rowsD = RowDistances(size);
        int rows = q.Rows, cols = q.Cols;
        if (rows < 1 || cols < 1) return 0;
        if (rowsD.Count != rows + 1 && rowsD.Count != rows) return -1;
        if (rowsD.Count == rows) RowShort++;
        var a = new Vector2(pa.X, pa.Z); var b = new Vector2(pb.X, pb.Z);
        float len = Vector2.Distance(a, b);
        if (len < 1e-3f) return 0;
        var f = (b - a) / len;
        // Mittellinie wie im Spiel: Hermite-Kurve mit den Knotentangenten (Länge = Sehne)
        var ta = tA ?? f; var tb = tB ?? f;
        Vector2 C(float t) { float t2 = t * t, t3 = t2 * t; return (2*t3-3*t2+1)*a + (t3-2*t2+t)*len*ta + (-2*t3+3*t2)*b + (t3-t2)*len*tb; }
        Vector2 Dn(float t)
        {
            float t2 = t * t;
            var d = (6*t2-6*t)*a + (3*t2-4*t+1)*len*ta + (-6*t2+6*t)*b + (3*t2-2*t)*len*tb;
            return d.LengthSquared() > 1e-8f ? Vector2.Normalize(d) : f;
        }
        bool world = Mode is 'C' or 'D';
        // Fahrbahn wie im Spiel als 3D-Hermite-Kurve (Knotendrehung samt Neigung, Tangentenlänge =
        // Knotenabstand): Höhe und Lage. Mit der Sehnenhöhe lag das Gelände dort, wo die Kurve
        // durchhängt (Gefällewechsel), bis über 10 cm zu hoch – am Rand über dem Asphalt.
        bool curve3 = world && dA is not null && dB is not null && UseHermiteHeight;
        float len3 = Vector3.Distance(pa, pb);
        Vector3 C3(float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return (2*t3-3*t2+1)*pa + (t3-2*t2+t)*len3*dA!.Value + (-2*t3+3*t2)*pb + (t3-t2)*len3*dB!.Value;
        }
        Vector2 D3(float t)
        {
            float t2 = t * t;
            var d = (6*t2-6*t)*pa + (3*t2-4*t+1)*len3*dA!.Value + (-6*t2+6*t)*pb + (3*t2-2*t)*len3*dB!.Value;
            var d2 = new Vector2(d.X, d.Z);
            return d2.LengthSquared() > 1e-8f ? Vector2.Normalize(d2) : f;
        }
        int n = 0;
        // je Spalte: freie Breite bis zum Modell einer anderen Straße (Strahl vom eigenen Rand nach außen)
        var free = new float[cols + 1];
        for (int i = 0; i <= cols; i++)
        {
            free[i] = float.MaxValue;
            if (rayFree is null || !world) continue;
            float u = (float)i / cols;
            Vector2 c, nrm;
            if (curve3) { var c3 = C3(u); c = new Vector2(c3.X, c3.Z); var tg = D3(u); nrm = new Vector2(-tg.Y, tg.X); }
            else { c = C(u); var tg = Dn(u); nrm = new Vector2(-tg.Y, tg.X); }
            var dir = nrm * zSign;
            float g = rayFree(c + dir * edge, dir, size);
            if (g < size) { free[i] = MathF.Max(0, g - TerrainClearance.EdgeClear); ColumnsClamped++; }
        }
        // Schmale Seiten (unter NearFlat + NearBlend): Verlauf gestaucht, damit der Außenrand die
        // Rasterhöhe erreicht. Sonst endete das Straßengelände auf Fahrbahnhöhe – bei einem Damm
        // über, im Einschnitt unter dem anschließenden Füllgelände (Kante bis einige Meter).
        float flat = NearFlat, blend = NearBlend;
        if (CompressNarrow && size < NearFlat + NearBlend)
        {
            float sc = size / (NearFlat + NearBlend);
            flat *= sc; blend *= sc;
        }
        // Höhe der ersten gekürzten Reihe je Spalte: alle weiteren gekürzten Reihen bekommen dieselbe
        // (sie liegen nur 1 cm auseinander – verschiedene Höhen wären eine senkrechte Wand)
        var clampH = new float[cols + 1];
        Array.Fill(clampH, float.NaN);
        for (int j = 1; j < rowsD.Count; j++)
            for (int i = 0; i <= cols; i++)
            {
                float u = (float)i / cols;
                // gekürzte Reihen nicht exakt aufeinander (Fläche 0, Normale unbestimmt), sondern je 1 cm
                // versetzt – bei höchstens 10 Reihen reicht das bis an den fremden Rand (EdgeClear 0,1 m)
                float dd = rowsD[j];
                bool clamped = dd > free[i];
                if (clamped)
                {
                    int first = j; while (first > 1 && rowsD[first - 1] > free[i]) first--;
                    dd = free[i] + 0.01f * (j - first);
                }
                float d = edge + dd;
                Vector2 c, nrm; float x;
                float roadY = pa.Y + (pb.Y - pa.Y) * u;
                if (curve3) { var c3 = C3(u); c = new Vector2(c3.X, c3.Z); roadY = c3.Y; var tg = D3(u); nrm = new Vector2(-tg.Y, tg.X); x = 0; }
                else if (world) { c = C(u); var tg = Dn(u); nrm = new Vector2(-tg.Y, tg.X); x = 0; }
                else { x = len * u; c = a + f * x; nrm = new Vector2(-f.Y, f.X); }
                var w = c + nrm * (zSign * d);
                float h = grid.HeightAt(w.X, w.Y);
                if (float.IsNaN(h)) continue;
                // erste NearFlat m auf Straßenhöhe, bis NearFlat+NearBlend m ins Raster übergehen.
                // Kurz halten: in Zwickeln zwischen zwei Straßen (Gabelung) zieht sonst jedes
                // Straßengelände zur eigenen Fahrbahnhöhe – dort, wo sich beide überlappen,
                // entsteht eine Stufe (dunkler Keil). Das Raster hält die Straßenhöhe nahe der
                // Straße ohnehin (terrain.py: core 6 m).
                // Abstand vom eigenen Rand nach dem Kürzen (dd): gekürzte Reihen liegen 1 cm auseinander
                // und müssen gleich hoch sein – mit der ungekürzten Reihenlage bekamen die äußeren
                // Reihen die Rasterhöhe, die inneren die Fahrbahnhöhe: senkrechte Wand (v79)
                float tt = Math.Clamp((dd - flat) / blend, 0, 1);
                if (other is not null)
                {
                    var (od, oh, ob) = other(w.X, w.Y, roadY);
                    if (od < GoreDist)
                    {
                        float k = Math.Clamp((GoreDist - od) / (GoreDist - GoreFull), 0, 1);
                        if (GapRamp && !ob && !float.IsNaN(oh) && MathF.Abs(oh - roadY) < GapMaxDh)
                        {
                            // zwischen zwei Fahrbahnen: gerade Rampe von der eigenen zur fremden Höhe.
                            // Das Gelände der anderen Straße liegt von ihrer Seite her auf derselben
                            // Ebene – keine Stufe in der Mitte (Mittelstreifen, Rampe neben der
                            // Hauptfahrbahn) und kein sichtbares Füllgelände dazwischen.
                            float hr = roadY + (h - roadY) * tt;
                            float rj = d - edge;
                            float hg = roadY + (oh - roadY) * (rj / MathF.Max(rj + od, 1e-3f));
                            h = hr + (hg - hr) * k;
                            GapRamped++;
                            goto shaped;
                        }
                        if (MathF.Abs(h - roadY) < GoreMaxDh)
                        {
                            // Zwickel: nahe einer anderen Straße schneller ins Raster (das beide Straßen
                            // stetig verbindet), sonst Stufe zwischen zwei Straßengeländen
                            float tg = Math.Clamp((dd - GoreFlat) / GoreBlend, 0, 1);
                            float t2 = tt + (MathF.Max(tt, tg) - tt) * k;
                            if (t2 > tt) RoadCap.RoadTerrainLowered++;
                            tt = t2;
                        }
                    }
                }
                h = roadY + (h - roadY) * tt;
                // nie steiler als MaxSlope ab dem flachen Streifen (Rest übernimmt das Füllgelände)
                if (MaxSlope > 0)
                {
                    float lim = MaxSlope * MathF.Max(0, dd - flat);
                    float dh0 = Math.Clamp(h - roadY, -lim, lim);
                    if (dh0 != h - roadY) SlopeLimited++;
                    h = roadY + dh0;
                }
            shaped:
                if (clamped)
                {
                    if (float.IsNaN(clampH[i])) clampH[i] = h; else h = clampH[i];
                }
                h -= RoadTerrainDrop;
                var off = world
                    ? new Vector3(w.X - pa.X, h - pa.Y, w.Y - pa.Z)        // Weltachsen ab Anfangsknoten
                    : new Vector3(x, h - pa.Y, zSign * d);                 // lokal
                q.Offsets.Add(new VertexData { X = (ushort)i, Y = (ushort)j, Data = off });
                n++;
            }
        return n;
    }

    /// <summary>Größte Breite, bis zu der keine Quad-Zeile umklappt (Kreuzprodukt wechselt Vorzeichen).</summary>
    internal static float FoldFreeSize(Road rd, RoadTerrain side, float zSign, float edge, Vector2? tA, Vector2? tB, Vector3? dA = null, Vector3? dB = null)
    {
        var q = side.QuadData;
        if (q.Offsets.Count == 0 || side.Size <= 0) return side.Size;
        var rowsD = RowDistances(side.Size);
        var p0 = new Vector2(rd.Node.Position.X, rd.Node.Position.Z);
        var v = new Dictionary<(int, int), Vector2>();
        foreach (var o in q.Offsets) v[(o.X, o.Y)] = p0 + new Vector2(o.Data.X, o.Data.Z);
        // Zeile 0 = Fahrbahnrand auf der Kurve
        var a = p0; var b = new Vector2(rd.ForwardNode.Position.X, rd.ForwardNode.Position.Z);
        float len = Vector2.Distance(a, b); if (len < 1e-3f) return side.Size;
        var f = (b - a) / len; var ta = tA ?? f; var tb = tB ?? f;
        var pa3 = rd.Node.Position; var pb3 = rd.ForwardNode.Position; float len3 = Vector3.Distance(pa3, pb3);
        bool curve3 = dA is not null && dB is not null && UseHermiteHeight;
        for (int i = 0; i <= q.Cols; i++)
        {
            float t = (float)i / q.Cols, t2 = t * t, t3 = t2 * t;
            Vector2 c, d;
            if (curve3)
            {
                var c3 = (2*t3-3*t2+1)*pa3 + (t3-2*t2+t)*len3*dA!.Value + (-2*t3+3*t2)*pb3 + (t3-t2)*len3*dB!.Value;
                var d3 = (6*t2-6*t)*pa3 + (3*t2-4*t+1)*len3*dA!.Value + (-6*t2+6*t)*pb3 + (3*t2-2*t)*len3*dB!.Value;
                c = new Vector2(c3.X, c3.Z); d = new Vector2(d3.X, d3.Z);
            }
            else
            {
                c = (2*t3-3*t2+1)*a + (t3-2*t2+t)*len*ta + (-2*t3+3*t2)*b + (t3-t2)*len*tb;
                d = (6*t2-6*t)*a + (3*t2-4*t+1)*len*ta + (-6*t2+6*t)*b + (3*t2-2*t)*len*tb;
            }
            d = d.LengthSquared() > 1e-8f ? Vector2.Normalize(d) : f;
            v[(i, 0)] = c + new Vector2(-d.Y, d.X) * (zSign * edge);
        }
        for (int j = 0; j < rowsD.Count - 1; j++)
            for (int i = 0; i < q.Cols; i++)
            {
                if (!v.TryGetValue((i, j), out var p00) || !v.TryGetValue((i + 1, j), out var p10) ||
                    !v.TryGetValue((i, j + 1), out var p01) || !v.TryGetValue((i + 1, j + 1), out var p11)) continue;
                var e1 = p10 - p00; var e2 = p01 - p00; var e3 = p11 - p10; var e4 = p11 - p01;
                float c1 = e1.X * e2.Y - e1.Y * e2.X, c2 = e3.X * (p10 - p11 + e3 * 0).Y; // Platzhalter
                float s1 = (e1.X * e2.Y - e1.Y * e2.X) * zSign;
                float s2 = ((p11 - p10).X * (p10 - p00).Y - (p11 - p10).Y * (p10 - p00).X) * -zSign;
                float s3 = ((p11 - p01).X * (p01 - p00).Y - (p11 - p01).Y * (p01 - p00).X) * zSign;
                // je Spalte gekürzte Reihen fallen zusammen (Fläche 0) – das ist kein Umklappen
                bool collapsed = ClampColumns && ((p01 - p00).LengthSquared() < 4e-4f || (p11 - p10).LengthSquared() < 4e-4f);
                if (collapsed) continue;
                if (s1 <= 0 || s2 <= 0 || (p11 - p01).LengthSquared() < 1e-4f || Vector2.Dot(p10 - p00, p11 - p01) <= 0)
                    return MathF.Max(0, MathF.Floor(rowsD[j]));
            }
        return side.Size;
    }

    /// <summary>Straßengelände folgt der Hermite-Höhe der Fahrbahn (aus: OSM2ETS_RT_CHORD).</summary>
    public static bool UseHermiteHeight = Environment.GetEnvironmentVariable("OSM2ETS_RT_CHORD") is null;

    /// <summary>Testvariante: A lokal ab Geländebeginn, B lokal ab Straßenmitte, C Weltachsen.</summary>
    [ThreadStatic] public static char Mode;
}
