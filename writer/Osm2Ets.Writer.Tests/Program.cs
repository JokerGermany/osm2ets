// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using System.Text;
using Osm2Ets.Writer;
using TruckLib.HashFs;
using TruckLib.ScsMap;

// Kleiner Testlauf ohne Testframework. Rückgabewert 0 = alles bestanden.
var tests = new List<(string Name, Action Body)>
{
    ("Zuordnung: mitgelieferte mapping.jsonc ist gültig", MappingShippedIsValid),
    ("Zuordnung: Regeln, Brücke, Standard", MappingResolve),
    ("Zuordnung: ungültige Werte werden abgelehnt", MappingRejectsInvalid),
    ("Prüfung: kaputte Straßen werden erkannt", ValidateRejectsBrokenRoads),
    ("Aufbau: Stücke, Typwechsel, Startpunkt, Maßstab", BuildProducesExpectedMap),
    ("Speichern und erneut laden", SaveAndReopen),
    ("Straßentypen aus .scs-Archiv lesen", LookListerReadsArchive),
    ("Regression: Stück mit Mittelpunkt in knotenlosem Sektor bleibt erhalten", SectorWithoutNodeIsSaved),
    ("Auflösung Normal: Geländeraster wird neu berechnet", ResolutionRecalculatesTerrain),
    ("Knoten: geradeaus verbinden, Abzweig offen, Zweirichtung drehen", ConnectsThroughRoads),
};

int failed = 0;
foreach (var (name, body) in tests)
{
    try
    {
        body();
        Console.WriteLine($"ok    {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FEHLER {name}: {ex.GetType().Name}: {ex.Message}");
    }
}
Console.WriteLine(failed == 0 ? $"{tests.Count} Tests bestanden." : $"{failed} von {tests.Count} Tests fehlgeschlagen.");
return failed == 0 ? 0 : 1;

// ---------------------------------------------------------------- Hilfen

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static string ShippedMapping() =>
    Path.Combine(AppContext.BaseDirectory, "mapping.jsonc");

static RunEntry MakeRun(int from, int to, string highway, string role = "core", bool oneway = false,
                    bool bridge = false) =>
    new() { From = from, To = to, Highway = highway, Role = role, Oneway = oneway, Bridge = bridge };

static RoadEntry MakeRoad(string id, double[][] pts, params RunEntry[] runs) =>
    new() { Id = id, Role = runs[0].Role, Points = pts.ToList(), Runs = runs.ToList(),
            LengthM = pts.Zip(pts.Skip(1), (a, b) => Math.Sqrt(Math.Pow(b[0] - a[0], 2) + Math.Pow(b[1] - a[1], 2)
                                                                   + Math.Pow(b[2] - a[2], 2))).Sum() };

static double[][] Line(int n, double step, double y = 0) =>
    Enumerable.Range(0, n).Select(i => new[] { 1000.0, y, 2000.0 - i * step }).ToArray();

static IntermediateFile Sample()
{
    var motorway = MakeRoad("c0p0", Line(6, 100),
        MakeRun(0, 2, "motorway", oneway: true), MakeRun(2, 3, "motorway", oneway: true, bridge: true),
        MakeRun(3, 5, "motorway", oneway: true));
    var b = MakeRoad("c1p0", new[] { new[] { 0.0, 0, 0 }, new[] { 50.0, 0, 0 }, new[] { 100.0, 0, 10 } },
        MakeRun(0, 2, "primary"));
    return new IntermediateFile
    {
        Format = IntermediateFile.ExpectedFormat, Version = 1,
        Projection = new ProjectionInfo { Scale = 1, VerticalScale = 1 },
        Roads = [b, motorway],
    };
}

// ---------------------------------------------------------------- Tests

static void MappingShippedIsValid()
{
    var cfg = MappingConfig.Load(ShippedMapping());
    Check(cfg.Rules.Count >= 4, "zu wenige Regeln");
    Check(cfg.Default.RoadType == "ger1", "Standardtyp");
}

static void MappingResolve()
{
    var cfg = MappingConfig.Load(ShippedMapping());
    var mw = cfg.Resolve(MakeRun(0, 1, "motorway", oneway: true));
    Check(mw.RoadType == "ger13" && mw.TerrainLeft == 4 && mw.TerrainRight == 80, $"Autobahn-Gelände {mw.TerrainLeft}/{mw.TerrainRight}");
    var bridge = cfg.Resolve(MakeRun(0, 1, "motorway", oneway: true, bridge: true));
    Check(bridge.TerrainLeft == mw.TerrainLeft, "Brücke vorläufig mit Gelände wie die Straße");
    Check(bridge.RoadType == mw.RoadType, "Brücke behält Straßentyp");
    var link = cfg.Resolve(MakeRun(0, 1, "motorway_link", role: "link", oneway: true));
    Check(link.TerrainLeft == 10, "Rampe");
    var conn = cfg.Resolve(MakeRun(0, 1, "residential", role: "connector"));
    Check(conn.TerrainLeft == 10, "Anschlussstraße");
    var other = cfg.Resolve(MakeRun(0, 1, "tertiary", role: "core"));
    Check(other.TerrainLeft == 20 && other.Resolution == RoadResolution.HighPoly, "Standard");
    Check(cfg.Resolve(MakeRun(0, 1, "motorway", oneway: true)) == mw, "Wertgleichheit der Stile");
}

static void MappingRejectsInvalid()
{
    string[] bad =
    [
        """{ "default": { "road_type": "GER4" } }""",
        """{ "default": { "road_type": "viel_zu_langer_name" } }""",
        """{ "default": { "road_type": "ger4", "resolution": "Ultra" } }""",
        """{ "default": { "road_type": "ger4" }, "rules": [ { "match": { "ref": "([" } } ] }""",
        """{ "default": { "road_type": "ger4" }, "rules": [ { "road_type": "ger1" } ] }""",
        """{ "default": { } }""",
        """{ "default": { "road_type": "ger4", "terain_left": 3 } }""",
        """{ "default": { "road_type": "ger4" }, "rulez": [] }""",
    ];
    foreach (var json in bad)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        bool threw = false;
        try { MappingConfig.Load(path); }
        catch (InvalidDataException) { threw = true; }
        finally { File.Delete(path); }
        Check(threw, $"nicht abgelehnt: {json}");
    }
}

static void ValidateRejectsBrokenRoads()
{
    var ok = MakeRoad("ok", Line(3, 10), MakeRun(0, 2, "primary"));
    Check(MapWriter.Validate(ok, out _, out _) is null, "gültige Straße abgelehnt");

    var dup = MakeRoad("dup", new[] { new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 0 }, new[] { 10.0, 0, 0 } }, MakeRun(0, 2, "primary"));
    Check(MapWriter.Validate(dup, out _, out _) is not null, "doppelter Punkt nicht erkannt");
    var longSeg = MakeRoad("long", new[] { new[] { 0.0, 0, 0 }, new[] { 1200.0, 0, 0 } }, MakeRun(0, 1, "primary"));
    Check(MapWriter.Validate(longSeg, out _, out _) is not null, "zu langer Abschnitt nicht erkannt");
    var gap = MakeRoad("gap", Line(4, 10), MakeRun(0, 1, "primary"), MakeRun(2, 3, "primary"));
    Check(MapWriter.Validate(gap, out _, out _) is not null, "Lücke in Läufen nicht erkannt");
    var overlap = MakeRoad("ov", Line(4, 10), MakeRun(0, 2, "primary"), MakeRun(1, 3, "primary"));
    Check(MapWriter.Validate(overlap, out _, out _) is not null, "Überlappung nicht erkannt");
    var nan = MakeRoad("nan", new[] { new[] { 0.0, 0, 0 }, new[] { double.NaN, 0, 0 } }, MakeRun(0, 1, "primary"));
    Check(MapWriter.Validate(nan, out _, out _) is not null, "NaN nicht erkannt");
}

static void BuildProducesExpectedMap()
{
    var cfg = MappingConfig.Load(ShippedMapping());
    var input = Sample();
    input.Roads.Add(MakeRoad("broken", new[] { new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 0 } }, MakeRun(0, 1, "primary")));
    var (map, res) = MapWriter.Build(input, cfg);
    Check(res.RoadsWritten == 2 && res.RoadsSkipped == 1, $"geschrieben {res.RoadsWritten}, übersprungen {res.RoadsSkipped}");
    Check(res.Items == 2 + 5, $"Straßenstücke {res.Items}");
    Check(map.MapItems.Values.OfType<Road>().Count() == 7, "Anzahl Road-Items");
    // Brücken haben in der mitgelieferten Zuordnung keinen eigenen Stil mehr
    Check(res.StyleChanges == 0, $"Typwechsel {res.StyleChanges}");
    Check(res.StartRoad == "c0p0", "Startpunkt auf der Autobahn");
    // Abschnitt 4 (nicht Brücke, Nachbarn nicht Brücke), Mitte zwischen z=1600 und z=1500
    Check(res.StartPosition.Y == 0 && Math.Abs(res.StartPosition.Z - 1550) < 0.01, $"Startpunkt: {res.StartPosition}");
    Check(map.NormalScale == 1 && map.CityScale == 1, "Maßstab");
    Check(map.MapItems.Values.OfType<Road>().All(r => r.Left.Terrain.Noise == TerrainNoise.Percent0), "Geländerauschen 0 %");
}

static void SaveAndReopen()
{
    var cfg = MappingConfig.Load(ShippedMapping());
    var (map, _) = MapWriter.Build(Sample(), cfg);
    var dir = Path.Combine(Path.GetTempPath(), "osm2ets-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        map.Save(dir, "testmap", true);
        var sectorDir = Path.Combine(dir, "testmap");
        int bases = Directory.EnumerateFiles(sectorDir, "*.base").Count();
        Check(MapWriter.EnsureLayerFiles(sectorDir) == bases, "je Sektor eine .layer-Datei");
        Check(MapWriter.EnsureLayerFiles(sectorDir) == 0, "zweiter Aufruf legt nichts mehr an");
        foreach (var layer in Directory.EnumerateFiles(sectorDir, "*.layer"))
            Check(new FileInfo(layer).Length == 24, "Größe der leeren .layer-Datei");
        var reopened = Map.Open(Path.Combine(dir, "testmap.mbd"));
        Check(reopened.MapItems.Count == map.MapItems.Count, "Items nach erneutem Laden");
        Check(reopened.Nodes.Count == map.Nodes.Count, "Knoten nach erneutem Laden");
        Check(reopened.MapItems.Values.OfType<Road>().All(r => r.RoadType.ToString() is "ger13" or "ger1"), "Straßentyp");
        Check(Vector3.Distance(reopened.StartPosition, map.StartPosition) < 0.01f, "Startpunkt");
    }
    finally
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}

static void SectorWithoutNodeIsSaved()
{
    // Vom Review gefunden: TruckLib legt beim Anhängen keinen Sektor für den
    // Mittelpunkt an. Mittelpunkt des 2. Stücks (80070, 128070) liegt in
    // Sektor (20, 32), beide Knoten aber in (19, 32) bzw. (20, 31).
    var cfg = MappingConfig.Load(ShippedMapping());
    var input = new IntermediateFile
    {
        Format = IntermediateFile.ExpectedFormat, Version = 1,
        Projection = new ProjectionInfo { Scale = 1, VerticalScale = 1 },
        Roads = [MakeRoad("c0p0", new[] { new[] { 79990.0, 0, 128450 }, new[] { 79990.0, 0, 128150 },
                                          new[] { 80150.0, 0, 127990 } }, MakeRun(0, 2, "motorway", oneway: true))],
    };
    var (map, res) = MapWriter.Build(input, cfg);
    Check(res.Items == 2, "zwei Stücke gebaut");
    var dir = Path.Combine(Path.GetTempPath(), "osm2ets-sector-" + Guid.NewGuid().ToString("N"));
    try
    {
        map.Save(dir, "sectest", true);
        var reopened = Map.Open(Path.Combine(dir, "sectest.mbd"));
        Check(reopened.MapItems.Count == 2, $"nach erneutem Laden {reopened.MapItems.Count} statt 2 Stücke");
    }
    finally
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}

static void ResolutionRecalculatesTerrain()
{
    static int Quads(string resolution)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, $$"""{ "default": { "road_type": "ger4", "terrain_left": 20, "terrain_right": 20, "resolution": "{{resolution}}" } }""");
        try
        {
            var cfg = MappingConfig.Load(path);
            var input = new IntermediateFile
            {
                Format = IntermediateFile.ExpectedFormat, Version = 1,
                Projection = new ProjectionInfo { Scale = 1, VerticalScale = 1 },
                Roads = [MakeRoad("r", Line(3, 150), MakeRun(0, 2, "primary"))],
            };
            var (map, _) = MapWriter.Build(input, cfg);
            return map.MapItems.Values.OfType<Road>()
                .Sum(r => r.Left.Terrain.QuadData.Rows * r.Left.Terrain.QuadData.Cols);
        }
        finally { File.Delete(path); }
    }
    int high = Quads("HighPoly"), normal = Quads("Normal");
    Check(normal > 0 && normal < high, $"Normal sollte weniger Geländequads haben als HighPoly ({normal} vs. {high})");
}

static void ConnectsThroughRoads()
{
    // Knoten 7 bei (0,0,0): A kommt von Süden (z>0) an, B fährt nach Norden weiter,
    // C zweigt nach Osten ab; D ist zweirichtig und falsch herum (endet an Knoten 9).
    var a = MakeRoad("a", new[] { new[] { 0.0, 0, 200 }, new[] { 0.0, 0, 100 }, new[] { 0.0, 0, 0 } }, MakeRun(0, 2, "motorway", oneway: true));
    var b = MakeRoad("b", new[] { new[] { 0.0, 0, 0 }, new[] { 0.0, 0, -100 }, new[] { 0.0, 0, -200 } }, MakeRun(0, 2, "motorway", oneway: true));
    var c = MakeRoad("c", new[] { new[] { 0.0, 0, 0 }, new[] { 60.0, 0, -40 }, new[] { 150.0, 0, -60 } }, MakeRun(0, 2, "motorway_link", role: "link", oneway: true));
    var d = MakeRoad("d", new[] { new[] { 0.0, 0, -500 }, new[] { 0.0, 0, -350 }, new[] { 0.0, 0, -200 } }, MakeRun(0, 2, "primary"));
    a.End = new EndInfo { Kind = "node", OsmNode = 7 };
    b.Start = new EndInfo { Kind = "node", OsmNode = 7 }; b.End = new EndInfo { Kind = "node", OsmNode = 9 };
    c.Start = new EndInfo { Kind = "node", OsmNode = 7 };
    d.End = new EndInfo { Kind = "node", OsmNode = 9 };
    var input = new IntermediateFile { Format = IntermediateFile.ExpectedFormat, Version = 1,
        Projection = new ProjectionInfo { Scale = 1, VerticalScale = 1 }, Roads = [a, b, c, d] };
    var (map, res) = MapWriter.Build(input, MappingConfig.Load(ShippedMapping()));
    Check(res.Connections == 2 && res.Reversed == 1, $"Verbindungen {res.Connections}, gedreht {res.Reversed}");
    Check(res.Warnings.Count == 0, string.Join("; ", res.Warnings));
    var roads = map.MapItems.Values.OfType<Road>().ToList();
    // a→b durchgehend; c beginnt eigenständig; b→d verbunden
    var atNode7 = roads.Where(r => r.ForwardNode.Position == Vector3.Zero || r.Node.Position == Vector3.Zero).ToList();
    var cFirst = roads.Single(r => r.Node.Position == Vector3.Zero && r.ForwardNode.Position.X > 1);
    Check(cFirst.BackwardItem is null, "Abzweig bleibt offen");
    var bFirst = roads.Single(r => r.Node.Position == Vector3.Zero && r.ForwardNode.Position.X == 0);
    Check(bFirst.BackwardItem is Road, "Geradeaus verbunden");
    var dir = MapWriter.Validate(d, out _, out _);
    Check(dir is null && d.Points[0][2] == -200, "Zweirichtungsstraße umgedreht");
    var dFirst = roads.Single(r => Math.Abs(r.Node.Position.Z + 200) < 0.01f && r.ForwardNode.Position.Z < -200);
    Check(dFirst.BackwardItem is Road, "b→d verbunden");
    var dir2 = Path.Combine(Path.GetTempPath(), "osm2ets-conn-" + Guid.NewGuid().ToString("N"));
    try
    {
        map.Save(dir2, "conn", true);
        var re = Map.Open(Path.Combine(dir2, "conn.mbd"));
        Check(re.MapItems.Count == map.MapItems.Count && re.Nodes.Count == map.Nodes.Count, "Speichern/Laden");
    }
    finally { if (Directory.Exists(dir2)) Directory.Delete(dir2, true); }
}

static void LookListerReadsArchive()
{
    var dir = Path.Combine(Path.GetTempPath(), "osm2ets-looks-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        const string sii = """
            SiiNunit
            {
            road_look : road.ger4 {
             name: "ger 1+1 test"
             road_size: 9.5
            }
            road_look : road.tmpl_test {
             name: "Vorlage Test"
            }
            }
            """;
        var writer = new HashFsV2Writer();
        writer.Add(Encoding.UTF8.GetBytes(sii), "/def/world/road_look.template.sii");
        writer.Add(Encoding.UTF8.GetBytes("SiiNunit\n{\n}\n"), "/def/world/other.sii");
        writer.Save(Path.Combine(dir, "def.scs"));
        File.WriteAllText(Path.Combine(dir, "kaputt.scs"), "kein Archiv");

        var warnings = new List<string>();
        var looks = LookLister.List(dir, null, false, warnings);
        Check(looks.Count == 2, $"2 Straßentypen erwartet, gefunden {looks.Count}");
        Check(looks.Any(l => l.TokenCandidate == "ger4" && l.ValidToken && l.Summary.Contains("road_size")), "ger4");
        Check(looks.Any(l => l.TokenCandidate == "tmpl_test"), "tmpl_test");
        Check(warnings.Any(w => w.Contains("kaputt.scs")), "Warnung für kaputtes Archiv fehlt");
        var filtered = LookLister.List(dir, "vorlage", false, []);
        Check(filtered.Count == 1 && filtered[0].TokenCandidate == "tmpl_test", "Filter");
    }
    finally
    {
        Directory.Delete(dir, true);
    }
}
