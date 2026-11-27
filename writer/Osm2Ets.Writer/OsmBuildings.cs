// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using System.Text.Json;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>Gebäudekatalog (buildings.jsonc): SCS-Modelle mit Art, Land und Bounding-Box.</summary>
public sealed class BuildingCatalog
{
    /// <summary>Lokale Achse, die zur Straße zeigen soll (Vorderseite): "-z" oder "+z".</summary>
    public string Front { get; set; } = "-z";
    public List<BuildingModel> Models { get; set; } = [];

    public static BuildingCatalog Load(string path)
    {
        var cat = JsonSerializer.Deserialize<BuildingCatalog>(File.ReadAllText(path), IntermediateFile.JsonOptions)
                  ?? throw new InvalidDataException($"{path}: leer");
        if (cat.Front is not ("-z" or "+z")) throw new InvalidDataException($"{path}: front muss \"-z\" oder \"+z\" sein");
        foreach (var m in cat.Models)
        {
            if (!TruckLib.Token.IsValidToken(m.Token) || m.Min.Length != 3 || m.Max.Length != 3 || m.SizeX <= 0 || m.SizeY <= 0 || m.SizeZ <= 0)
                throw new InvalidDataException($"{path}: Modell '{m.Token}' ungültig");
        }
        return cat;
    }
}

public sealed class BuildingModel
{
    public string Token { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Country { get; set; } = "";
    public float[] Min { get; set; } = [0, 0, 0];
    public float[] Max { get; set; } = [0, 0, 0];
    public string Model { get; set; } = "";
    public string Look { get; set; } = "default";
    public string Variant { get; set; } = "default";
    public float SizeX => Max[0] - Min[0];
    public float SizeY => Max[1] - Min[1];
    public float SizeZ => Max[2] - Min[2];
    public bool LongIsX => SizeX >= SizeZ;
    public float Long => MathF.Max(SizeX, SizeZ);
    public float Short => MathF.Min(SizeX, SizeZ);
}

/// <summary>
/// Gebäude aus OSM als SCS-Modelle (Model-Items). Je Gebäude das Modell seiner Art (sonst einer
/// verwandten), dessen Grundfläche am besten passt: je Achse höchstens ±15 % skaliert, deutsche Modelle
/// bevorzugt, unter gleich guten Modellen nach OSM-ID gestreut. Lange Hallen und Reihenhäuser werden aus
/// mehreren gleichen Modellen längs zusammengesetzt. Die Längsachse folgt dem OSM-Rechteck, die Vorderseite
/// (Katalog: front) zeigt zur nächsten Straße (Zwischenformat: street; sonst die nächste Fahrbahn der Karte).
/// Höhe: tiefster Geländepunkt unter dem Gebäude, am Hang angehoben, soweit der Sockel reicht.
/// Weggelassen: kein passendes Modell, Fahrbahn oder Prefab zu nah, Gelände darunter zu steil.
/// Aus: OSM2ETS_NO_BUILDINGS=1.
/// </summary>
internal static class OsmBuildings
{
    public static bool Enabled = Environment.GetEnvironmentVariable("OSM2ETS_NO_BUILDINGS") != "1";
    static readonly bool Debug = Environment.GetEnvironmentVariable("OSM2ETS_BUILDING_DEBUG") == "1";
    public const float MaxScale = 1.15f;     // je Achse höchstens ±15 %
    /// <summary>Aufschlag je Land: Jedes passende deutsche Modell (Kosten aus der Skalierung höchstens
    /// 2 · ln 1,15 ≈ 0,28) schlägt jedes ausländische; danach Österreich, Schweiz, ohne Land, Benelux.</summary>
    static float CountryCost(string c) => c switch { "de" => 0f, "at" => 0.30f, "ch" => 0.32f, "-" or "" => 0.35f, "lux" or "be" or "nl" => 0.40f, _ => 0.45f };
    /// <summary>Aufschlag für eine verwandte Art – höher als jeder Länderaufschlag: lieber ein Wohnhaus aus
    /// Luxemburg als ein deutscher Wohnblock.</summary>
    public const float OtherKindCost = 0.5f;
    public const float VarietyCost = 0.15f;  // so viel schlechter darf ein Modell zur Abwechslung sein
    public const float RoadGap = 2f;         // Abstand zum Fahrbahnrand (Modellbreite)
    public const float BankDist = 20f;       // näher am Fahrbahnrand nur, wenn die Straße etwa auf
    public const float BankHeight = 2f;      //   Geländehöhe liegt (sonst steht das Haus in der Böschung)
    public const float PrefabGap = 8f;       // Abstand zu Prefab-Spurpunkten
    public const float MaxBuried = 3f;       // so tief darf die Bergseite im Gelände stecken
    public const int MaxTiles = 8;

    static readonly Dictionary<string, string[]> Related = new()
    {
        ["house"] = ["apartments"], ["apartments"] = ["commercial", "house"], ["garage"] = ["shed"],
        ["shed"] = ["garage"], ["farm"] = ["industrial", "house"], ["retail"] = ["commercial", "industrial"],
        ["commercial"] = ["apartments", "industrial"], ["industrial"] = ["commercial"], ["church"] = ["commercial"],
        ["silo"] = ["industrial"],
    };
    static readonly HashSet<string> Tileable = ["industrial", "farm", "retail", "garage", "house", "apartments", "commercial"];

    public sealed record Result(int Placed, int Items, int Tiled, int NoModel, int OnRoad, int OnPrefab, int Steep,
                                Dictionary<string, int> ByKind, Dictionary<string, int> ByCountry);

    public sealed record Choice(BuildingModel Model, int Tiles, float ScaleLong, float ScaleShort, float ScaleY, float Cost);

    /// <summary>Bestes Modell für ein Rechteck L × B (Höhe h oder null) der Art kind; null, wenn keins passt.</summary>
    public static Choice? Choose(BuildingCatalog cat, string kind, double length, double width, double? height, string seed)
    {
        var all = new List<Choice>();
        foreach (var (k, extra) in new[] { (kind, 0f) }.Concat((Related.GetValueOrDefault(kind) ?? []).Select(r => (r, OtherKindCost))))
        {
            foreach (var m in cat.Models.Where(m => m.Kind == k))
            {
                int n = 1;
                if (length > m.Long * MaxScale && Tileable.Contains(k))
                    n = (int)Math.Ceiling(length / (m.Long * MaxScale));
                if (n > MaxTiles) continue;
                float sl = (float)(length / n / m.Long), sw = (float)(width / m.Short);
                if (!In(sl) || !In(sw)) continue;
                float sy = height is double h ? Math.Clamp((float)(h / m.SizeY), 1 / MaxScale, MaxScale) : MathF.Sqrt(sl * sw);
                float cost = MathF.Abs(MathF.Log(sl)) + MathF.Abs(MathF.Log(sw)) + extra + (n - 1) * 0.05f
                           + CountryCost(m.Country)
                           + (height is double h2 ? 0.5f * MathF.Abs(MathF.Log((float)(h2 / m.SizeY))) : 0);
                all.Add(new Choice(m, n, sl, sw, sy, cost));
            }
        }
        if (all.Count == 0) return null;
        var top = all.MinBy(c => c.Cost)!;
        // zur Abwechslung fast gleich gute Modelle – aber keins aus einem weniger bevorzugten Land
        var good = all.Where(c => c.Cost <= top.Cost + VarietyCost && CountryCost(c.Model.Country) <= CountryCost(top.Model.Country))
                      .OrderBy(c => c.Model.Token, StringComparer.Ordinal).ToList();
        return good[(int)(StableHash(seed) % (uint)good.Count)];
    }

    static bool In(float s) => s >= 1 / MaxScale - 1e-4f && s <= MaxScale + 1e-4f;

    static uint StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }

    /// <summary>Drehung, die die lokale −z-Achse auf <paramref name="d"/> legt (wie TruckLib-Knoten).</summary>
    public static Quaternion Facing(Vector2 d)
    {
        float yaw = MathF.Atan2(-d.Y, d.X) - MathF.PI / 2;
        return Quaternion.CreateFromYawPitchRoll(yaw, 0, 0);
    }

    public static Result Apply(Map map, IntermediateFile input, List<RoadEntry> roads,
                               IReadOnlyDictionary<RoadEntry, ResolvedStyle[]> styles, BuildingCatalog cat,
                               IReadOnlyList<Vector2> prefabLanes, Func<float, float, float>? terrain)
    {
        var byKind = new Dictionary<string, int>();
        var byCountry = new Dictionary<string, int>();
        int placed = 0, items = 0, tiled = 0, noModel = 0, onRoad = 0, onPrefab = 0, steep = 0;
        if (input.Buildings.Count == 0) return new(0, 0, 0, 0, 0, 0, 0, byKind, byCountry);

        // Fahrbahnen (alle Straßen und Gleise) und Prefab-Spurpunkte in Raster
        const float cell = 50f;
        var segs = new Dictionary<(int, int), List<(Vector3 A, Vector3 B, float Half)>>();
        foreach (var r in roads)
        {
            if (!styles.TryGetValue(r, out var st) || r.Points.Count < 2) continue;
            for (int i = 0; i + 1 < r.Points.Count; i++)
            {
                var s = st[Math.Min(i, st.Length - 1)];
                var a = MapWriter.ToVector(r.Points[i]); var b = MapWriter.ToVector(r.Points[i + 1]);
                float half = s.Width / 2 + MathF.Abs(s.ModelCenter) + MathF.Abs(s.Offset);
                float pad = half + BankDist;
                for (int gx = (int)MathF.Floor((MathF.Min(a.X, b.X) - pad) / cell); gx <= (int)MathF.Floor((MathF.Max(a.X, b.X) + pad) / cell); gx++)
                    for (int gz = (int)MathF.Floor((MathF.Min(a.Z, b.Z) - pad) / cell); gz <= (int)MathF.Floor((MathF.Max(a.Z, b.Z) + pad) / cell); gz++)
                    {
                        if (!segs.TryGetValue((gx, gz), out var l)) segs[(gx, gz)] = l = [];
                        l.Add((a, b, half));
                    }
            }
        }
        var lanes = new Dictionary<(int, int), List<Vector2>>();
        foreach (var p in prefabLanes)
        {
            var key = ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell));
            if (!lanes.TryGetValue(key, out var l)) lanes[key] = l = [];
            l.Add(p);
        }
        // Prefab-Flächen (Kreis um die Knoten)
        var prefabDisks = map.MapItems.Values.OfType<Prefab>().Select(pf =>
        {
            var c = pf.Nodes.Aggregate(Vector2.Zero, (s, n) => s + new Vector2(n.Position.X, n.Position.Z)) / pf.Nodes.Count;
            float rad = pf.Nodes.Max(n => Vector2.Distance(c, new Vector2(n.Position.X, n.Position.Z)));
            return (C: c, R: rad);
        }).ToList();

        // nächster Fahrbahnpunkt: (Abstand zum Rand, Punkt, Höhe der Fahrbahn)
        (float Edge, Vector2 P, float Y) Nearest(Vector2 q)
        {
            (float, Vector2, float) best = (float.PositiveInfinity, q, float.NaN);
            if (!segs.TryGetValue(((int)MathF.Floor(q.X / cell), (int)MathF.Floor(q.Y / cell)), out var l)) return best;
            foreach (var (a, b, half) in l)
            {
                var a2 = new Vector2(a.X, a.Z); var ab = new Vector2(b.X, b.Z) - a2;
                float l2 = ab.LengthSquared();
                float t = l2 < 1e-8f ? 0 : Math.Clamp(Vector2.Dot(q - a2, ab) / l2, 0, 1);
                var p = a2 + t * ab;
                float e = Vector2.Distance(q, p) - half;
                if (e < best.Item1) best = (e, p, a.Y + t * (b.Y - a.Y));
            }
            return best;
        }
        bool NearLane(Vector2 q)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (lanes.TryGetValue(((int)MathF.Floor(q.X / cell) + dx, (int)MathF.Floor(q.Y / cell) + dz), out var l))
                        foreach (var p in l)
                            if (Vector2.DistanceSquared(p, q) < PrefabGap * PrefabGap) return true;
            return false;
        }

        foreach (var b in input.Buildings)
        {
            var center = new Vector2((float)b.Center[0], (float)b.Center[1]);
            double? h = b.Height ?? (b.Levels is double lv ? lv * 3.0 + 2.0 : null);
            var ch = Choose(cat, b.Kind, b.Length, b.Width, h, b.Osm);
            if (ch is null) { noModel++; if (Debug) Console.Error.WriteLine($"BNOMODEL {b.Kind} {b.Length:0.0} {b.Width:0.0} {b.Osm}"); continue; }
            var m = ch.Model;
            float ang = (float)(b.Angle * Math.PI / 180);
            var u = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
            var nrm = new Vector2(-u.Y, u.X);
            // Vorderseite zur nächsten Straße: aus OSM (auch Straßen außerhalb der Karte), sonst die nächste
            // Fahrbahn der Karte
            var near = Nearest(center);
            var toRoad = b.Street is { Length: 2 } st ? new Vector2((float)st[0], (float)st[1])
                       : float.IsFinite(near.Edge) ? near.P - center : nrm;
            var axis = m.LongIsX ? nrm : u;   // Richtung der lokalen z-Achse in der Welt (bis aufs Vorzeichen)
            var front = Vector2.Dot(axis, toRoad) >= 0 ? axis : -axis;
            var negZ = cat.Front == "-z" ? front : -front;
            var rot = Facing(negZ);
            float sx = m.LongIsX ? ch.ScaleLong : ch.ScaleShort, sz = m.LongIsX ? ch.ScaleShort : ch.ScaleLong;
            var scale = new Vector3(sx, ch.ScaleY, sz);
            float tileLen = (float)(b.Length / ch.Tiles);
            // Lage prüfen: je Kachel Ecken, Kantenmitten und Mitte
            var tiles = new List<(Vector2 C, float H)>();
            bool skip = false;
            float hmin = float.PositiveInfinity, hmax = float.NegativeInfinity;
            for (int i = 0; i < ch.Tiles && !skip; i++)
            {
                var tc = center + u * (float)((i + 0.5) * tileLen - b.Length / 2);
                foreach (var (fl, fw) in new[] { (0f, 0f), (-0.5f, -0.5f), (0.5f, -0.5f), (0.5f, 0.5f), (-0.5f, 0.5f), (0f, -0.5f), (0f, 0.5f), (-0.5f, 0f), (0.5f, 0f) })
                {
                    var q = tc + u * (fl * tileLen) + nrm * (fw * (float)b.Width);
                    var nq = Nearest(q);
                    float hq = terrain?.Invoke(q.X, q.Y) ?? float.NaN;
                    if (float.IsNaN(hq)) hq = float.IsNaN(nq.Y) ? 0 : nq.Y;
                    if (nq.Edge < RoadGap || (nq.Edge < BankDist && MathF.Abs(nq.Y - hq) > BankHeight)) { onRoad++; skip = true; break; }
                    if (NearLane(q) || prefabDisks.Any(d => Vector2.DistanceSquared(d.C, q) < d.R * d.R)) { onPrefab++; skip = true; break; }
                    hmin = MathF.Min(hmin, hq); hmax = MathF.Max(hmax, hq);
                }
                tiles.Add((tc, 0));
            }
            if (skip) continue;
            if (Debug) Console.Error.WriteLine($"BTILT {hmax - hmin:0.00} {b.Kind} {m.Min[1]:0.00} {b.Osm}");
            // Hanglage: das Modell so weit anheben, wie sein Sockel unter y = 0 reicht (höchstens um den
            // halben Höhenunterschied) – die Talseite steht dann auf dem Sockel, die Bergseite steckt
            // um den Rest im Gelände
            float lift = MathF.Min((hmax - hmin) / 2, MathF.Max(0, -m.Min[1] * ch.ScaleY));
            if (hmax - hmin - lift > MaxBuried) { steep++; continue; }
            var cLocal = new Vector3((m.Min[0] + m.Max[0]) / 2 * sx, 0, (m.Min[2] + m.Max[2]) / 2 * sz);
            var off = Vector3.Transform(cLocal, rot);
            foreach (var (tc, _) in tiles)
            {
                var pos = new Vector3(tc.X - off.X, hmin + lift - 0.1f, tc.Y - off.Z);
                var item = Model.Add(map, pos, m.Token, m.Variant, m.Look);
                item.Node.Rotation = rot;
                item.Scale = scale;
                item.ViewDistance = (ushort)(b.Length > 25 || m.SizeY * ch.ScaleY > 15 ? 900 : 600);
                items++;
            }
            placed++;
            if (ch.Tiles > 1) tiled++;
            byKind[m.Kind] = byKind.GetValueOrDefault(m.Kind) + 1;
            byCountry[m.Country] = byCountry.GetValueOrDefault(m.Country) + 1;
        }
        return new(placed, items, tiled, noModel, onRoad, onPrefab, steep, byKind, byCountry);
    }
}
