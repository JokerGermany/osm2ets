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
    ("L5: Feldweg Geheimstraße, Fußweg und Parkplatzgasse nicht im Navi", MinorWaysHiddenFromNavigation),
    ("L5: Privatstraße behält ihren Typ, ist aber nicht im Navi", RestrictedWaysHidden),
    ("Gelände: Mittelstreifen endet vor der Gegenfahrbahn (asymmetrische Modellränder)", MedianTerrainStopsAtOtherCarriageway),
    ("Gelände: zwischen zwei unterschiedlich hohen Fahrbahnen lückenlose Rampe, nie im fremden Modell", GapBetweenRoadsIsRamped),
    ("Gelände: schmale Seite erreicht am Rand die Rasterhöhe (höchstens 45°)", NarrowRoadTerrainReachesGrid),
    ("Prefab-Anschluss: Einlauf übernimmt die Attribute des ersetzten Abschnitts", PrefabLeadKeepsRunAttributes),
    ("Gelände: Brücke darüber verdeckt die Nachbarfahrbahn nicht", BridgeAboveIsNoNeighbour),
    ("Füllgelände: nie über Asphalt oder Straßengelände (Hang neben der Straße)", FillStaysBelowRoadTerrain),
    ("Unterführung: unter der Brücke Höhe der unteren Straße, Böschung zu den Enden, Damm daneben bleibt", UnderpassLowersTerrainUnderBridge),
    ("Brückenkörper: an der tieferen von zwei Autobahnbrücken über eine Straße, Brücke als ein Stück", BridgeBodyOnTwinBridges),
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
    Check(mw.RoadType == "ger13" && mw.TerrainLeft == 4 && mw.TerrainRight == 30, $"Autobahn-Gelände {mw.TerrainLeft}/{mw.TerrainRight}");
    var bridge = cfg.Resolve(MakeRun(0, 1, "motorway", oneway: true, bridge: true));
    Check(bridge.TerrainLeft == 0 && bridge.TerrainRight == 0 && bridge.RoadType == mw.RoadType, "Brücke ohne Geländestreifen, sonst wie die Straße");
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
    // Brücke: gleicher Straßentyp, aber ohne Geländestreifen → Wechsel hinein und heraus
    Check(res.StyleChanges == 2, $"Typwechsel {res.StyleChanges}");
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
    var real = res.Warnings.Where(w => !w.StartsWith("Info:")).ToList();
    Check(real.Count == 0, string.Join("; ", real));
    var roads = map.MapItems.Values.OfType<Road>().ToList();
    // a→b durchgehend; c beginnt eigenständig; b→d verbunden
    var atNode7 = roads.Where(r => r.ForwardNode.Position == Vector3.Zero || r.Node.Position == Vector3.Zero).ToList();
    // Abzweig c ist bis zum Rand der Autobahn gekürzt (keine Überlagerung ohne Prefab)
    var cFirst = roads.Where(r => r.Node.Position.X > 0.5f && r.Node.Position.Z > -45 && r.ForwardNode.Position.X > r.Node.Position.X)
                      .MinBy(r => r.Node.Position.X)!;
    Check(cFirst.BackwardItem is null, "Abzweig bleibt offen");
    Check(cFirst.Node.Position.X > 5, $"Abzweig gekürzt (Anfang x = {cFirst.Node.Position.X:0.0})");
    Check(res.BranchesTrimmed == 1 && res.Overlaps!.Count == 0, $"gekürzt {res.BranchesTrimmed}, Überlagerungen {res.Overlaps!.Count}");
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

static void MinorWaysHiddenFromNavigation()
{
    var track = MakeRoad("track", new[] { new[] { 0.0, 0, 0 }, new[] { 0.0, 0, -100 } }, MakeRun(0, 1, "track"));
    var foot = MakeRoad("foot", new[] { new[] { 100.0, 0, 0 }, new[] { 100.0, 0, -100 } }, MakeRun(0, 1, "footway"));
    var aisleRun = MakeRun(0, 1, "service"); aisleRun.Service = "parking_aisle";
    var aisle = MakeRoad("aisle", new[] { new[] { 200.0, 0, 0 }, new[] { 200.0, 0, -100 } }, aisleRun);
    var street = MakeRoad("street", new[] { new[] { 300.0, 0, 0 }, new[] { 300.0, 0, -100 } }, MakeRun(0, 1, "residential"));
    var input = new IntermediateFile { Format = IntermediateFile.ExpectedFormat, Version = 1,
        Projection = new ProjectionInfo { Scale = 1, VerticalScale = 1 }, Roads = [track, foot, aisle, street] };
    var (map, _) = MapWriter.Build(input, MappingConfig.Load(ShippedMapping()));
    Road At(double x) => map.MapItems.Values.OfType<Road>().Single(r => Math.Abs(r.Node.Position.X - x) < 0.5);
    var t = At(0); var f = At(100); var a = At(200); var s = At(300);
    Check(t.Secret && t.GpsAvoid && !t.AiVehicles, $"Feldweg: secret {t.Secret}, gps_avoid {t.GpsAvoid}, ai {t.AiVehicles}");
    Check(!f.ShowInUiMap && f.GpsAvoid && !f.AiVehicles, $"Fußweg: ui_map {f.ShowInUiMap}, gps_avoid {f.GpsAvoid}");
    Check(!a.ShowInUiMap && a.GpsAvoid, $"Parkplatzgasse: ui_map {a.ShowInUiMap}, gps_avoid {a.GpsAvoid}");
    Check(s.ShowInUiMap && !s.GpsAvoid && !s.Secret, $"Wohnstraße unverändert: ui_map {s.ShowInUiMap}, gps_avoid {s.GpsAvoid}, secret {s.Secret}");
    Check(t.RoadType == "ger37" && f.RoadType == "ger4" && a.RoadType == "ger4", $"Typen {t.RoadType}/{f.RoadType}/{a.RoadType}");
}

static void RestrictedWaysHidden()
{
    var run = MakeRun(0, 1, "residential"); run.Access = "private";
    var priv = MakeRoad("priv", new[] { new[] { 0.0, 0, 0 }, new[] { 0.0, 0, -100 } }, run);
    var trackRun = MakeRun(0, 1, "track"); trackRun.Access = "no";
    var track = MakeRoad("track", new[] { new[] { 100.0, 0, 0 }, new[] { 100.0, 0, -100 } }, trackRun);
    var input = new IntermediateFile { Format = IntermediateFile.ExpectedFormat, Version = 1,
        Projection = new ProjectionInfo { Scale = 1, VerticalScale = 1 }, Roads = [priv, track] };
    var cfg = MappingConfig.Load(ShippedMapping());
    var (map, _) = MapWriter.Build(input, cfg);
    Road At(double x) => map.MapItems.Values.OfType<Road>().Single(r => Math.Abs(r.Node.Position.X - x) < 0.5);
    var p = At(0); var t = At(100);
    var normal = cfg.Resolve(MakeRun(0, 1, "residential"));
    Check(p.RoadType == normal.RoadType, $"Typ {p.RoadType}, erwartet {normal.RoadType}");
    Check(!p.ShowInUiMap && p.GpsAvoid && !p.AiVehicles, $"Privatstraße: ui_map {p.ShowInUiMap}, gps_avoid {p.GpsAvoid}, ai {p.AiVehicles}");
    Check(t.Secret && !t.ShowInUiMap && t.GpsAvoid, $"gesperrter Feldweg: secret {t.Secret}, ui_map {t.ShowInUiMap}");
}


static void MedianTerrainStopsAtOtherCarriageway()
{
    // ger16 („hw 3 lanes one way“): Modell −9,0…+8,0 m um die Straßenlinie (Mitte −0,5 m).
    // Zwei Richtungsfahrbahnen, Linien 22 m bzw. 19 m auseinander: zwischen den linken
    // Modellrändern bleiben 4 m bzw. 1 m Mittelstreifen.
    foreach (var (dist, gap) in new[] { (22.0, 4f), (19.0, 1f) })
    {
        var st = new ResolvedStyle("ger16", 60, 60, RoadResolution.HighPoly, null, null, Width: 17, ModelCenter: -0.5f);
        var east = MakeRoad("e", new[] { new[] { 0.0, 0, 0 }, new[] { 300.0, 0, 0 } }, MakeRun(0, 1, "motorway", oneway: true));
        var west = MakeRoad("w", new[] { new[] { 300.0, 0, -dist }, new[] { 0.0, 0, -dist } }, MakeRun(0, 1, "motorway", oneway: true));
        var styles = new Dictionary<RoadEntry, ResolvedStyle[]> { [east] = [st], [west] = [st] };
        TerrainClearance.Widen(styles);
        TerrainClearance.Apply([east, west], styles);
        foreach (var r in new[] { east, west })
        {
            var s = styles[r][0];
            // linke Seite (Mittelstreifen): reicht nicht bis an den fremden Modellrand …
            Check(s.TerrainLeft <= gap - TerrainClearance.EdgeClear + 1e-3f, $"{r.Id} {dist} m: Mittelstreifen-Gelände {s.TerrainLeft} m bei {gap} m Lücke");
            // … und beide zusammen decken die Lücke (kein Spalt zum Füllgelände)
            Check(2 * s.TerrainLeft >= gap - 1e-3f, $"{r.Id} {dist} m: Lücke nicht gedeckt ({s.TerrainLeft} m je Seite)");
            Check(s.TerrainRight == 60, $"{r.Id}: Außenseite verändert ({s.TerrainRight})");
        }
    }
    TerrainClearance.Share = false; TerrainClearance.SearchRadius = 80f;
}

static void GapBetweenRoadsIsRamped()
{
    // Richtungsfahrbahn A (y 0) nach Osten, Gegenfahrbahn B (y 0,6) nach Westen nördlich davon.
    // ger13 (−4,5…+8,0 m): die linken Ränder zeigen zueinander. Fall 1 parallel (Lücke 11 m),
    // Fall 2 zusammenlaufend (Lücke 21 m → 1 m): je Spalte gekürzt, nie im Modell der anderen.
    var cfg = MappingConfig.Load(ShippedMapping());
    foreach (var converge in new[] { false, true })
    {
        double zB0 = converge ? -30 : -20, zB1 = converge ? -10 : -20;
        var a = MakeRoad("a", new[] { new[] { 0.0, 0, 0 }, new[] { 300.0, 0, 0 } }, MakeRun(0, 1, "motorway", oneway: true));
        var b = MakeRoad("b", new[] { new[] { 300.0, 0.6, zB0 }, new[] { 0.0, 0.6, zB1 } }, MakeRun(0, 1, "motorway", oneway: true));
        int nx = 51, nz = 31;
        var input = new IntermediateFile
        {
            Format = IntermediateFile.ExpectedFormat, Version = 1,
            Projection = new ProjectionInfo { Scale = 1, VerticalScale = 1 },
            Roads = [a, b],
            TerrainGrid = new TerrainGridEntry { Origin = [-100, -100], Cell = 10, Nx = nx, Nz = nz, Heights = new float[nx * nz] },
        };
        var (map, _) = MapWriter.Build(input, cfg);
        var roads = map.MapItems.Values.OfType<Road>().ToList();
        // Linien wie geschrieben (Spurzentrierung verschiebt sie seitlich): z(x) aus den Knoten
        double LineZ(bool isA, double x)
        {
            var pts = roads.Where(r => (r.Node.Position.Y < 0.3f) == isA)
                           .SelectMany(r => new[] { r.Node.Position, r.ForwardNode.Position })
                           .OrderBy(p => p.X).ToList();
            for (int k = 0; k < pts.Count - 1; k++)
                if (x >= pts[k].X && x <= pts[k + 1].X && pts[k + 1].X > pts[k].X)
                    return pts[k].Z + (pts[k + 1].Z - pts[k].Z) * (x - pts[k].X) / (pts[k + 1].X - pts[k].X);
            return double.NaN;
        }
        // zugewandte Modellränder: A (nach Osten) links = −z, B (nach Westen) links = +z, je 4,5 m
        double EdgeA(double x) => LineZ(true, x) - 4.5;
        double EdgeB(double x) => LineZ(false, x) + 4.5;
        int checkedPts = 0;
        foreach (var r in roads)
        {
            bool isA = r.Node.Position.Y < 0.3f;
            foreach (var o in r.Left.Terrain.QuadData.Offsets)
            {
                var p = r.Node.Position + o.Data;
                if (p.X < 20 || p.X > 280) continue;   // Enden auslassen
                double ea = EdgeA(p.X), eb = EdgeB(p.X);
                if (double.IsNaN(ea) || double.IsNaN(eb)) continue;
                // nie im Modell der anderen Fahrbahn (kleine Toleranz für Sehne/Kurve)
                if (isA) Check(p.Z >= eb - 0.05, $"A-Gelände im Modell von B: z {p.Z:0.00} < {eb:0.00} (x {p.X:0})");
                else Check(p.Z <= ea + 0.05, $"B-Gelände im Modell von A: z {p.Z:0.00} > {ea:0.00} (x {p.X:0})");
                // Höhe auf der geraden Rampe zwischen beiden Rändern (0 bei A, 0,6 bei B), 5 cm tiefer
                double gap = ea - eb;
                double od = isA ? p.Z - eb : ea - p.Z;   // Abstand zum fremden Rand
                // volle Rampe gilt bis TerrainFill.GoreFull (8 m) vor der anderen Fahrbahn
                if (gap > 0.5 && od < TerrainFill.GoreFull)
                {
                    double f = Math.Clamp((ea - p.Z) / gap, 0, 1);
                    double want = 0.6 * f - TerrainFill.RoadTerrainDrop;
                    Check(Math.Abs(p.Y - want) < 0.06, $"{(isA ? "A" : "B")}: Höhe {p.Y:0.00} statt {want:0.00} bei z {p.Z:0.00} (x {p.X:0}, Lücke {gap:0.0})");
                }
                checkedPts++;
            }
        }
        Check(checkedPts > 20, $"zu wenige Geländepunkte geprüft ({checkedPts})");
        // Deckung: an jeder Stelle reicht das Gelände von beiden Seiten zusammen über die ganze Lücke
        for (double x = 30; x <= 270; x += 5)
        {
            double reachA = EdgeA(x), reachB = EdgeB(x);
            foreach (var r in roads)
            {
                bool isA = r.Node.Position.Y < 0.3f;
                foreach (var o in r.Left.Terrain.QuadData.Offsets)
                {
                    var p = r.Node.Position + o.Data;
                    if (Math.Abs(p.X - x) > 3) continue;
                    if (isA) reachA = Math.Min(reachA, p.Z); else reachB = Math.Max(reachB, p.Z);
                }
            }
            Check(reachA <= reachB + 0.2, $"Lücke ohne Straßengelände bei x {x}: A bis z {reachA:0.00}, B bis z {reachB:0.00} ({(converge ? "zusammenlaufend" : "parallel")})");
        }
    }
}

static void NarrowRoadTerrainReachesGrid()
{
    // Straße (ger1, ±4 m) auf Höhe 0 nach Osten, rechts (+z) Einschnitt: Raster 0 bis z = 5, dann
    // Steigung k. Schmale rechte Seite: der Außenrand muss die Rasterhöhe erreichen (früher lag er
    // fast auf Fahrbahnhöhe und das Füllgelände dahinter höher – Kante), bei steilem Raster
    // höchstens TerrainFill.MaxSlope ab dem flachen Streifen.
    foreach (var (size, k) in new[] { (6f, 0.3f), (4f, 1.5f) })
    {
        int nx = 41, nz = 41;
        var grid = new TerrainGridEntry { Origin = [-100, -100], Cell = 5, Nx = nx, Nz = nz, Heights = new float[nx * nz] };
        for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
                grid.Heights[j * nx + i] = MathF.Max(0, (-100 + j * 5 - 5) * k);
        var map = new Map();
        var rd = Road.Add(map, new Vector3(0, 0, 0), new Vector3(30, 0, 0), "ger1", size, size);
        TerrainFill.Mode = 'D';
        var pa = rd.Node.Position; var pb = rd.ForwardNode.Position;
        int n = TerrainFill.ShapeRoadSide(rd.Right.Terrain, grid, pa, pb, size, +1, 4f, Vector2.UnitX, Vector2.UnitX, null, Vector3.UnitX, Vector3.UnitX, null);
        Check(n > 0, "keine Höhenpunkte");
        var outer = rd.Right.Terrain.QuadData.Offsets.GroupBy(o => o.X).Select(g => g.OrderBy(o => o.Y).Last()).ToList();
        float rows = TerrainFill.NearFlat + TerrainFill.NearBlend;
        float flat = TerrainFill.NearFlat * size / rows;
        foreach (var o in outer)
        {
            var p = pa + o.Data;
            float g = grid.HeightAt(p.X, p.Z);
            float want = MathF.Min(g, TerrainFill.MaxSlope * (size - flat)) - TerrainFill.RoadTerrainDrop;
            Check(MathF.Abs(p.Y - want) < 0.05f, $"Seite {size} m, Steigung {k}: Außenrand {p.Y:0.00} statt {want:0.00} (Raster {g:0.00}) bei x {p.X:0.0}");
        }
    }
}

static void PrefabLeadKeepsRunAttributes()
{
    // Straße 0…90 m, Abschnitte 3–5 Brücke. Ende an einem Prefab-Knoten bei x 95: die Punkte ab
    // x 70 fallen weg, der Einlauf ersetzt die Abschnitte ab Punkt 6 – die waren keine Brücke.
    // Früher erbte der Lauf, der bei Punkt 6 endete (die Brücke), den Einlauf: BT 46 an der A 9
    // ohne Straßengelände bis zum Prefab.
    double[][] Pts() => Enumerable.Range(0, 10).Select(i => new[] { i * 10.0, 0, 0 }).ToArray();
    var r = MakeRoad("t", Pts(), MakeRun(0, 3, "tertiary"), MakeRun(3, 6, "tertiary", bridge: true), MakeRun(6, 9, "tertiary"));
    Interchanges.SetTail(r, new Vector3(95, 0, 5), Vector3.UnitX, 20f);
    int segs = r.Points.Count - 1;
    Check(segs >= 7, $"unerwartete Punktzahl {r.Points.Count}");
    bool Bridge(RoadEntry e, int seg) => e.Runs.First(x => x.From <= seg && seg < x.To).Bridge;
    Check(e2(r), "Läufe decken nicht alle Abschnitte");
    Check(!Bridge(r, segs - 1) && !Bridge(r, segs - 2), "Einlauf am Ende als Brücke markiert");
    Check(Bridge(r, 3) && Bridge(r, 5) && !Bridge(r, 2), "Brücke davor verändert");
    // gespiegelt: Anfang an einem Prefab-Knoten bei x −5, Brücke auf den Abschnitten 3–5
    var h = MakeRoad("h", Pts(), MakeRun(0, 3, "tertiary"), MakeRun(3, 6, "tertiary", bridge: true), MakeRun(6, 9, "tertiary"));
    Interchanges.SetHead(h, new Vector3(-5, 0, 5), Vector3.UnitX, 20f);
    Check(e2(h), "Läufe (Anfang) decken nicht alle Abschnitte");
    Check(!Bridge(h, 0) && !Bridge(h, 1), "Einlauf am Anfang als Brücke markiert");
    int firstBridge = Enumerable.Range(0, h.Points.Count - 1).First(sg => Bridge(h, sg));
    Check(Math.Abs(h.Points[firstBridge][0] - 30) < 1e-6, $"Brücke beginnt bei x {h.Points[firstBridge][0]} statt 30");

    static bool e2(RoadEntry e)
    {
        int n = e.Points.Count - 1;
        for (int sg = 0; sg < n; sg++) if (e.Runs.Count(x => x.From <= sg && sg < x.To) != 1) return false;
        return e.Runs[0].From == 0 && e.Runs[^1].To == n;
    }
}

static void BridgeAboveIsNoNeighbour()
{
    // Fahrbahn A (y 0) und B (y 0,5) parallel 20 m auseinander, dazwischen quer darüber eine
    // Brücke C (y 10). Für das Straßengelände von A ist B der Nachbar, nicht die Brücke darüber.
    var st = new ResolvedStyle("ger1", 60, 60, RoadResolution.HighPoly, null, null, Width: 8);
    var a = MakeRoad("a", new[] { new[] { 0.0, 0, 0 }, new[] { 200.0, 0, 0 } }, MakeRun(0, 1, "primary"));
    var b = MakeRoad("b", new[] { new[] { 0.0, 0.5, 20 }, new[] { 200.0, 0.5, 20 } }, MakeRun(0, 1, "primary"));
    var c = MakeRoad("c", new[] { new[] { 100.0, 10, -50 }, new[] { 100.0, 10, 70 } }, MakeRun(0, 1, "primary", bridge: true));
    var styles = new Dictionary<RoadEntry, ResolvedStyle[]> { [a] = [st], [b] = [st], [c] = [st] };
    var cap = RoadCap.Build([a, b, c], styles, null);
    // Punkt im Zwickel neben A, 1 m neben der Brücke (im Grundriss)
    var (d, hgt, br) = cap.NearestOther(95, 8, a, TerrainFill.GoreDist, 0f);
    Check(!br && MathF.Abs(hgt - 0.5f) < 1e-3f, $"Nachbar ist die Brücke (Höhe {hgt:0.00}, Brücke {br})");
    Check(MathF.Abs(d - 8f) < 0.05f, $"Abstand zu B {d:0.00} statt 8");
    // ohne Bezugshöhe (alte Abfrage) wäre es die Brücke
    var (_, h0, b0) = cap.NearestOther(95, 8, a, TerrainFill.GoreDist);
    Check(b0 && h0 > 5, "Prüfaufbau: ohne Bezugshöhe sollte die Brücke am nächsten sein");
}

static void FillStaysBelowRoadTerrain()
{
    // Straße auf Höhe 0 nach Osten; nördlich (−z) ein Hang (Raster steigt ab 6 m mit 25 %), südlich
    // eben. Kein Füllgelände-Eckpunkt über Asphalt (mind. 0,25 m darunter) oder über dem
    // Straßengelände (mind. TerrainMargin − SettleTol darunter).
    int nx = 61, nz = 41;
    var heights = new float[nx * nz];
    for (int j = 0; j < nz; j++)
        for (int i = 0; i < nx; i++)
        {
            float z = -200 + j * 10, x = -100 + i * 10;
            heights[j * nx + i] = MathF.Max(0, (-z - 6) * 0.25f) + 0.002f * x;
        }
    var road = MakeRoad("r", new[] { new[] { 0.0, 0, 0 }, new[] { 200.0, 0.4, 0 } }, MakeRun(0, 1, "primary"));
    var input = new IntermediateFile
    {
        Format = IntermediateFile.ExpectedFormat, Version = 1,
        Projection = new ProjectionInfo { Scale = 1, VerticalScale = 1 },
        Roads = [road],
        TerrainGrid = new TerrainGridEntry { Origin = [-100, -200], Cell = 10, Nx = nx, Nz = nz, Heights = heights },
    };
    var (map, _) = MapWriter.Build(input, MappingConfig.Load(ShippedMapping()));
    var rd = map.MapItems.Values.OfType<Road>().ToList();
    var fill = map.MapItems.Values.OfType<Terrain>().ToList();
    Check(rd.Count > 0 && fill.Count > 0, "keine Straße oder kein Füllgelände");
    // Straßengelände-Höhe an (x, z) über die Quads der Straßenseiten (je Spalte linear in z)
    float RoadSurface(float x, float z, out bool covered)
    {
        covered = false;
        foreach (var r in rd)
        {
            var pa = r.Node.Position; var pb = r.ForwardNode.Position;
            if (x < pa.X || x > pb.X) continue;
            float t = (x - pa.X) / (pb.X - pa.X), y = pa.Y + (pb.Y - pa.Y) * t;
            if (MathF.Abs(z) <= 4f) { covered = true; return y; }
            foreach (var (side, sign) in new[] { (r.Left.Terrain, -1f), (r.Right.Terrain, 1f) })
            {
                if (side.Size <= 0 || MathF.Sign(z) != MathF.Sign(sign)) continue;
                var cols = side.QuadData.Offsets.GroupBy(o => o.X).OrderBy(g => g.Key).ToList();
                if (cols.Count < 2) continue;
                int ci = Math.Clamp((int)MathF.Floor(t * side.QuadData.Cols), 0, side.QuadData.Cols - 1);
                var prof = side.QuadData.Offsets.Where(o => o.X == ci).Select(o => pa + o.Data).OrderBy(p => MathF.Abs(p.Z)).ToList();
                var prof2 = side.QuadData.Offsets.Where(o => o.X == ci + 1).Select(o => pa + o.Data).OrderBy(p => MathF.Abs(p.Z)).ToList();
                if (prof.Count < 2 || prof2.Count < 2) continue;
                float Lin(List<Vector3> pr)
                {
                    var all = new List<Vector3> { new(pr[0].X, y, sign * 4f) }; all.AddRange(pr);
                    for (int k = 0; k < all.Count - 1; k++)
                        if (MathF.Abs(z) >= MathF.Abs(all[k].Z) && MathF.Abs(z) <= MathF.Abs(all[k + 1].Z))
                        {
                            float u = (MathF.Abs(z) - MathF.Abs(all[k].Z)) / MathF.Max(1e-4f, MathF.Abs(all[k + 1].Z) - MathF.Abs(all[k].Z));
                            return all[k].Y + (all[k + 1].Y - all[k].Y) * u;
                        }
                    return float.NaN;
                }
                float h1 = Lin(prof), h2 = Lin(prof2);
                if (float.IsNaN(h1) || float.IsNaN(h2)) continue;
                float u2 = Math.Clamp((x - prof[0].X) / MathF.Max(1e-3f, prof2[0].X - prof[0].X), 0, 1);
                covered = true;
                return h1 + (h2 - h1) * u2;
            }
        }
        return float.NaN;
    }
    int under = 0, beside = 0;
    foreach (var t in fill)
        foreach (var side in new[] { t.Left.Terrain, t.Right.Terrain })
            foreach (var o in side.QuadData.Offsets)
            {
                var p = t.Node.Position + o.Data;
                if (p.X < 15 || p.X > 185) continue;
                float s = RoadSurface(p.X, p.Z, out bool cov);
                if (!cov || float.IsNaN(s)) continue;
                if (MathF.Abs(p.Z) <= 4f) { Check(p.Y <= s - 0.25f, $"Füllgelände unter Asphalt zu hoch: {p.Y:0.00} bei Fahrbahn {s:0.00} (x {p.X:0.0}, z {p.Z:0.0})"); under++; }
                else { Check(p.Y <= s - CoverCap.TerrainMargin + CoverCap.SettleTol + 0.02f, $"Füllgelände über dem Straßengelände: {p.Y:0.00} bei {s:0.00} (x {p.X:0.0}, z {p.Z:0.0})"); beside++; }
            }
    Check(under > 5 && beside > 5, $"zu wenige Punkte geprüft ({under}/{beside})");
}

static void UnderpassLowersTerrainUnderBridge()
{
    // Untere Straße (y 0) nach Osten bei z 0; darüber quer eine 40 m lange Brücke (y 6, x 100,
    // z −20…20) mit ebenerdigen Zufahrten. Daneben (x 114) eine ebenerdige Straße auf y 6, die vor
    // der unteren Straße endet (wie eine Richtungsfahrbahn ohne eigene Brücke).
    var st = new ResolvedStyle("ger1", 60, 60, RoadResolution.HighPoly, null, null, Width: 8);
    var low = MakeRoad("low", new[] { new[] { 0.0, 0, 0 }, new[] { 200.0, 0, 0 } }, MakeRun(0, 1, "primary"));
    var up = MakeRoad("up", new[] { new[] { 100.0, 6, -150 }, new[] { 100.0, 6, -20 }, new[] { 100.0, 6, 20 }, new[] { 100.0, 6, 150 } },
        MakeRun(0, 1, "primary"), MakeRun(1, 2, "primary", bridge: true), MakeRun(2, 3, "primary"));
    var side = MakeRoad("side", new[] { new[] { 114.0, 6, -150 }, new[] { 114.0, 6, -14 } }, MakeRun(0, 1, "primary"));
    var styles = new Dictionary<RoadEntry, ResolvedStyle[]> { [low] = [st], [up] = [st, st, st], [side] = [st] };
    var u = Underpasses.Build([low, up, side], styles, []);
    Check(u.Count == 1, $"{u.Count} Unterführungen statt 1");
    float floor = -Underpasses.FloorDrop;
    // unter der Brückenmitte (über der unteren Straße): Sohle
    Check(MathF.Abs(u.Cap(102, 0, 6) - floor) < 0.01f, $"Sohle {u.Cap(102, 0, 6):0.00} statt {floor:0.00}");
    // 10 m vom Ende in die Brücke hinein: Böschung (Neigung mindestens Slope)
    float expect = floor + Underpasses.Slope * (10 - 4 - Underpasses.Shoulder);
    Check(MathF.Abs(u.Cap(98, 10, 6) - expect) < 0.05f, $"Böschung {u.Cap(98, 10, 6):0.00} statt {expect:0.00}");
    // neben der Brücke: Kegel
    float cone = floor + Underpasses.Slope * 3;
    Check(MathF.Abs(u.Cap(100 - 4 - Underpasses.SideMargin - 3, 0, 6) - cone) < 0.05f, $"Kegel {u.Cap(91, 0, 6):0.00} statt {cone:0.00}");
    // vor dem Brückenende und weit seitlich: unverändert
    Check(u.Cap(100, -40, 6) == 6 && u.Cap(160, 0, 6) == 6, "außerhalb der Unterführung verändert");
    // Damm der ebenerdigen Straße daneben bleibt (höchstens FloorDrop darunter)
    Check(u.Cap(114, -16, 6) >= 6 - Underpasses.FloorDrop - 1e-3f, $"Damm daneben angeschnitten: {u.Cap(114, -16, 6):0.00}");
    // Brücke über nichts (Tal): keine Unterführung
    var u2 = Underpasses.Build([up], new Dictionary<RoadEntry, ResolvedStyle[]> { [up] = [st, st, st] }, []);
    Check(u2.Count == 0, "Brücke ohne Straße darunter als Unterführung erkannt");
    // ganze Karte: Raster auf Brückenhöhe – unter der Brücke liegt das Füllgelände trotzdem unten
    int nx = 41, nz = 41;
    var heights = Enumerable.Repeat(6f, nx * nz).ToArray();
    var input = new IntermediateFile
    {
        Format = IntermediateFile.ExpectedFormat, Version = 1,
        Projection = new ProjectionInfo { Scale = 1, VerticalScale = 1 },
        Roads = [low, up],
        TerrainGrid = new TerrainGridEntry { Origin = [-100, -200], Cell = 10, Nx = nx, Nz = nz, Heights = heights },
    };
    var (map, _) = MapWriter.Build(input, MappingConfig.Load(ShippedMapping()));
    var near = map.MapItems.Values.OfType<Terrain>()
        .SelectMany(t => new[] { t.Left.Terrain, t.Right.Terrain }.SelectMany(sd => sd.QuadData.Offsets.Select(o => t.Node.Position + o.Data)))
        .Where(p => MathF.Abs(p.X - 100) < 6 && MathF.Abs(p.Z) < 8).ToList();
    Check(near.Count > 0, "kein Füllgelände unter der Brücke");
    Check(near.All(p => p.Y < 1f), $"Füllgelände unter der Brücke bis {near.Max(p => p.Y):0.00} m (Brücke 6 m)");
    // Geländer an beiden Brückenrändern
    var bridgeItems = map.MapItems.Values.OfType<Road>().Where(r => r.Left.Terrain.Size == 0 && r.Right.Terrain.Size == 0).ToList();
    Check(bridgeItems.Count > 0 && bridgeItems.All(r => r.Left.Railings.Models[0].Model.ToString() == "mod_ger_3" && r.Right.Railings.Models[0].Model.ToString() == "mod_ger_3"),
        "Brücke ohne Geländer");
}

static void BridgeBodyOnTwinBridges()
{
    // Zwei Richtungsfahrbahnen (z −10 nach Osten, z +10 nach Westen – Gegenfahrbahn links), je mit
    // 40 m Brücke über eine Straße bei x 100 (y 0); die westliche Fahrbahn liegt 0,5 m höher.
    var low = MakeRoad("low", new[] { new[] { 100.0, 0, -200 }, new[] { 100.0, 0, 200 } }, MakeRun(0, 1, "primary"));
    var east = MakeRoad("east", new[] { new[] { 0.0, 7, 10 }, new[] { 80.0, 7, 10 }, new[] { 100.0, 7, 10 }, new[] { 120.0, 7, 10 }, new[] { 200.0, 7, 10 } },
        MakeRun(0, 1, "motorway", oneway: true), MakeRun(1, 3, "motorway", oneway: true, bridge: true), MakeRun(3, 4, "motorway", oneway: true));
    var west = MakeRoad("west", new[] { new[] { 200.0, 7.5, -10 }, new[] { 121.0, 7.5, -10 }, new[] { 79.0, 7.5, -10 }, new[] { 0.0, 7.5, -10 } },
        MakeRun(0, 1, "motorway", oneway: true), MakeRun(1, 2, "motorway", oneway: true, bridge: true), MakeRun(2, 3, "motorway", oneway: true));
    var warnings = new List<string>();
    var bodies = BridgeBodies.Plan([low, east, west], new BridgeBodySpec { Railing = "dlc_it_8" }, warnings);
    Check(bodies.Count == 1, $"{bodies.Count} Brückenkörper statt 1");
    var b = bodies[0];
    Check(b.Road == east && b.Partner == "west", $"Träger {b.Road.Id} statt der tieferen Fahrbahn east");
    Check(MathF.Abs(b.Offset - 12f) < 0.01f, $"Versatz {b.Offset:0.00} statt 12 (halber Abstand 10 + 2)");
    Check(east.Points.Count == 4 && east.Runs[1].From == 1 && east.Runs[1].To == 2 && east.Runs[2].From == 2 && east.Runs[2].To == 3,
        "Brücke des Trägers nicht zu einem Stück zusammengelegt");
    // ohne Straße darunter kein Körper
    var e2 = MakeRoad("e2", new[] { new[] { 0.0, 7, 10 }, new[] { 80.0, 7, 10 }, new[] { 120.0, 7, 10 }, new[] { 200.0, 7, 10 } },
        MakeRun(0, 1, "motorway", oneway: true), MakeRun(1, 2, "motorway", oneway: true, bridge: true), MakeRun(2, 3, "motorway", oneway: true));
    var w2 = MakeRoad("w2", new[] { new[] { 200.0, 7, -10 }, new[] { 120.0, 7, -10 }, new[] { 80.0, 7, -10 }, new[] { 0.0, 7, -10 } },
        MakeRun(0, 1, "motorway", oneway: true), MakeRun(1, 2, "motorway", oneway: true, bridge: true), MakeRun(2, 3, "motorway", oneway: true));
    Check(BridgeBodies.Plan([e2, w2], new BridgeBodySpec { Railing = "dlc_it_8" }, warnings).Count == 0, "Talbrücke bekam einen Körper");
}
