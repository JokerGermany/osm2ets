// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using TruckLib;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Geländematerial: überall Gras („grass ger main“, darauf wächst die deutsche Detailvegetation – Grasbüschel
/// und Blumen), in Flächen der Bodenbedeckung aus OSM (Zwischenformat: landcover) ein passendes Material.
/// Äcker bekommen Feldmaterialien der gemeinsamen SCS-Materialbibliothek („agricultural …“ aus
/// terrain_material.base_share.sii): je Acker ein Bild (Weizen grün/jung/reif, Stoppeln, Mais, Raps, grünes
/// Feld, gepflügt, Reihenkultur) nach crop=* oder – ohne Angabe – fest nach der OSM-Kennung gestreut; nahe
/// der Strecke die nahe Variante, weiter weg „mid“, weit weg „far“. Furchen und Pflanzreihen (Äcker, Obst- und
/// Rebanlagen, Baumschulen) folgen der Längsseite der Fläche (Materialdrehung je Geländeseite).
/// Gemalt wird je Quad des Straßen- und Füllgeländes nach seiner Mitte; in Überschneidungen gilt die
/// kleinere Fläche. Prefab-Gelände bekommt nur das Grundmaterial. Reihenfolge der Quads wie in
/// <see cref="Underpasses.Pave"/>: Spalte für Spalte, je Spalte die Reihen nach außen.
/// Aus: OSM2ETS_NO_LANDCOVER=1 (dann bleibt das bisherige Material „dirt“).
/// Ungeprüft: Aussehen der Materialien, Richtung der Materialdrehung (OSM2ETS_FIELD_ROT_SIGN=-1 dreht andersherum,
/// OSM2ETS_FIELD_ROT_OFFSET=90 quer), dass die gemeinsamen Materialien im Grundspiel ohne DLC vorhanden sind.
/// </summary>
public static class Landcover
{
    public static bool Enabled = Environment.GetEnvironmentVariable("OSM2ETS_NO_LANDCOVER") != "1";
    /// <summary>Grundmaterial überall (terrain_mat.34 „grass ger main“).</summary>
    public const string Default = "34";
    /// <summary>Material je Art (terrain_material.sii bzw. .base_share.sii des Grundspiels; Auswahl nach Namen,
    /// Aussehen ungeprüft). Äcker: <see cref="FieldLooks"/>.</summary>
    public static readonly Dictionary<string, string> Materials = new()
    {
        ["lawn"] = "82",            // grass green main – gepflegtes Gras
        ["meadow"] = "77",          // grass flowers main – Wiese mit Blumen
        ["orchard"] = "77",         // grass flowers main – Streuobstwiese (Bäume: noch nicht)
        ["farmyard"] = "share_516", // generic dirt gravel 01 – Hoffläche
        ["vineyard"] = "share_288", // grassland grass rows 01 01 – Rebzeilen
        ["nursery"] = "share_331",  // agricultural soil grass rows 01 – Baumschule
        ["greenhouse"] = "share_37",// generic gravel 01 01 – zwischen den Gewächshäusern
        ["heath"] = "80",           // grass droughty main
        ["scrub"] = "24",           // dirty grass
        ["bare"] = "0",             // dirt
        ["farmland"] = "",          // je Acker aus FieldLooks
    };
    /// <summary>Arten mit Reihen: Material an der Längsseite ausgerichtet.</summary>
    static readonly HashSet<string> RowKinds = ["farmland", "orchard", "vineyard", "nursery"];

    /// <summary>Feldbild: Material nah (ohne Zusatz), mittel („mid“), fern („far“), Anteil ohne crop=*.</summary>
    public sealed record FieldLook(string Name, string Near, string Mid, string Far, float Weight);
    public static readonly FieldLook[] FieldLooks =
    [
        new("wheat green", "share_98", "share_101", "share_103", 0.18f),   // agricultural wheat green 01 01
        new("wheat young", "share_99", "share_100", "share_102", 0.10f),   // agricultural wheat young 01 01
        new("wheat ripe", "share_91", "share_95", "share_97", 0.14f),      // agricultural wheat dry 01 01
        new("stubble", "share_92", "share_94", "share_96", 0.05f),         // agricultural wheat cut 01 01
        new("corn", "share_326", "share_326", "share_328", 0.15f),         // field corn tall
        new("rape", "share_142", "share_142", "share_142", 0.08f),         // agricultural rape 01 01
        new("green", "share_68", "share_68", "share_70", 0.12f),           // agricultural field green 01 01
        new("plowed", "share_67", "share_67", "share_69", 0.10f),          // agricultural field plowed 01 01
        new("rows", "share_272", "share_272", "share_273", 0.08f),         // agricultural field rows 01
    ];
    /// <summary>Acker bis so weit (m) von einer Straße der Karte: nahe Variante, bis FieldMid: mittlere, sonst ferne.</summary>
    public const float FieldNear = 80f, FieldMid = 300f;
    /// <summary>Drehung in Schritten (Grad); Vorzeichen und Versatz zum Prüfen im Spiel.</summary>
    public const float RotStep = 15f;
    public static float RotSign = Environment.GetEnvironmentVariable("OSM2ETS_FIELD_ROT_SIGN") == "-1" ? -1f : 1f;
    public static float RotOffset = float.TryParse(Environment.GetEnvironmentVariable("OSM2ETS_FIELD_ROT_OFFSET"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ro) ? ro : 0f;

    static readonly string[] Cereals = ["wheat", "barley", "rye", "oats", "oat", "triticale", "spelt", "cereal", "cereals", "grain"];

    /// <summary>Feldbild eines Ackers: nach crop=*, sonst fest nach der OSM-Kennung gestreut.</summary>
    public static FieldLook LookFor(string osm, string? crop)
    {
        uint h = 2166136261;
        foreach (char ch in osm) h = (h ^ ch) * 16777619;
        float u = (h % 10000) / 10000f;
        FieldLook Pick(IEnumerable<FieldLook> looks)
        {
            var l = looks.ToList();
            float sum = l.Sum(x => x.Weight), acc = 0;
            foreach (var x in l) { acc += x.Weight / sum; if (u < acc) return x; }
            return l[^1];
        }
        FieldLook Named(string n) => FieldLooks.First(x => x.Name == n);
        var c = (crop ?? "").Trim().ToLowerInvariant();
        if (Cereals.Contains(c)) return Pick(FieldLooks.Where(x => x.Name is "wheat green" or "wheat young" or "wheat ripe" or "stubble"));
        return c switch
        {
            "maize" or "corn" => Named("corn"),
            "rape" or "rapeseed" or "canola" or "oilseed_rape" => Named("rape"),
            "potato" or "potatoes" or "sugar_beet" or "sugarbeet" or "beet" or "vegetables" or "vegetable" or "asparagus"
                or "strawberry" or "strawberries" or "hop" or "hops" => Named("rows"),
            "grass" or "hay" or "clover" or "fodder" or "alfalfa" or "lucerne" or "silage" => Named("green"),
            _ => Pick(FieldLooks),
        };
    }

    /// <summary>Materialdrehung (Grad, 0…180 in RotStep-Schritten) für Reihen in Richtung <paramref name="angle"/>
    /// auf einer Geländeseite, deren Element in Richtung <paramref name="dir"/> läuft (beides x-z-Ebene).</summary>
    public static float Rotation(float angle, float dir)
    {
        float r = RotSign * (angle - dir) + RotOffset;
        r = ((r % 180) + 180) % 180;
        r = MathF.Round(r / RotStep) * RotStep;
        return r >= 180 ? 0 : r;
    }

    /// <summary>Bisheriges Material der Straßen-/Füll-/Prefab-Seiten (TruckLib-Vorgabe „dirt“).</summary>
    const string Dirt = "0";

    private sealed record Poly(Vector2[] Outer, List<Vector2[]> Inner, string Kind, float Area, Vector2 Min, Vector2 Max,
                               Token Mat, float Angle);

    public sealed record Result(int Sides, int Quads, Dictionary<string, int> ByKind, int PrefabSides,
                                Dictionary<string, int> Fields, int[] FieldDistance, int Rotated);

    public sealed class Index
    {
        const float Cell = 100f;
        readonly Dictionary<(int, int), List<Poly>> cells = [];
        public int Count { get; }
        /// <summary>Äcker je Feldbild; je Entfernungsstufe (nah, mittel, fern).</summary>
        public readonly Dictionary<string, int> Fields = [];
        public readonly int[] FieldDistance = new int[3];

        /// <param name="roadDistance">Abstand einer Fläche (Außenring) zur nächsten Straße der Karte, für die
        /// Feldvarianten; null = alle fern.</param>
        public Index(IEnumerable<LandcoverEntry> entries, Func<Vector2[], float>? roadDistance = null)
        {
            foreach (var e in entries)
            {
                if (e.Outer.Count < 3 || !Materials.ContainsKey(e.Kind)) continue;
                var o = e.Outer.Select(p => new Vector2((float)p[0], (float)p[1])).ToArray();
                var inner = e.Inner.Where(r => r.Count >= 3).Select(r => r.Select(p => new Vector2((float)p[0], (float)p[1])).ToArray()).ToList();
                float area = MathF.Abs(Signed(o)) - inner.Sum(r => MathF.Abs(Signed(r)));
                var min = new Vector2(o.Min(p => p.X), o.Min(p => p.Y)); var max = new Vector2(o.Max(p => p.X), o.Max(p => p.Y));
                string mat = Materials[e.Kind];
                if (e.Kind == "farmland")
                {
                    var look = LookFor(e.Osm, e.Crop);
                    float d = roadDistance?.Invoke(o) ?? float.PositiveInfinity;
                    int cls = d <= FieldNear ? 0 : d <= FieldMid ? 1 : 2;
                    mat = cls == 0 ? look.Near : cls == 1 ? look.Mid : look.Far;
                    Fields[look.Name] = Fields.GetValueOrDefault(look.Name) + 1;
                    FieldDistance[cls]++;
                }
                float angle = RowKinds.Contains(e.Kind) && e.Angle is { } a ? (float)a : float.NaN;
                var poly = new Poly(o, inner, e.Kind, area, min, max, new Token(mat), angle);
                for (int i = (int)MathF.Floor(min.X / Cell); i <= (int)MathF.Floor(max.X / Cell); i++)
                    for (int j = (int)MathF.Floor(min.Y / Cell); j <= (int)MathF.Floor(max.Y / Cell); j++)
                    {
                        if (!cells.TryGetValue((i, j), out var l)) cells[(i, j)] = l = [];
                        l.Add(poly);
                    }
                Count++;
            }
            foreach (var l in cells.Values) l.Sort((a, b) => a.Area.CompareTo(b.Area));   // kleinste zuerst
        }

        static float Signed(Vector2[] r)
        {
            float a = 0;
            for (int i = 0, j = r.Length - 1; i < r.Length; j = i++) a += (r[j].X - r[i].X) * (r[j].Y + r[i].Y);
            return a / 2;
        }

        static bool InRing(Vector2[] r, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = r.Length - 1; i < r.Length; j = i++)
                if ((r[i].Y > p.Y) != (r[j].Y > p.Y) && p.X < (r[j].X - r[i].X) * (p.Y - r[i].Y) / (r[j].Y - r[i].Y) + r[i].X)
                    inside = !inside;
            return inside;
        }

        private Poly? At(Vector2 p)
        {
            if (!cells.TryGetValue(((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell)), out var l)) return null;
            foreach (var f in l)
            {
                if (p.X < f.Min.X || p.Y < f.Min.Y || p.X > f.Max.X || p.Y > f.Max.Y || !InRing(f.Outer, p)) continue;
                if (f.Inner.Any(r => InRing(r, p))) continue;
                return f;
            }
            return null;
        }

        /// <summary>Art der kleinsten Fläche, die p enthält, oder null.</summary>
        public string? KindAt(Vector2 p) => At(p)?.Kind;

        /// <summary>Material und Reihenrichtung (Grad, NaN ohne Reihen) der kleinsten Fläche an p.</summary>
        internal (string Kind, Token Mat, float Angle)? MaterialAt(Vector2 p) => At(p) is { } f ? (f.Kind, f.Mat, f.Angle) : null;
    }

    /// <summary>Abstand eines Rings (Punkte alle ≤ 10 m) zur nächsten Straße der Karte (ohne Gleise), höchstens
    /// bis FieldMid genau – darüber +∞.</summary>
    static Func<Vector2[], float> RoadDistance(List<RoadEntry> roads)
    {
        const float C = 100f;
        var pts = new Dictionary<(int, int), List<Vector2>>();
        foreach (var r in roads)
        {
            if (r.Role == "rail") continue;
            for (int k = 0; k + 1 < r.Points.Count; k++)
            {
                var a = new Vector2((float)r.Points[k][0], (float)r.Points[k][2]);
                var b = new Vector2((float)r.Points[k + 1][0], (float)r.Points[k + 1][2]);
                int n = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(a, b) / 10));
                for (int s = 0; s <= n; s++)
                {
                    var q = Vector2.Lerp(a, b, s / (float)n);
                    var key = ((int)MathF.Floor(q.X / C), (int)MathF.Floor(q.Y / C));
                    if (!pts.TryGetValue(key, out var l)) pts[key] = l = [];
                    l.Add(q);
                }
            }
        }
        int reach = (int)MathF.Ceiling(FieldMid / C);
        return ring =>
        {
            float best = float.PositiveInfinity;
            for (int i = 0; i < ring.Length; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Length];
                int n = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(a, b) / 10));
                for (int s = 0; s < n; s++)
                {
                    var p = Vector2.Lerp(a, b, s / (float)n);
                    int gi = (int)MathF.Floor(p.X / C), gj = (int)MathF.Floor(p.Y / C);
                    for (int x = gi - reach; x <= gi + reach; x++)
                        for (int z = gj - reach; z <= gj + reach; z++)
                            if (pts.TryGetValue((x, z), out var l))
                                foreach (var q in l)
                                {
                                    float d = Vector2.Distance(p, q);
                                    if (d < best) best = d;
                                }
                }
                if (best <= FieldNear) return best;
            }
            return best <= FieldMid ? best : float.PositiveInfinity;
        };
    }

    /// <summary>Grundmaterial setzen und die Quads aller Straßen- und Füllgelände-Seiten nach der Bodenbedeckung
    /// bemalen. Nach dem Formen des Geländes aufrufen (die Quad-Ecken stehen dann in den Offsets).</summary>
    public static Result Apply(Map map, IntermediateFile input)
    {
        var ix = new Index(input.Landcover, input.Landcover.Any(e => e.Kind == "farmland") ? RoadDistance(input.Roads) : null);
        int sides = 0, quads = 0, prefabSides = 0, rotated = 0;
        var byKind = new Dictionary<string, int>();
        foreach (var it in map.MapItems.Values)
        {
            switch (it)
            {
                case Road r:
                    foreach (var side in new[] { r.Left.Terrain, r.Right.Terrain })
                        if (Paint(side, r.Node.Position, Dir(r.Node.Position, r.ForwardNode.Position), ix, byKind, ref quads, ref rotated)) sides++;
                    break;
                case Terrain t:
                    foreach (var side in new[] { t.Left.Terrain, t.Right.Terrain })
                        if (Paint(side, t.Node.Position, Dir(t.Node.Position, t.ForwardNode.Position), ix, byKind, ref quads, ref rotated)) sides++;
                    break;
                case Prefab p:
                    foreach (var n in p.PrefabNodes)
                        if (n.Terrain is { } pt && pt.QuadData.BrushMaterials.Count > 0 && pt.QuadData.BrushMaterials[0].Name == new Token(Dirt))
                        {
                            pt.QuadData.BrushMaterials[0] = new Material(Default);
                            prefabSides++;
                        }
                    break;
            }
        }
        return new Result(sides, quads, byKind, prefabSides, ix.Fields, ix.FieldDistance, rotated);
    }

    /// <summary>Richtung eines Elements (Anfangs- zum Endknoten) in Grad, x-z-Ebene.</summary>
    static float Dir(Vector3 a, Vector3 b) => MathF.Atan2(b.Z - a.Z, b.X - a.X) * 180f / MathF.PI;

    static bool Paint(RoadTerrain side, Vector3 origin, float dir, Index ix, Dictionary<string, int> byKind, ref int painted, ref int rotated)
    {
        var q = side.QuadData;
        if (side.Size <= 0 || q.BrushMaterials.Count == 0) return false;
        if (q.BrushMaterials[0].Name == new Token(Dirt)) q.BrushMaterials[0] = new Material(Default);
        if (ix.Count == 0 || q.Offsets.Count == 0 || q.Quads.Count != q.Cols * q.Rows) return true;
        var at = new Dictionary<(int, int), Vector3>(q.Offsets.Count);
        foreach (var o in q.Offsets) at[(o.X, o.Y)] = origin + o.Data;       // Weltachsen ab Anfangsknoten
        for (int i = 0; i < q.Cols; i++)
            for (int j = 0; j < q.Rows; j++)
            {
                if (!at.TryGetValue((i, j), out var p00) || !at.TryGetValue((i + 1, j), out var p10) ||
                    !at.TryGetValue((i, j + 1), out var p01) || !at.TryGetValue((i + 1, j + 1), out var p11)) continue;
                var c = (p00 + p10 + p01 + p11) / 4;
                if (ix.MaterialAt(new Vector2(c.X, c.Z)) is not { } m) continue;
                int k = i * q.Rows + j;
                var quad = q.Quads[k];
                if (quad.MainMaterial != 0) continue;       // schon bemalt (z. B. Stein unter Brücken, Grund im Wasser)
                float rot = float.IsNaN(m.Angle) ? 0 : Rotation(m.Angle, dir);
                int idx = q.BrushMaterials.FindIndex(x => x.Name == m.Mat && MathF.Abs(x.Rotation - rot) < 0.05f);
                if (idx < 0)
                {
                    if (q.BrushMaterials.Count < 16)
                    {
                        q.BrushMaterials.Add(new Material(m.Mat, rot));
                        idx = q.BrushMaterials.Count - 1;
                    }
                    else idx = q.BrushMaterials.FindIndex(x => x.Name == m.Mat);   // voll: gleiches Material, andere Drehung
                }
                if (idx <= 0) continue;
                quad.MainMaterial = (Nibble)idx;
                q.Quads[k] = quad;
                painted++;
                if (rot != 0) rotated++;
                byKind[m.Kind] = byKind.GetValueOrDefault(m.Kind) + 1;
            }
        return true;
    }
}
