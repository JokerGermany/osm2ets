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
    ("Brückenkörper je Straßentyp: gerade Brücke ein Stück, Kuppe mehrere Stücke, Rampe rechts", BridgeBodySingle),
    ("Brückenkörper: Brücke über schräg kreuzende Straße wird verlängert, rechtwinklige nicht", BridgeExtendedOverSkewedRoad),
    ("Unterführung: Sohle bei schräger Kreuzung um den Versatz an den Brückenkanten breiter", UnderpassSoleWiderWhenSkewed),
    ("Anschlussstelle: inneres Prefab rückt vom Bauwerk weg (Zusatzspur-Straße darüber)", InterchangeShiftAwayFromStructure),
    ("Anschlussstelle: kurze Endstücke der Zusatzspur-Straße gehören zur Brücke", ExtRoadAbsorbsStubs),
    ("Anschlussstelle: Autobahnbeginn aus zwei Anschlussästen wird Einfahrt", MotorwayStartFromTwoLinks),
    ("Anschlussstelle: zweispurige Ausfahrt von 3 Spuren, zweispurige Rampen", Hw3DoubleExitAndTwoLaneLinks),
    ("Anschlussstelle: Autobahnende an zwei zweispurigen Ästen wird Gabelung 2+2", Split22AtTwoTwoLaneLinks),
    ("Anschlussstelle: vierspurige Strecke zwischen Einmündung 3+2 und Ausfahrt 4→3+1", Lane4MergeAndExit),
    ("Rampen: Zusammenführung 2+1 und Spurende 2→1", LinkJoinAndLaneDrop),
    ("Einmündung mit Abbiegespur: gegenüber Sperrfläche bis zum Übergang 1+1", TurnTHatchedArm),
    ("Einmündung mit Abbiegespur: freier Rechtsabbieger aus der Sperrfläche entfällt", SlipDroppedInHatchedStretch),
    ("Füllgelände am Hang: grobes Quad stößt nicht durch die Prefab-Fahrbahn", SettleKeepsFillBelowPrefab),
    ("Tankstelle an gerader Landstraße: Durchfahrts-Prefab, Geländewege entfallen", FuelInline),
    ("Tankstelle ohne lange Straße: Prefab mit Zufahrt und Einmündung mit Platz für das Kreuzungs-Prefab", FuelSingleAccess),
    ("Wechsel 1+1 → 2+1 ohne Platz für den Übergang: Linie läuft gerade durch, kein S-Bogen", PlainLaneChangeKeepsLine),
    ("Kreuzung mit Fußweg und Feldweg daneben: Wege gelöst, kurzer Arm verbunden, Prefab geplant", MinorWaysDoNotBlockJunction),
    ("Gelände am Prefab-Anschluss: Spurpunkt am Knoten sperrt nur jenseits der Knotenlinie", PrefabLaneBoundAtNode),
    ("Ampel: Profil aus der .ppd und Prefab-Flag „Ampeln“, bleibt nach Speichern erhalten", TrafficSignalSwitchesSemaphoresOn),
    ("Land: Knoten von Straßen und Prefabs ja, Gelände und Gebäude nein", CountryOnNetworkNodesOnly),
    ("Gleis: Schienenvorlage mit KI-Zügen, nicht im Navi, ohne Sperrbaken, Land gesetzt", RailWrittenAsTrack),
    ("Gebäudekatalog: mitgelieferte buildings.jsonc gültig, Haus bekommt deutsches Modell (±15 %)", BuildingCatalogShipped),
    ("Gebäude: Modell auf dem OSM-Rechteck, Vorderseite zur Straße; auf der Fahrbahn weggelassen", BuildingPlacedFacingStreet),
    ("Gebäude: lange Halle aus mehreren gleichen Modellen längs", LongHallTiled),
    ("Gebäude: großer Markt 126 × 92 m aus Stücken längs und quer", WideHallTiled),
    ("Wald: Waldwände am Rand (bis 300 m neben der Straße, auch an Lichtungen), innen leer", ForestWalls),
    ("Wald: Straße durch den Wald – Wände beiderseits, Fahrbahn frei", ForestWallsAlongRoadThroughForest),
    ("Baumreihe: eine Reihe Bäume, an der Fahrbahn unterbrochen", TreeRowAsCurve),
    ("Einzelbaum: Modell nach Gattung/Blattwerk/Höhe, skaliert; aus der Fahrbahn geschoben", SingleTrees),
    ("Geländematerial: überall Gras, Quads in OSM-Flächen nach Art (kleinere Fläche gewinnt)", LandcoverPaint),
    ("Wasser: Abstandsraster, Nachbarbecken schmal, gemeinsame Kante kein Ufer, Becken-Profil, Projektion", WaterIndexRules),
    ("Wasser: Teich und Schwimmbecken – Spiegel unter dem Ufer, Streifen decken die Fläche, Grund abgesenkt und bemalt", WaterPondAndPool),
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

static string ShippedBuildings() =>
    Path.Combine(AppContext.BaseDirectory, "buildings.jsonc");

// Mitte der Modell-Bounding-Box in der Welt (x/z)
static Vector2 ModelCenter(Model m, BuildingModel bm)
{
    var local = new Vector3((bm.Min[0] + bm.Max[0]) / 2 * m.Scale.X, 0, (bm.Min[2] + bm.Max[2]) / 2 * m.Scale.Z);
    var c = m.Node.Position + Vector3.Transform(local, m.Node.Rotation);
    return new Vector2(c.X, c.Z);
}

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

static TruckLib.Models.Ppd.PrefabDescriptor ThreeArmPpd(params string[] semaphoreProfiles)
{
    // Einmündung: drei Knoten 10 m um den Mittelpunkt, Richtung jeweils nach außen
    var ppd = new TruckLib.Models.Ppd.PrefabDescriptor();
    foreach (var (p, d) in new[] { (new Vector3(0, 0, 10), Vector3.UnitZ), (new Vector3(10, 0, 0), Vector3.UnitX),
                                   (new Vector3(-10, 0, 0), -Vector3.UnitX) })
        ppd.Nodes.Add(new TruckLib.Models.Ppd.ControlNode { Position = p, Direction = d });
    foreach (var sp in semaphoreProfiles)
        ppd.Semaphores.Add(new TruckLib.Models.Ppd.Semaphore { Profile = sp });
    return ppd;
}

static void TrafficSignalSwitchesSemaphoresOn()
{
    // Einmündung mit Ampelplätzen und OSM-Ampel am Knoten: Profil der Plätze, Zusatzteil _semaphores und das
    // Prefab-Flag „Ampeln“ (kDOP-Bit 19) – ohne das Flag stellt das Spiel trotz Profil keine Ampeln auf.
    // Zweite Einmündung ohne Ampelplätze: kein Profil, kein Flag, Meldung.
    var map = new Map();
    var ppd = ThreeArmPpd("cr_t1x1t", "cr_t1x1t", "cr_1x1");
    var pf = Prefab.Add(map, new Vector3(100, 0, 100), "ch_14001", ppd);
    var bare = Prefab.Add(map, new Vector3(400, 0, 100), "ch_42010", ThreeArmPpd());
    PrefabFootprint.Reset();
    PrefabFootprint.Register(pf, ppd);
    PrefabFootprint.Register(bare, ThreeArmPpd());
    try
    {
        var at = (Vector3 c) => new AreaEntry { Kind = "traffic_signals", Points = [[c.X, c.Z + 3]] };
        var (set, missing) = TrafficSignals.Apply(map, [at(pf.Nodes[0].Position), at(bare.Nodes[0].Position)]);
        Check(set == 1 && missing.Count == 1, $"gesetzt {set}, gemeldet {missing.Count} (erwartet 1/1)");
        Check(pf.SemaphoreProfile.ToString() == "cr_t1x1t", $"Profil {pf.SemaphoreProfile}");
        Check(pf.AdditionalParts.Any(t => t.ToString() == "_semaphores"), "Zusatzteil _semaphores fehlt");
        Check(ItemFlags.Get(pf, ItemFlags.PrefabSemaphores), $"Flag „Ampeln“ fehlt (Flags {ItemFlags.Bits(pf):X8})");
        Check(ItemFlags.Bits(pf) == 1u << ItemFlags.PrefabSemaphores, $"weitere Flags gesetzt: {ItemFlags.Bits(pf):X8}");
        Check(!ItemFlags.Get(bare, ItemFlags.PrefabSemaphores) && string.IsNullOrEmpty(bare.SemaphoreProfile.ToString()),
              "Prefab ohne Ampelplätze hat Profil oder Flag");
        // Speichern und neu laden: Flag und Profil stehen in der Datei
        var dir = Path.Combine(Path.GetTempPath(), "osm2ets-signals-" + Guid.NewGuid().ToString("N"));
        try
        {
            map.Save(dir, "signals", true);
            var again = Map.Open(Path.Combine(dir, "signals.mbd"));
            var pf2 = (Prefab)again.MapItems[pf.Uid];
            Check(ItemFlags.Get(pf2, ItemFlags.PrefabSemaphores), "Flag „Ampeln“ nach dem Laden weg");
            Check(pf2.SemaphoreProfile.ToString() == "cr_t1x1t", $"Profil nach dem Laden {pf2.SemaphoreProfile}");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    finally { PrefabFootprint.Reset(); }
}

static void RailWrittenAsTrack()
{
    // Gleis (Pseudo-Klasse railway, Rolle rail) neben der Autobahn: eingleisig template99, zweigleisig
    // template98 (lanes = Gleiszahl); nicht auf Karte/Navi, KI-Verkehr (Züge) an, an den abgeschnittenen
    // Enden keine Sperrbaken; Gleisknoten tragen das Land.
    var cfg = MappingConfig.Load(ShippedMapping());
    var input = Sample();
    input.Profile = "de";
    RunEntry Rail(int from, int to, int tracks) => new() { From = from, To = to, Highway = MapWriter.RailClass, Role = "rail", Lanes = tracks };
    var one = MakeRoad("r1", [[1100.0, 0, 2000], [1100.0, 0, 1700], [1100.0, 0, 1400]], Rail(0, 2, 1));
    var two = MakeRoad("r2", [[1200.0, 0, 2000], [1200.0, 0, 1600]], Rail(0, 1, 2));
    foreach (var r in new[] { one, two }) { r.Role = "rail"; r.Start = new EndInfo { Kind = "cut", Open = true }; r.End = new EndInfo { Kind = "cut", Open = true }; }
    input.Roads.AddRange([one, two]);
    var (map, _) = MapWriter.Build(input, cfg);
    var rails = map.MapItems.Values.OfType<Road>().Where(r => r.RoadType.ToString() is "template99" or "template98").ToList();
    Check(rails.Any(r => r.RoadType.ToString() == "template99") && rails.Any(r => r.RoadType.ToString() == "template98"),
          "Gleisstücke: " + string.Join(", ", rails.Select(r => r.RoadType.ToString())));
    Check(rails.All(r => !r.ShowInUiMap && r.GpsAvoid && r.AiVehicles && !r.Secret), "Flags der Gleise");
    Check(!map.MapItems.Values.OfType<Buildings>().Any(), "Sperrbaken an Gleisenden");
    Check(rails.All(r => r.Node.ForwardCountry == 5 && r.ForwardNode.BackwardCountry == 5), "Land an Gleisknoten");
}

static void BuildingCatalogShipped()
{
    var cat = BuildingCatalog.Load(ShippedBuildings());
    var kinds = new HashSet<string> { "house", "apartments", "garage", "shed", "industrial", "commercial", "retail", "farm", "church", "silo" };
    Check(cat.Models.Count > 100, $"nur {cat.Models.Count} Modelle");
    Check(cat.Models.All(m => kinds.Contains(m.Kind)), "unbekannte Art");
    Check(cat.Models.Select(m => (m.Token, m.Variant)).Distinct().Count() == cat.Models.Count, "doppelte Modell/Variante");
    Check(cat.Models.All(m => m.Looks.Count > 0 && m.Variant != ""), "Modell ohne Looks oder Variante");
    foreach (var k in new[] { "house", "apartments", "industrial", "shed" })
        Check(cat.Models.Any(m => m.Kind == k && m.Country == "de"), $"kein deutsches Modell für {k}");
    // gewöhnliches Einfamilienhaus 12 × 9 m: deutsches Modell, ein Stück, je Achse höchstens ±15 %
    var ch = OsmBuildings.Choose(cat, "house", 12, 9, null, "w1");
    Check(ch is not null && ch.Model.Country == "de" && ch.Tiles == 1, $"Haus: {ch?.Model.Token} ({ch?.Model.Country})");
    foreach (var f in new[] { ch!.ScaleLong, ch.ScaleShort, ch.ScaleY })
        Check(f >= 1 / OsmBuildings.MaxScale - 1e-3f && f <= OsmBuildings.MaxScale + 1e-3f, $"Skalierung {f}");
    // gleiche Wahl für dieselbe OSM-ID, Streuung über mehrere IDs (Modell, Variante, Look)
    Check(OsmBuildings.Choose(cat, "house", 12, 9, null, "w1")!.Model == ch.Model, "Wahl nicht stabil");
    var picks = Enumerable.Range(0, 40).Select(i => OsmBuildings.Choose(cat, "house", 12, 9, null, $"w{i}")!.Model).ToList();
    Check(picks.Select(p => p.Token).Distinct().Count() > 1, "keine Abwechslung bei den Modellen");
    Check(picks.Select(p => (p.Token, p.Variant)).Distinct().Count() > picks.Select(p => p.Token).Distinct().Count(), "keine Abwechslung bei den Varianten");
    var looks = Enumerable.Range(0, 40).Select(i => OsmBuildings.LookFor(picks[i], $"w{i}")).ToList();
    Check(looks.Select((l, i) => picks[i].Looks.Contains(l)).All(x => x) && looks.Distinct().Count() > 1, "Looks nicht aus dem Modell oder ohne Abwechslung");
    // Winzige Hütte (2 × 1,5 m): kein Modell passt
    Check(OsmBuildings.Choose(cat, "shed", 2, 1.5, null, "w2") is null, "Hütte 2 × 1,5 m bekam ein Modell");
}

static void BuildingPlacedFacingStreet()
{
    // Autobahn längs z bei x = 1000 (Sample). Haus 40 m östlich, Längsseite längs z, Straße (OSM) im
    // Osten: ein Modell, Bounding-Box-Mitte auf der OSM-Mitte, Längsachse längs z, lokale −z (Katalog:
    // front) nach Osten. Ein zweites Haus quer über der Fahrbahn bleibt weg.
    var cfg = MappingConfig.Load(ShippedMapping());
    var cat = BuildingCatalog.Load(ShippedBuildings());
    var input = Sample();
    input.Buildings.Add(new BuildingEntry { Osm = "w1", Kind = "house", Center = [1040, 1800], Length = 12, Width = 9, Angle = 90, Street = [1, 0] });
    input.Buildings.Add(new BuildingEntry { Osm = "w2", Kind = "house", Center = [1000, 1650], Length = 12, Width = 9, Angle = 0 });
    var (map, _) = MapWriter.Build(input, cfg, buildings: cat);
    var models = map.MapItems.Values.OfType<Model>().ToList();
    Check(models.Count == 1, $"{models.Count} Modelle statt 1");
    var m = models[0];
    var bm = cat.Models.Single(x => x.Token == m.Name.ToString() && x.Variant == m.Variant.ToString());
    Check(bm.Looks.Contains(m.Look.ToString()), $"Look {m.Look} nicht im Modell");
    var c = ModelCenter(m, bm);
    Check(Vector2.Distance(c, new Vector2(1040, 1800)) < 0.05f, $"Mitte {c}");
    var front = Vector3.Transform(cat.Front == "-z" ? -Vector3.UnitZ : Vector3.UnitZ, m.Node.Rotation);
    var longAxis = Vector3.Transform(bm.LongIsX ? Vector3.UnitX : Vector3.UnitZ, m.Node.Rotation);
    Check(MathF.Abs(longAxis.Z) > 0.999f, $"Längsachse {longAxis}");
    Check(bm.LongIsX ? front.X > 0.999f : front.Z * front.Z > 0.998f, $"Vorderseite {front} (Modell {bm.Token})");
    Check(m.Scale.X > 0 && MathF.Abs(m.Scale.X - 1) <= 0.151f && MathF.Abs(m.Scale.Z - 1) <= 0.151f, $"Skalierung {m.Scale}");
}

static void LongHallTiled()
{
    // Halle 150 × 30 m: länger als jedes Hallenmodell (+15 %) – mehrere gleiche Modelle längs, zusammen so
    // lang wie die Halle, alle gleich gedreht, Mitten auf der Längsachse
    var cfg = MappingConfig.Load(ShippedMapping());
    var cat = BuildingCatalog.Load(ShippedBuildings());
    var ch = OsmBuildings.Choose(cat, "industrial", 150, 30, null, "w9");
    Check(ch is not null && ch.Tiles >= 2, $"Halle: {ch?.Model.Token} × {ch?.Tiles}");
    Check(MathF.Abs(ch!.ScaleLong * ch.Model.Long * ch.Tiles - 150) < 0.5f, "Gesamtlänge");
    var input = Sample();
    input.Buildings.Add(new BuildingEntry { Osm = "w9", Kind = "industrial", Center = [1200, 1700], Length = 150, Width = 30, Angle = 90 });
    var (map, _) = MapWriter.Build(input, cfg, buildings: cat);
    var models = map.MapItems.Values.OfType<Model>().ToList();
    Check(models.Count == ch.Tiles * ch.Rows, $"{models.Count} Modelle statt {ch.Tiles} × {ch.Rows}");
    Check(models.All(x => x.Name.ToString() == ch.Model.Token && x.Variant.ToString() == ch.Model.Variant
                          && x.Look == models[0].Look && x.Node.Rotation == models[0].Node.Rotation), "verschiedene Modelle/Varianten/Looks/Drehungen");
    var cs = models.Select(x => ModelCenter(x, ch.Model)).OrderBy(v => v.Y).ToList();
    Check(MathF.Abs(cs.Average(v => v.X) - 1200) < 0.05f && MathF.Abs(cs.Max(v => v.X) - cs.Min(v => v.X) - (30 - 30f / ch.Rows)) < 0.1f, "Reihen quer falsch");
    Check(MathF.Abs(cs[^1].Y - cs[0].Y - (150 - 150f / ch.Tiles)) < 0.1f, "Abstand der äußeren Stücke");
}

static void WideHallTiled()
{
    // breiter als jedes Modell (+15 %): Reihen quer; alle Stücke gleich, zusammen so groß wie der Markt
    var cfg = MappingConfig.Load(ShippedMapping());
    var cat = BuildingCatalog.Load(ShippedBuildings());
    var ch = OsmBuildings.Choose(cat, "retail", 126, 92, null, "w7");
    Check(ch is not null && ch.Rows >= 2, $"Markt: {ch?.Model.Token} {ch?.Tiles} × {ch?.Rows}");
    Check(MathF.Abs(ch!.ScaleShort * ch.Model.Short * ch.Rows - 92) < 0.5f && MathF.Abs(ch.ScaleLong * ch.Model.Long * ch.Tiles - 126) < 0.5f, "Gesamtmaße");
    var input = Sample();
    input.Buildings.Add(new BuildingEntry { Osm = "w7", Kind = "retail", Center = [1300, 1700], Length = 126, Width = 92, Angle = 90 });
    var (map, _) = MapWriter.Build(input, cfg, buildings: cat);
    Check(map.MapItems.Values.OfType<Model>().Count() == ch.Tiles * ch.Rows, "Stückzahl");
    // Wohnhäuser werden nicht quer zusammengesetzt
    Check(OsmBuildings.Choose(cat, "house", 12, 30, null, "w8") is not { Rows: > 1 }, "Haus quer zusammengesetzt");
}

// Kurvenknoten (x, z) aller Curve-Items samt Modell
static List<(Vector2 P, string Model)> CurveNodes(Map map) =>
    map.MapItems.Values.OfType<Curve>()
       .SelectMany(c => new[] { c.Node.Position, c.ForwardNode.Position }.Select(v => (new Vector2(v.X, v.Z), c.Subcurves[0].Model.ToString())))
       .ToList();

static void ForestWalls()
{
    // Nadelwald x 1100…1500, z 1500…1800 neben der Autobahn (Sample, x = 1000, z 1500…2000), Lichtung
    // x 1150…1200, z 1600…1650: Waldwände nur an Rändern bis 300 m neben der Autobahn, Reihen 1,5 … 18,5 m
    // innerhalb, vorn Unterholz, dahinter Fichte/Tanne; das Innere bleibt leer.
    var input = Sample();
    input.Forests.Add(new ForestEntry
    {
        Osm = "w1", Leaf = "needleleaved",
        Outer = [[1100, 1500], [1500, 1500], [1500, 1800], [1100, 1800]],
        Inner = [[[1150, 1600], [1200, 1600], [1200, 1650], [1150, 1650]]],
    });
    var cfg = MappingConfig.Load(ShippedMapping());
    var (map, result) = MapWriter.Build(input, cfg);
    var nodes = CurveNodes(map);
    Check(nodes.Count > 0, "keine Waldwand");
    float Edge(Vector2 p) => new[] { p.X - 1100, 1500 - p.X, p.Y - 1500, 1800 - p.Y }.Min();
    float Hole(Vector2 p) => MathF.Max(MathF.Max(1150 - p.X, p.X - 1200), MathF.Max(1600 - p.Y, p.Y - 1650));
    Check(nodes.All(n => Edge(n.P) >= 0.5f), "Knoten außerhalb des Waldes");
    Check(nodes.All(n => MathF.Min(Edge(n.P), Hole(n.P) > 0 ? Hole(n.P) : 99) <= Forests.RowOffsets[^1] + 0.6f), "Bäume im Waldinneren");
    Check(nodes.All(n => n.P.X <= 1000 + input.ForestNearM + 60), "Waldwand weiter als 300 m von der Straße");
    Check(nodes.Any(n => n.P.X < 1110 && n.Model == Forests.Undergrowth.Model), "kein Unterholz am Westrand");
    Check(nodes.Any(n => n.Model == "3l02v") && nodes.Any(n => n.Model == "3l00j"), "keine Fichten/Tannen");
    Check(nodes.Any(n => Hole(n.P) > 0 && Hole(n.P) < 20), "keine Wand an der Lichtung");
    Check(result.Warnings.Any(w => w.Contains("Info: Wälder: 1 Flächen")), "Info-Zeile fehlt");
}

static void ForestWallsAlongRoadThroughForest()
{
    // Mischwald x 800…1200, z 1500…2100 umschließt die Autobahn: Wände beiderseits der Fahrbahn, nicht auf ihr
    // und nicht näher als Fahrbahnrand + Freihaltezone; weiter als EdgeDepth vom Waldrand.
    var input = Sample();
    input.Forests.Add(new ForestEntry { Osm = "w2", Leaf = "mixed", Outer = [[800, 1450], [1200, 1450], [1200, 2100], [800, 2100]] });
    var cfg = MappingConfig.Load(ShippedMapping());
    var (map, _) = MapWriter.Build(input, cfg);
    var nodes = CurveNodes(map);
    var road = nodes.Where(n => MathF.Abs(n.P.X - 1000) < 100 && n.P.Y > 1550 && n.P.Y < 1950).ToList();
    Check(road.Any(n => n.P.X < 1000) && road.Any(n => n.P.X > 1000), "nicht beiderseits der Straße");
    Check(road.All(n => MathF.Abs(n.P.X - 1000) >= Forests.RoadClear), $"zu nah an der Fahrbahn: {road.Min(n => MathF.Abs(n.P.X - 1000)):0.0} m");
    Check(road.Max(n => MathF.Abs(n.P.X - 1000)) < 60, "Waldwand an der Straße zu tief");
}

static void TreeRowAsCurve()
{
    // Laubbaum-Reihe quer über die Autobahn: eine Reihe Buchen, an der Fahrbahn unterbrochen
    var input = Sample();
    input.TreeRows.Add(new TreeRowEntry { Osm = "w3", Leaf = "broadleaved", Points = [[900, 1700], [1100, 1700]] });
    var cfg = MappingConfig.Load(ShippedMapping());
    var (map, _) = MapWriter.Build(input, cfg);
    var nodes = CurveNodes(map);
    Check(nodes.Count >= 4 && nodes.All(n => n.Model == Forests.TreeRow["broadleaved"].Model && MathF.Abs(n.P.Y - 1700) < 0.01f), "Baumreihe");
    Check(nodes.Any(n => n.P.X < 1000) && nodes.Any(n => n.P.X > 1000) && nodes.All(n => MathF.Abs(n.P.X - 1000) > Forests.RoadClear), "Fahrbahn nicht frei");
}

static void SingleTrees()
{
    // Eiche (genus), Tanne (needleleaved), toter Baum (leafless), 20-m-Laubbaum (Höhe → Eichenmodell, skaliert),
    // Laubbaum ohne Angaben (Buche oder Eiche, ±15 %), Baum mitten auf der Autobahn (weggelassen)
    var input = Sample();
    input.Trees.AddRange([
        new TreeEntry { Osm = "n1", Leaf = "broadleaved", Genus = "Quercus", Pos = [1100, 1700] },
        new TreeEntry { Osm = "n2", Leaf = "needleleaved", Pos = [1120, 1700] },
        new TreeEntry { Osm = "n3", Leaf = "leafless", Pos = [1140, 1700], Height = 8 },
        new TreeEntry { Osm = "n4", Leaf = "broadleaved", Pos = [1160, 1700], Height = 20 },
        new TreeEntry { Osm = "n5", Leaf = "broadleaved", Pos = [1180, 1700] },
        new TreeEntry { Osm = "n6", Leaf = "broadleaved", Pos = [1000, 1700] },
        new TreeEntry { Osm = "n8", Leaf = "broadleaved", Genus = "Fagus", Pos = [1000 - Forests.Index.HalfRoad(MappingConfig.Load(ShippedMapping()).Resolve(input.Roads[1].Runs[0])) - 1, 1800] },
    ]);
    var cfg = MappingConfig.Load(ShippedMapping());
    var (map, result) = MapWriter.Build(input, cfg);
    var models = map.MapItems.Values.OfType<Model>().ToDictionary(m => (int)MathF.Round(m.Node.Position.X), m => m);
    // n6 mitten auf der Autobahn: 10 m reichen nicht – weggelassen; n8 (Buche) 1 m neben dem Fahrbahnrand: verschoben
    Check(models.Count == 6 && !models.ContainsKey(1000), $"{models.Count} Bäume, auf der Fahrbahn: {models.ContainsKey(1000)}");
    var moved = map.MapItems.Values.OfType<Model>().Single(m => MathF.Abs(m.Node.Position.Z - 1800) <= Forests.MaxShift);
    Check(moved.Name.ToString() == "1036" && moved.Node.Position.X < 1000 - 2, $"verschobener Baum {moved.Node.Position} {moved.Name}");
    Check(models[1100].Name.ToString() == "122" && models[1120].Name.ToString() == "ibe_1u25" && models[1140].Name.ToString() == "shr_1z870", "Modellwahl");
    Check(models[1160].Name.ToString() == "122" && MathF.Abs(models[1160].Scale.Y - 20 / 19.58f) < 0.01f, $"20-m-Baum: {models[1160].Name} {models[1160].Scale}");
    Check(models[1180].Name.ToString() is "122" or "1036" && models[1180].Scale.X is >= 0.85f and <= 1.15f, "Laubbaum ohne Angaben");
    Check(models.Values.All(m => m.Look.ToString() == "default" && m.Variant.ToString() == "default" && m.Scale.X == m.Scale.Z), "Look/Variante/Maßstab");
    Check(result.Warnings.Any(w => w.Contains("Einzelbäume: 6 von 7 gesetzt (1 aus dem Weg verschoben")), "Info-Zeile");

    // Eiche 4 m neben dem Fahrbahnrand der Autobahn: ihre Krone (10 m) ragte zu weit über die Fahrbahn – Buche
    var st = cfg.Resolve(input.Roads[1].Runs[0]);
    float half = Forests.Index.HalfRoad(st);
    var input2 = Sample();
    input2.Trees.Add(new TreeEntry { Osm = "n7", Leaf = "broadleaved", Genus = "Quercus", Pos = [1000 + half + 4, 1700] });
    var (map2, _) = MapWriter.Build(input2, cfg);
    var oak = map2.MapItems.Values.OfType<Model>().Single();
    var tm = Forests.TreeModels.First(m => m.Token == oak.Name.ToString());
    float edge = MathF.Abs(oak.Node.Position.X - 1000) - half;
    Check(edge >= tm.Crown * oak.Scale.X - Forests.MaxOverhang - 0.01f, $"Krone ragt zu weit: {tm.Name} {edge:0.0} m");
    Check(Vector2.Distance(new Vector2(oak.Node.Position.X, oak.Node.Position.Z), new Vector2(1000 + half + 4, 1700)) <= Forests.MaxShift + 0.01f, "zu weit verschoben");
}

static void LandcoverPaint()
{
    // Geländestreifen x 0…100 bei z = 0, links (Norden, −z) 40 m, rechts 40 m, 10 × 4 Quads je Seite.
    // Acker x 0…60 über die ganze Breite, darin eine Wiese x 20…40, z −20…0 (kleiner, gewinnt).
    var map = new Map();
    var t = Terrain.Add(map, new Vector3(0, 0, 0), new Vector3(100, 0, 0), "0", 40, 40);
    foreach (var (side, sign) in new[] { (t.Left.Terrain, -1f), (t.Right.Terrain, 1f) })
    {
        var q = side.QuadData;
        q.Cols = 10; q.Rows = 4;
        q.Quads = Enumerable.Range(0, 40).Select(_ => new TerrainQuad()).ToList();
        q.Offsets.Clear();
        for (int i = 0; i <= 10; i++)
            for (int j = 0; j <= 4; j++)
                q.Offsets.Add(new VertexData { X = (ushort)i, Y = (ushort)j, Data = new Vector3(10 * i, 0, sign * 10 * j) });
    }
    var input = new IntermediateFile
    {
        Landcover =
        [
            new LandcoverEntry { Osm = "w1", Kind = "farmland", Outer = [[0, -50], [60, -50], [60, 50], [0, 50]] },
            new LandcoverEntry { Osm = "w2", Kind = "meadow", Outer = [[20, -20], [40, -20], [40, 0], [20, 0]] },
        ],
    };
    var r = Landcover.Apply(map, input);
    var L = t.Left.Terrain.QuadData; var R = t.Right.Terrain.QuadData;
    Check(L.BrushMaterials[0].Name.ToString() == Landcover.Default && R.BrushMaterials[0].Name.ToString() == Landcover.Default, "Grundmaterial");
    string Mat(TerrainQuadData q, int i, int j) => q.BrushMaterials[(int)q.Quads[i * q.Rows + j].MainMaterial].Name.ToString();
    // links: Quad (Spalte 2, Reihe 0) liegt bei x 20…30, z −10…0 → Wiese; (Spalte 2, Reihe 3) z −40…−30 → Acker
    Check(Mat(L, 2, 0) == Landcover.Materials["meadow"] && Mat(L, 2, 1) == Landcover.Materials["meadow"], "Wiese links");
    Check(Mat(L, 2, 3) == Landcover.Materials["farmland"] && Mat(L, 0, 0) == Landcover.Materials["farmland"], "Acker links");
    Check(Mat(L, 7, 0) == Landcover.Default && Mat(R, 9, 3) == Landcover.Default, "außerhalb nicht Gras");
    Check(Mat(R, 2, 0) == Landcover.Materials["farmland"], "rechts Acker, nicht Wiese");
    Check(r.Quads == 6 * 4 * 2 && r.ByKind["meadow"] == 4 && r.ByKind["farmland"] == 44, $"{r.Quads} Quads, Wiese {r.ByKind.GetValueOrDefault("meadow")}");
}

static IEnumerable<(Vector3 C, string Mat, bool NoGrass)> TerrainQuads(Map map)
{
    foreach (var it in map.MapItems.Values)
    {
        var (origin, sides) = it switch
        {
            Road r => (r.Node.Position, new[] { r.Left.Terrain, r.Right.Terrain }),
            Terrain t => (t.Node.Position, new[] { t.Left.Terrain, t.Right.Terrain }),
            _ => (Vector3.Zero, Array.Empty<RoadTerrain>()),
        };
        foreach (var s in sides)
        {
            var q = s.QuadData;
            if (q.Offsets.Count == 0 || q.Quads.Count != q.Cols * q.Rows) continue;
            var at = new Dictionary<(int, int), Vector3>();
            foreach (var o in q.Offsets) at[(o.X, o.Y)] = origin + o.Data;
            for (int i = 0; i < q.Cols; i++)
                for (int j = 0; j < q.Rows; j++)
                    if (at.TryGetValue((i, j), out var a) && at.TryGetValue((i + 1, j), out var b) &&
                        at.TryGetValue((i, j + 1), out var c) && at.TryGetValue((i + 1, j + 1), out var d))
                    {
                        var quad = q.Quads[i * q.Rows + j];
                        yield return ((a + b + c + d) / 4, q.BrushMaterials[(int)quad.MainMaterial].Name.ToString(), quad.NoDetailVegetation);
                    }
        }
    }
}

static void WaterIndexRules()
{
    // A und B mit 3 m Damm dazwischen → beide 1-m-Streifen; C stößt an A (gemeinsame Kante x = 0): dort kein
    // Ufer. D (See) allein → 4-m-Streifen.
    var ix = new Water.Index([
        new WaterEntry { Osm = "a", Kind = "pond", Outer = [[-30, 0], [0, 0], [0, 20], [-30, 20]] },
        new WaterEntry { Osm = "b", Kind = "pond", Outer = [[-30, 23], [0, 23], [0, 40], [-30, 40]] },
        new WaterEntry { Osm = "c", Kind = "pond", Outer = [[0, 0], [30, 0], [30, 20], [0, 20]] },
        new WaterEntry { Osm = "d", Kind = "lake", Outer = [[200, 0], [260, 0], [260, 60], [200, 60]] },
    ]);
    var by = ix.Bodies.ToDictionary(b => b.Osm);
    Check(by["a"].Half == Water.Narrow && by["b"].Half == Water.Narrow && by["d"].Half == Water.Wide, "Streifenbreite");
    Check(by["a"].SignedDistance(-1, 10) > 8.5f, $"gemeinsame Kante zählt als Ufer: {by["a"].SignedDistance(-1, 10):0.0}");
    Check(by["a"].SignedDistance(-15, 1) is > 0.9f and < 1.1f, "Ufer bei z = 0");
    Check(by["d"].SignedDistance(230, -5) is > -5.1f and < -4.9f, "außen 5 m");
    // Spiegel: Gelände h = x/10 → tiefster Uferpunkt bei x = 200
    ix.ComputeLevels((x, z) => x / 10f, [], new());
    float lv = by["d"].Level;
    Check(MathF.Abs(lv - (20 - Water.LevelBelow)) < 1e-3f, $"Spiegel {lv}");
    Check(MathF.Abs(ix.Post(230, 30, 50) - (lv - by["d"].Style.Depth)) < 1e-3f, "Grund in der Mitte");
    Check(MathF.Abs(ix.Post(201, 30, 50) - (lv + Water.ShoreAbove - by["d"].Style.Bank)) < 0.05f, "Böschung 1 m vom Ufer");
    Check(ix.Post(230, -1, 0) >= lv + Water.ShoreAbove - 1e-3f, "außen nicht auf Ufer-Höhe angehoben");
    Check(ix.Post(230, -1, 100) < 100 && ix.Post(230, -1, 100) >= lv + Water.ShoreAbove && ix.Post(230, -30, 100) == 100, "Neigung zum Ufer");
    // Projektion: U-Form, Band über beide Arme → zwei Intervalle
    var u = new[] { new[] { new Vector2(0, 0), new Vector2(30, 0), new Vector2(30, 20), new Vector2(20, 20), new Vector2(20, 5),
                            new Vector2(10, 5), new Vector2(10, 20), new Vector2(0, 20) } };
    var iv = Water.Projection(u, 8, 12);
    Check(iv.Count == 2 && MathF.Abs(iv[0].A) < 1e-3f && MathF.Abs(iv[0].B - 10) < 1e-3f && MathF.Abs(iv[1].A - 20) < 1e-3f && MathF.Abs(iv[1].B - 30) < 1e-3f,
          "Projektion U-Form: " + string.Join(" ", iv));
    // Band über eine schräge Kante: genau der x-Bereich im Band
    var tri = new[] { new[] { new Vector2(0, 0), new Vector2(40, 0), new Vector2(0, 40) } };
    var it = Water.Projection(tri, 10, 14);
    Check(it.Count == 1 && MathF.Abs(it[0].A) < 1e-3f && MathF.Abs(it[0].B - 30) < 1e-3f, "Projektion Dreieck: " + string.Join(" ", it));
}

static void WaterPondAndPool()
{
    // Geländeraster x −100…500, z −200…200 (10 m), Hang h = 0,01·x + 0,02·z; Landstraße bei z = 0 (x 0…200).
    // Teich x 60…100, z 30…60 südlich der Straße (Straßengelände bis 60 m breit → gekürzt); Schwimmbecken
    // x 300…320, z −100…−90; Baum mitten im Teich.
    int nx = 61, nz = 41;
    static float H(float x, float z) => 0.01f * x + 0.02f * z;
    var heights = new float[nx * nz];
    for (int j = 0; j < nz; j++)
        for (int i = 0; i < nx; i++) heights[j * nx + i] = H(-100 + i * 10, -200 + j * 10);
    var road = MakeRoad("r", new[] { new[] { 0.0, H(0, 0), 0 }, new[] { 100.0, H(100, 0), 0 }, new[] { 200.0, H(200, 0), 0 } }, MakeRun(0, 2, "primary"));
    var input = new IntermediateFile
    {
        Format = IntermediateFile.ExpectedFormat, Version = 1,
        Projection = new ProjectionInfo { Scale = 1, VerticalScale = 1 },
        Roads = [road],
        TerrainGrid = new TerrainGridEntry { Origin = [-100, -200], Cell = 10, Nx = nx, Nz = nz, Heights = heights },
        Water =
        [
            new WaterEntry { Osm = "w1", Kind = "pond", Outer = [[60, 30], [100, 30], [100, 60], [60, 60]] },
            new WaterEntry { Osm = "w2", Kind = "pool", Outer = [[300, -100], [320, -100], [320, -90], [300, -90]] },
        ],
        Trees = [new TreeEntry { Osm = "n1", Leaf = "broadleaved", Pos = [80, 45] }],
    };
    var (map, result) = MapWriter.Build(input, MappingConfig.Load(ShippedMapping()));
    var strips = map.MapItems.Values.OfType<Terrain>().Where(t => t.Left.Terrain.QuadData.BrushMaterials[0].Name.ToString().StartsWith("wtr_tran")).ToList();
    var pond = strips.Where(t => t.Node.Position.X < 200).ToList();
    var pool = strips.Where(t => t.Node.Position.X >= 200).ToList();
    Check(pond.Count >= 7 && pool.Count >= 2, $"Wasserstreifen: Teich {pond.Count}, Becken {pool.Count}");
    float level = H(60, 30) - Water.LevelBelow;                      // tiefster Uferpunkt (60, 30)
    Check(pond.All(t => MathF.Abs(t.Node.Position.Y - level) < 0.02f && MathF.Abs(t.ForwardNode.Position.Y - level) < 0.02f),
          $"Spiegel {pond[0].Node.Position.Y:0.00} statt {level:0.00}");
    var pc = pond[0].Left.Terrain.QuadData.BrushColors[0];
    var bc = pool[0].Right.Terrain.QuadData.BrushColors[0];
    Check((pc.R, pc.G, pc.B) == (25, 2, 0) && (bc.R, bc.G, bc.B) == (8, 5, 0), $"Farben {pc} / {bc}");
    Check(strips.All(t => !t.Collision && !t.Left.DetailVegetation && !t.Right.DetailVegetation && !t.Boundary), "Wasser mit Kollision, Rand oder Detailvegetation");
    bool Wet(float x, float z) => pond.Any(t => x >= t.Node.Position.X && x <= t.ForwardNode.Position.X && MathF.Abs(z - t.Node.Position.Z) <= t.Left.Terrain.Size + 1e-3f);
    for (float x = 60.5f; x < 100; x += 2)
        for (float z = 30.5f; z < 60; z += 2) Check(Wet(x, z), $"Teich bei ({x}, {z}) ohne Wasser");
    Check(pond.All(t => t.Node.Position.X >= 59.7f && t.ForwardNode.Position.X <= 100.3f &&
                        t.Node.Position.Z - t.Left.Terrain.Size >= 30 - 4.1f && t.Node.Position.Z + t.Right.Terrain.Size <= 60 + 4.1f), "Wasser ragt weit über den Teich");
    // Becken: Grund abgesenkt, Ufer und Umgebung über dem Spiegel
    var g = input.TerrainGrid!;
    Check(g.HeightAt(80, 45) < level - 1.2f, $"Grund {g.HeightAt(80, 45):0.00} nicht abgesenkt (Spiegel {level:0.00})");
    Check(g.HeightAt(56, 45) >= level + Water.ShoreAbove - 0.01f && g.HeightAt(80, 26) >= level + Water.ShoreAbove - 0.01f, "Ufer unter dem Spiegel");
    // Straßengelände zum Teich hin gekürzt (Teich 30 m neben der Straßenlinie)
    var rd = map.MapItems.Values.OfType<Road>().Where(r => r.ForwardNode.Position.X > 60 && r.Node.Position.X < 100).ToList();
    Check(rd.Count > 0 && rd.All(r => r.Right.Terrain.Size <= 30 - Water.RoadKeep && r.Right.Terrain.Size >= Water.RoadMin),
          "Straßengelände nicht gekürzt: " + string.Join(", ", rd.Select(r => r.Right.Terrain.Size)));
    // Grund: Schlamm im Teich, Fliesen im Becken, ohne Detailvegetation
    var quads = TerrainQuads(map).ToList();
    var inPond = quads.Where(q => q.C.X > 61 && q.C.X < 99 && q.C.Z > 31 && q.C.Z < 59).ToList();
    var inPool = quads.Where(q => q.C.X > 301 && q.C.X < 319 && q.C.Z > -99 && q.C.Z < -91).ToList();
    Check(inPond.Count > 20 && inPond.All(q => q.Mat == "20" && q.NoGrass), $"Teichgrund: {inPond.Count} Quads, Materialien {string.Join(",", inPond.Select(q => q.Mat).Distinct())}");
    Check(inPool.Count > 5 && inPool.All(q => q.Mat == "share_27" && q.NoGrass), $"Beckengrund: {inPool.Count} Quads, Materialien {string.Join(",", inPool.Select(q => q.Mat).Distinct())}");
    Check(!map.MapItems.Values.OfType<Model>().Any(), "Baum im Teich gesetzt");
    Check(result.Warnings.Any(w => w.Contains("Info: Wasserflächen: 2 von 2 gesetzt")), "Info-Zeile fehlt");
}

static void CountryOnNetworkNodesOnly()
{
    // Wie der Editor speichert: Land in allen Knoten von Straßen und Prefabs (je Netz einer als Grenze),
    // Gelände und Gebäude (z. B. Sperrbaken) ohne Land.
    var map = new Map();
    var r1 = Road.Add(map, new Vector3(0, 0, 0), new Vector3(50, 0, 0), "ger1");
    var r2 = r1.Append(new Vector3(100, 0, 0));
    var pf = Prefab.Add(map, new Vector3(300, 0, 0), "ch_14001", ThreeArmPpd());
    var t = Terrain.Add(map, new Vector3(0, 0, 100), new Vector3(50, 0, 100), "0", 10, 10);
    var b = Buildings.Add(map, new Vector3(0, 0, 200), new Vector3(10, 0, 200), "dead_end");
    int nets = Country.Apply(map, "de");
    Check(nets == 2, $"{nets} Netze statt 2");
    foreach (var nd in new[] { r1.Node, r1.ForwardNode, r2.ForwardNode }.Concat(pf.Nodes))
        Check(nd.ForwardCountry == 5 && nd.BackwardCountry == 5, $"Netzknoten bei {nd.Position} ohne Land");
    foreach (var nd in new[] { t.Node, t.ForwardNode, b.Node, b.ForwardNode })
        Check(nd.ForwardCountry == 0 && nd.BackwardCountry == 0 && !nd.IsCountryBorder, $"Knoten bei {nd.Position} hat ein Land");
    Check(map.Nodes.Values.Count(n => n.IsCountryBorder) == 2, "je Netz ein Grenzknoten erwartet");
}

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
    // Sichtstück (Straße einer höheren Stufe mit offenem Ende): Typ nach Klasse, aber ohne KI/Navi/Karte
    var vis = cfg.Resolve(MakeRun(0, 1, "track", role: "visible"));
    var track = cfg.Resolve(MakeRun(0, 1, "track", role: "core"));
    Check(vis.RoadType == track.RoadType, "Sichtstück behält Straßentyp");
    Check(vis.AiVehicles == track.AiVehicles && vis.ShowInUiMap == false && vis.GpsAvoid == true, "Sichtstück: KI wie sonst, nicht auf Karte/Navi");
    var visRes = cfg.Resolve(MakeRun(0, 1, "unclassified", role: "visible"));
    Check(visRes.AiVehicles != false && visRes.RoadType == other.RoadType, "Sichtstück einer Gemeindestraße mit KI");
    // Seitenstreifen nach shoulder=* (Issue #3): asphaltierter Rand, nicht an Autobahnen
    var shRun = MakeRun(0, 1, "primary", role: "core"); shRun.Shoulder = "yes";
    var sh = cfg.Resolve(shRun);
    Check(sh.Right?.LeftEdge == "ger_sh_as_15" && sh.Right?.RightEdge == "ger_sh_as_15" && sh.RoadType == cfg.Resolve(MakeRun(0, 1, "primary", role: "core")).RoadType, "shoulder=yes: asphaltierte Ränder");
    var shR = MakeRun(0, 1, "primary", role: "core"); shR.Shoulder = "right";
    Check(cfg.Resolve(shR).Right?.RightEdge == "ger_sh_as_15" && cfg.Resolve(shR).Right?.LeftEdge != "ger_sh_as_15", "shoulder=right: nur rechts");
    var shM = MakeRun(0, 1, "motorway", oneway: true); shM.Shoulder = "no";
    Check(cfg.Resolve(shM) == mw, "Autobahn: shoulder ändert nichts");
    var swL = MakeRun(0, 1, "residential", role: "connector"); swL.Sidewalk = "left";
    Check(cfg.Resolve(swL).Right?.LeftEdge == "ger_sw_3m_a" && cfg.Resolve(swL).Right?.RightEdge != "ger_sw_3m_a", "sidewalk=left: Gehweg nur links");
    var swB = MakeRun(0, 1, "residential", role: "connector"); swB.Sidewalk = "both"; swB.Shoulder = "yes";
    Check(cfg.Resolve(swB).Right?.LeftEdge == "ger_sw_3m_a" && cfg.Resolve(swB).Right?.RightEdge == "ger_sw_3m_a", "sidewalk=both hat Vorrang vor shoulder");
    // Sperrfläche (setzt der Writer): Straßentyp ger26, sonst wie die Regel des Stücks
    var hc = MakeRun(0, 1, "primary", role: "connector"); hc.Hatched = true;
    var hcs = cfg.Resolve(hc);
    Check(hcs.RoadType == "ger26" && hcs.TerrainLeft == conn.TerrainLeft, "Sperrfläche: ger26, Gelände wie Anschlussstraße");
    var hv = MakeRun(0, 1, "primary", role: "visible"); hv.Hatched = true;
    var hvs = cfg.Resolve(hv);
    Check(hvs.RoadType == "ger26" && hvs.ShowInUiMap == false && hvs.GpsAvoid == true, "Sperrfläche auf Sichtstück: nicht auf Karte/Navi");
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
    // prüft anderes; gerundete Ecken änderten Stückzahl und Kürzungen
    CornerRounding.Enabled = false;
    try {
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
    } finally { CornerRounding.Enabled = true; }
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
    // prüft anderes; gerundete Ecken änderten Stückzahl und Kürzungen
    CornerRounding.Enabled = false;
    try {
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
    } finally { CornerRounding.Enabled = true; }
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
    // Feldweg hier ohne Anschluss ans Netz: nicht erreichbar → nicht geheim, sondern nicht auf der Karte
    Check(!t.Secret && !t.ShowInUiMap && t.GpsAvoid && !t.AiVehicles, $"Feldweg: secret {t.Secret}, gps_avoid {t.GpsAvoid}, ai {t.AiVehicles}");
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
    // Geheimstraße ohne „GPS meiden“ (sonst Fehler im Spiel: Secret road item with 'GPS avoid' flag set)
    Check(t.Secret && !t.ShowInUiMap && !t.GpsAvoid, $"gesperrter Feldweg: secret {t.Secret}, ui_map {t.ShowInUiMap}, gps_avoid {t.GpsAvoid}");
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

static void PrefabLaneBoundAtNode()
{
    // Straße r (halbe Breite 4) endet bei x = 0 an einem Prefab, das bei x > 0 liegt. Dessen äußere Spur
    // setzt die Straße fort; ihr Spurpunkt (1, 2) liegt 2 m innerhalb des Straßenrands (z = 4).
    var st = new ResolvedStyle("ger1", 20, 20, RoadResolution.HighPoly, null, null, Width: 8);
    var r = MakeRoad("r", new[] { new[] { -50.0, 0, 0 }, new[] { 0.0, 0, 0 } }, MakeRun(0, 1, "primary"));
    var styles = new Dictionary<RoadEntry, ResolvedStyle[]> { [r] = [st] };
    var old = (RoadCap.PrefabCentres, RoadCap.UseNodeBounds, RoadCap.PrefabLanes);
    try
    {
        RoadCap.PrefabLanes = null;
        RoadCap.PrefabCentres = [(new Vector3(1, 0, 2), Vector2.Zero, Vector2.UnitX)];
        var cap = RoadCap.Build([r], styles, null);
        // letzte Spalte: Strahl vom Rand quer nach außen, längs der Knotenlinie – frei
        float g0 = cap.RayFree(new Vector2(0, 4), Vector2.UnitY, 20, r, new Vector2(-50, 0), Vector2.Zero);
        Check(g0 >= 20 - 1e-3f, $"Strahl längs der Knotenlinie gesperrt ({g0:0.00})");
        // Strahl ins Prefab hinein: gesperrt erst ab der Knotenlinie (x = 0), nicht schon am Anfang
        float g1 = cap.RayFree(new Vector2(-2, 2), Vector2.UnitX, 20, r, new Vector2(-50, -10), new Vector2(0, -10));
        Check(MathF.Abs(g1 - 2) < 1e-3f, $"Strahl ins Prefab: {g1:0.00} statt 2");
        // ohne Knotengrenze wie bisher: Scheibe (3 m) reicht über den Rand, Spalte ohne Gelände
        RoadCap.UseNodeBounds = false;
        var cap0 = RoadCap.Build([r], styles, null);
        float g2 = cap0.RayFree(new Vector2(0, 4), Vector2.UnitY, 20, r, new Vector2(-50, 0), Vector2.Zero);
        Check(g2 < 1e-3f, $"Prüfaufbau: ohne Knotengrenze sollte die Spalte gesperrt sein ({g2:0.00})");
    }
    finally { (RoadCap.PrefabCentres, RoadCap.UseNodeBounds, RoadCap.PrefabLanes) = old; }
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
    // Brückenkörper mit Brüstung (Straßentyp der Brücke: Standard ger1 → shared_simple, dlc_no_6)
    // statt der Geländer an beiden Rändern
    var bridgeItems = map.MapItems.Values.OfType<Road>().Where(r => r.Left.Terrain.Size == 0 && r.Right.Terrain.Size == 0).ToList();
    Check(bridgeItems.Count > 0 && bridgeItems.All(r =>
            new[] { r.Left, r.Right }.SelectMany(sd => sd.Railings.Models).Count(m => m.Model.ToString() == "dlc_no_6") == 1 &&
            new[] { r.Left, r.Right }.SelectMany(sd => sd.Railings.Models).All(m => m.Model.ToString() != "mod_ger_3")),
        "Brücke ohne Körper oder mit zusätzlichem Geländer");
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


static void BridgeBodySingle()
{
    var cfg = MappingConfig.Load(ShippedMapping());
    var warnings = new List<string>();
    // gerade Autobahnbrücke (3 Punkte auf einer Linie) → ein Stück, dlc_no_5 links mit Versatz 0
    var mw = MakeRoad("mw", new[] { new[] { 0.0, 7, 0 }, new[] { 80.0, 7, 0 }, new[] { 100.0, 7, 0 }, new[] { 120.0, 7, 0 }, new[] { 200.0, 7, 0 } },
        MakeRun(0, 1, "motorway", oneway: true), MakeRun(1, 3, "motorway", oneway: true, bridge: true), MakeRun(3, 4, "motorway", oneway: true));
    // Feldweg über die Autobahn mit Kuppe (Mitte 1,2 m über der Sehne) → mehrere Stücke, dlc_no_6
    var tr = MakeRoad("tr", new[] { new[] { 150.0, 0, -100 }, new[] { 150.0, 6, -30 }, new[] { 150.0, 8.4, 0 }, new[] { 150.0, 6, 30 }, new[] { 150.0, 0, 100 } },
        MakeRun(0, 1, "track", role: "visible"), MakeRun(1, 3, "track", role: "visible", bridge: true), MakeRun(3, 4, "track", role: "visible"));
    // Rampe auf einer Brücke → dlc_no_14 rechts
    var ramp = MakeRoad("ramp", new[] { new[] { 0.0, 7, 40 }, new[] { 20.0, 7, 40 }, new[] { 40.0, 7, 40 } },
        MakeRun(0, 1, "motorway_link", role: "link", oneway: true), MakeRun(1, 2, "motorway_link", role: "link", oneway: true, bridge: true));
    var singles = BridgeBodies.PlanSingle([mw, tr, ramp], cfg.Resolve, cfg.BodyFor, [], 200f, warnings);
    Check(singles.Count == 3, $"{singles.Count} Körper statt 3");
    var a = singles.Single(x => x.Road == mw);
    Check(a.Type.Railing == "dlc_no_5" && a.Type.Side == "left" && a.Type.Offset == 0 && a.Merged, "Autobahn: dlc_no_5 links, ein Stück");
    Check(mw.Points.Count == 4 && a.Run.To == a.Run.From + 1, "gerade Brücke nicht zusammengelegt");
    var b = singles.Single(x => x.Road == tr);
    Check(b.Type.Railing == "dlc_no_6" && !b.Merged && tr.Points.Count == 5, "Kuppe: dlc_no_6, nicht zusammengelegt (Lichtraum)");
    var c = singles.Single(x => x.Road == ramp);
    Check(c.Type.Railing == "dlc_no_14" && c.Type.Side == "right", "Rampe: dlc_no_14 rechts");
    // breite Straße (über max_width) bekommt keinen Körper
    var wide = cfg.Resolve(new RunEntry { From = 0, To = 1, Highway = "primary", Role = "core", Lanes = 4 });
    Check(wide.RoadType == "ger29" && cfg.BodyFor(wide) is null, $"Körper für zu breite Straße ({wide.RoadType}, {wide.Width} m)");
}

static void BridgeExtendedOverSkewedRoad()
{
    var cfg = MappingConfig.Load(ShippedMapping());
    var warnings = new List<string>();
    // 16 m Autobahnbrücke (x −8…8, y 7) über einen Feldweg (y 0), der unter 60° kreuzt
    RoadEntry Motorway(string id) => MakeRoad(id, new[] { new[] { -100.0, 7, 0 }, new[] { -8.0, 7, 0 }, new[] { 8.0, 7, 0 }, new[] { 100.0, 7, 0 } },
        MakeRun(0, 1, "motorway", oneway: true), MakeRun(1, 2, "motorway", oneway: true, bridge: true), MakeRun(2, 3, "motorway", oneway: true));
    var mw = Motorway("mw");
    var c = MathF.Cos(MathF.PI / 3); var s = MathF.Sin(MathF.PI / 3);
    var tr = MakeRoad("tr", new[] { new[] { -50.0 * c, 0, -50.0 * s }, new[] { 50.0 * c, 0, 50.0 * s } }, MakeRun(0, 1, "track", role: "visible"));
    var singles = BridgeBodies.PlanSingle([mw, tr], cfg.Resolve, cfg.BodyFor, [], 200f, warnings);
    var a = singles.Single(x => x.Road == mw);
    // nötig je Seite: (2,25 + 3) / sin 60° + 1,6 (Widerlager) + 9,4 (Körperbreite ab Linie) · cot 60° ≈ 13,1 m
    float need = (2.25f + BridgeBodies.Clearance) / s + BridgeBodies.Wall + 9.4f * c / s;
    double x0 = mw.Points[a.Run.From][0], x1 = mw.Points[a.Run.To][0];
    Check(Math.Abs(x0 + need) < 0.1 && Math.Abs(x1 - need) < 0.1, $"Brücke {x0:0.0}…{x1:0.0} statt ±{need:0.0}");
    Check(a.Merged && mw.Points.Count == 4 && mw.Runs.Count == 3 && mw.Runs[0].To == a.Run.From && mw.Runs[2].From == a.Run.To,
        "verlängerte Brücke nicht als ein Stück oder Läufe nicht lückenlos");
    Check(warnings.Any(w => w.Contains("verlängert")), "keine Meldung zur Verlängerung");
    // rechtwinklig darunter mit genug Platz: bleibt 16 m
    var mw2 = Motorway("mw2");
    var tr2 = MakeRoad("tr2", new[] { new[] { 0.0, 0, -50 }, new[] { 0.0, 0, 50 } }, MakeRun(0, 1, "track", role: "visible"));
    var b = BridgeBodies.PlanSingle([mw2, tr2], cfg.Resolve, cfg.BodyFor, [], 200f, warnings).Single(x => x.Road == mw2);
    Check(mw2.Points[b.Run.From][0] == -8 && mw2.Points[b.Run.To][0] == 8, "rechtwinklig unterquerte Brücke verändert");
}

static void UnderpassSoleWiderWhenSkewed()
{
    // 60 m Brücke (y 8) längs x über eine Straße (y 0, Breite 8), die unter 45° kreuzt
    var st = new ResolvedStyle("ger1", 60, 60, RoadResolution.HighPoly, null, null, Width: 8);
    var up = MakeRoad("up", new[] { new[] { -150.0, 8, 0 }, new[] { -30.0, 8, 0 }, new[] { 30.0, 8, 0 }, new[] { 150.0, 8, 0 } },
        MakeRun(0, 1, "primary"), MakeRun(1, 2, "primary", bridge: true), MakeRun(2, 3, "primary"));
    var low = MakeRoad("low", new[] { new[] { -100.0, 0, -100 }, new[] { 100.0, 0, 100 } }, MakeRun(0, 1, "primary"));
    var styles = new Dictionary<RoadEntry, ResolvedStyle[]> { [low] = [st], [up] = [st, st, st] };
    var u = Underpasses.Build([low, up], styles, []);
    Check(u.Count == 1, $"{u.Count} Unterführungen statt 1");
    float floor = -Underpasses.FloorDrop;
    // Brückenkante 4 m + 2 m neben der Linie: Straßenmitte dort 6 m längs versetzt (cot 45° = 1),
    // ihr Rand 4 / sin 45° = 5,7 m weiter – bis 11,7 m (+ 1,5 m Bankett) längs bleibt die Sohle flach
    Check(MathF.Abs(u.Cap(11, 4, 8) - floor) < 0.01f, $"Sohle an der Brückenkante bei s+11: {u.Cap(11, 4, 8):0.00} statt {floor:0.00}");
    Check(MathF.Abs(u.Cap(-11, -4, 8) - floor) < 0.01f, "Sohle an der anderen Kante");
    Check(u.Cap(25, 0, 8) > floor + 1, $"vor dem Brückenende keine Böschung: {u.Cap(25, 0, 8):0.00}");
}

static void InterchangeShiftAwayFromStructure()
{
    // Einfahrt: Zusammenführung 40 m lang, Spurende 63 m; Brücke 29,5…49,5 m hinter dem Knoten,
    // also unter dem Ende der Zusammenführung
    float d = Interchanges.ShiftFor([(29.5f, 49.5f)], [], 40f, 63f, 1000, 1000, 1000, out float ext);
    Check(MathF.Abs(d - (40 + Interchanges.ExtMargin - 29.5f + 0.5f)) < 0.01f, $"Verschiebung {d:0.00}");
    Check(MathF.Abs(ext - (49.5f + d + Interchanges.ExtMargin - 40)) < 0.01f, $"Zusatzspur-Straße {ext:0.00}");
    // auf der anderen Seite des Knotens ein Bauwerk im Weg: keine Verschiebung (wie bisher)
    Check(Interchanges.ShiftFor([(29.5f, 49.5f)], [(10f, 20f)], 40f, 63f, 1000, 1000, 1000, out _) == 0, "trotz Bauwerk verschoben");
    // Bauwerk dicht am Knoten: mehr als MaxShift nötig – keine Verschiebung
    Check(Interchanges.ShiftFor([(5f, 25f)], [], 40f, 63f, 1000, 1000, 1000, out _) == 0, "zu weit verschoben");
    // Bauwerk nur unter dem äußeren Prefab: keine Verschiebung, nur Zusatzspur-Straße
    float d2 = Interchanges.ShiftFor([(60f, 80f)], [], 40f, 63f, 1000, 1000, 1000, out float ext2);
    Check(d2 == 0 && ext2 > 0, $"Verschiebung {d2:0.0}, Zusatzspur-Straße {ext2:0.0}");
    // Rampe zu kurz für die Verschiebung
    Check(Interchanges.ShiftFor([(29.5f, 49.5f)], [], 40f, 63f, 1000, 1000, 50, out _) == 0, "Rampe zu kurz, trotzdem verschoben");
}

static void ExtRoadAbsorbsStubs()
{
    // Hauptfahrbahn längs x mit Brücke 30…50 m; Zusatzspur-Straße 25…54 m: die 5 m davor und 4 m
    // dahinter werden Brücke (bis ExtMargin + 1,5 m), längere Endstücke nicht
    var main = MakeRoad("m", new[] { new[] { 0.0, 5, 0 }, new[] { 30.0, 5, 0 }, new[] { 50.0, 5, 0 }, new[] { 200.0, 5, 0 } },
        MakeRun(0, 1, "motorway", oneway: true), MakeRun(1, 2, "motorway", oneway: true, bridge: true), MakeRun(2, 3, "motorway", oneway: true));
    var plan = new Interchanges.Plan { Kind = "entry", OsmNode = 1, MainIn = main, MainOut = main, Ramp = main,
                                       Anchor = Vector3.Zero, Direction = Vector3.UnitX };
    var ext = Interchanges.ExtRoad(plan, main, true, 25, 54, [(30f, 50f)]);
    Check(ext.Runs.All(r => r.Bridge) && ext.Runs.All(r => r.Role == Interchanges.ExtRole),
        "Endstücke nicht zur Brücke: " + string.Join(" ", ext.Runs.Select(r => $"{r.From}-{r.To}{(r.Bridge ? "B" : "")}")));
    var ext2 = Interchanges.ExtRoad(plan, main, true, 20, 54, [(30f, 50f)]);
    Check(!ext2.Runs[0].Bridge && ext2.Runs.Any(r => r.Bridge), "10 m langes Endstück wurde Brücke");
}

static void MotorwayStartFromTwoLinks()
{
    // Zwei 2-spurige Anschlussäste treffen sich bei (0,0); in +x Richtung beginnt die
    // Autobahn mit 150 m 3 Spuren (Beschleunigungsstreifen), dann 2 Spuren.
    RunEntry R(int a, int b, string hw, int lanes) => new() { From = a, To = b, Highway = hw, Role = hw == "motorway" ? "core" : "link", Oneway = true, Lanes = lanes };
    var main = MakeRoad("m", [[0, 0, 0], [150, 0, 0], [600, 0, 0]], R(0, 1, "motorway", 3), R(1, 2, "motorway", 2));
    main.Start = new EndInfo { Kind = "node", OsmNode = 7 };
    var through = MakeRoad("t", [[-500, 0, 0], [0, 0, 0]], R(0, 1, "motorway_link", 2));
    through.End = new EndInfo { Kind = "node", OsmNode = 7 };
    // Zufahrt kommt von rechts (rechts von +x ist +z)
    var ramp = MakeRoad("r", [[-400, 0, 60], [-150, 0, 12], [0, 0, 0]], R(0, 2, "motorway_link", 1));
    ramp.End = new EndInfo { Kind = "node", OsmNode = 7 };
    var plans = Interchanges.Find([main, through, ramp], []);
    Check(plans.Count == 1 && plans[0].Kind == "entry" && plans[0].MainIn == through && plans[0].Ramp == ramp,
          $"keine Einfahrt erkannt ({plans.Count})");
    Check(main.Runs[0].Lanes == 2 && main.Runs[1].Lanes == 2, "Zusatzspur nicht auf 2 Spuren gesetzt");
    // ohne eindeutige Geradeaus-Richtung (beide Äste gleich schräg) keine Einfahrt
    var a = MakeRoad("a", [[-300, 0, 60], [0, 0, 0]], R(0, 1, "motorway_link", 2)); a.End = new EndInfo { Kind = "node", OsmNode = 8 };
    var b = MakeRoad("b", [[-300, 0, -60], [0, 0, 0]], R(0, 1, "motorway_link", 2)); b.End = new EndInfo { Kind = "node", OsmNode = 8 };
    var m2 = MakeRoad("m2", [[0, 0, 0], [600, 0, 0]], R(0, 1, "motorway", 2)); m2.Start = new EndInfo { Kind = "node", OsmNode = 8 };
    Check(Interchanges.Find([m2, a, b], []).Count == 0, "Gabel ohne Geradeaus-Ast als Einfahrt erkannt");
}

static void Hw3DoubleExitAndTwoLaneLinks()
{
    RunEntry R(int a, int b, string hw, int lanes) => new() { From = a, To = b, Highway = hw, Role = hw == "motorway" ? "core" : "link", Oneway = true, Lanes = lanes };
    EndInfo N(long id) => new() { Kind = "node", OsmNode = id };
    // 3 Spuren, 150 m vor dem Knoten 4 Spuren (Verzögerungsstreifen), Ausfahrt rechts (+z) zweispurig
    var mi = MakeRoad("mi", [[-600, 0, 0], [-150, 0, 0], [0, 0, 0]], R(0, 1, "motorway", 3), R(1, 2, "motorway", 4)); mi.End = N(1);
    var mo = MakeRoad("mo", [[0, 0, 0], [600, 0, 0]], R(0, 1, "motorway", 3)); mo.Start = N(1);
    var ra = MakeRoad("ra", [[0, 0, 0], [150, 0, 12], [400, 0, 60]], R(0, 2, "motorway_link", 2)); ra.Start = N(1); ra.End = N(2);
    // an der Rampe hängt am anderen Ende eine einspurige Rampe
    var r1 = MakeRoad("r1", [[400, 0, 60], [600, 0, 100]], R(0, 1, "motorway_link", 1)); r1.Start = N(2);
    var plans = Interchanges.Find([mi, mo, ra, r1], []);
    Check(plans.Count == 1 && plans[0].Kind == "exit" && plans[0].K == Interchanges.Hw3Double, "keine zweispurige Ausfahrt erkannt");
    Check(mi.Runs[1].Lanes == 3, "Verzögerungsstreifen nicht auf 3 Spuren gesetzt");
    // Rampe am zweispurigen Rampenknoten: zweispurig, auch wenn am anderen Ende eine einspurige hängt
    Check(Interchanges.NormalizeLinkLanes([mi, mo, ra, r1]) == 1 && ra.Runs[0].Role == Interchanges.TwoLaneLinkRole, "Rampe am zweispurigen Prefab-Knoten nicht zweispurig");
    // ohne Prefab: zweispurige Rampe neben einspuriger bleibt einspurig
    RunEntry R2(int a, int b, int lanes) => new() { From = a, To = b, Highway = "motorway_link", Role = "link", Oneway = true, Lanes = lanes };
    var x2 = MakeRoad("x2", [[0, 0, 500], [300, 0, 500]], R2(0, 1, 2)); x2.End = N(9);
    var x1 = MakeRoad("x1", [[300, 0, 500], [500, 0, 500]], R2(0, 1, 1)); x1.Start = N(9);
    Interchanges.Find([x2, x1], []);
    Check(Interchanges.NormalizeLinkLanes([x2, x1]) == 0 && x2.Runs[0].Role == "link", "Rampe trotz einspurigem Anschluss zweispurig");
}

static void Split22AtTwoTwoLaneLinks()
{
    RunEntry R(int a, int b, string hw, int lanes) => new() { From = a, To = b, Highway = hw, Role = hw == "motorway" ? "core" : "link", Oneway = true, Lanes = lanes };
    EndInfo N(long id) => new() { Kind = "node", OsmNode = id };
    // Autobahnende: 2 Spuren (vor dem Knoten 3), dann zwei zweispurige Äste, einer geradeaus, einer rechts
    var mi = MakeRoad("mi", [[-600, 0, 0], [-150, 0, 0], [0, 0, 0]], R(0, 1, "motorway", 2), R(1, 2, "motorway", 3)); mi.End = N(1);
    var st = MakeRoad("st", [[0, 0, 0], [500, 0, 0]], R(0, 1, "motorway_link", 2)); st.Start = N(1);
    var ra = MakeRoad("ra", [[0, 0, 0], [100, 0, 25], [400, 0, 120]], R(0, 2, "motorway_link", 2)); ra.Start = N(1);
    var plans = Interchanges.Find([mi, st, ra], []);
    Check(plans.Count == 1 && plans[0].K == Interchanges.Split22 && plans[0].MainOut == st && plans[0].Ramp == ra,
          $"keine Gabelung 2+2 erkannt ({plans.Count}, {(plans.Count > 0 ? plans[0].K.Name : "-")})");
    Check(mi.Runs[1].Lanes == 2, "Zusatzspur vor der Gabelung nicht auf 2 Spuren gesetzt");
}

static void Lane4MergeAndExit()
{
    RunEntry R(int a, int b, string hw, int lanes) => new() { From = a, To = b, Highway = hw, Role = hw == "motorway" ? "core" : "link", Oneway = true, Lanes = lanes };
    EndInfo N(long id) => new() { Kind = "node", OsmNode = id };
    // in +x: 3 Spuren bis x = 0, Einmündung von rechts (+z) zweispurig, 4 Spuren bis x = 700,
    // dort Ausfahrt nach rechts (einspurig), 3 Spuren weiter
    var a = MakeRoad("a", [[-600, 0, 0], [0, 0, 0]], R(0, 1, "motorway", 3)); a.End = N(1);
    var ramp = MakeRoad("r", [[-400, 0, 120], [-100, 0, 25], [0, 0, 0]], R(0, 2, "motorway_link", 2)); ramp.End = N(1);
    var m = MakeRoad("m", [[0, 0, 0], [100, 0, 0], [700, 0, 0]], R(0, 1, "motorway", 5), R(1, 2, "motorway", 4)); m.Start = N(1); m.End = N(2);
    var b = MakeRoad("b", [[700, 0, 0], [1300, 0, 0]], R(0, 1, "motorway", 3)); b.Start = N(2);
    var ex = MakeRoad("x", [[700, 0, 0], [800, 0, 25], [1000, 0, 120]], R(0, 2, "motorway_link", 1)); ex.Start = N(2);
    var plans = Interchanges.Find([a, ramp, m, b, ex], []);
    Check(plans.Count == 2 && plans.Any(p => p.K == Interchanges.Hw3Merge2 && p.Kind == "entry" && p.Ramp == ramp)
          && plans.Any(p => p.K == Interchanges.Hw3Exit && p.Kind == "exit" && p.Ramp == ex), $"Paar nicht erkannt ({plans.Count})");
    Check(m.Runs.All(x => x.Role == "ext3" && x.Lanes == 4), "vierspurige Strecke nicht als ext3/4 Spuren");
    // ohne Ausfahrt am Ende: keine Einmündung
    var m2 = MakeRoad("m2", [[0, 0, 0], [700, 0, 0]], R(0, 1, "motorway", 4)); m2.Start = N(1);
    var a2 = MakeRoad("a2", [[-600, 0, 0], [0, 0, 0]], R(0, 1, "motorway", 3)); a2.End = N(1);
    var r2 = MakeRoad("r2", [[-400, 0, 120], [-100, 0, 25], [0, 0, 0]], R(0, 2, "motorway_link", 2)); r2.End = N(1);
    Check(Interchanges.Find([a2, r2, m2], []).Count == 0, "Einmündung ohne passende Ausfahrt gesetzt");
}

static void LinkJoinAndLaneDrop()
{
    RunEntry R(int a, int b, int lanes) => new() { From = a, To = b, Highway = "motorway_link", Role = "link", Oneway = true, Lanes = lanes };
    EndInfo N(long id) => new() { Kind = "node", OsmNode = id };
    var a = MakeRoad("a", [[-500, 0, 0], [0, 0, 0]], R(0, 1, 2)); a.End = N(1);
    var b = MakeRoad("b", [[-400, 0, 120], [-100, 0, 25], [0, 0, 0]], R(0, 2, 1)); b.End = N(1);
    var c = MakeRoad("c", [[0, 0, 0], [130, 0, 0], [400, 0, 0], [700, 0, 0]], R(0, 1, 3), R(1, 2, 2), R(2, 3, 1)); c.Start = N(1);
    var roads = new List<RoadEntry> { a, b, c };
    Check(Interchanges.PrepareLaneDrops(roads) == 1 && roads.Count == 4, "Rampe nicht am Spurende geteilt");
    var plans = Interchanges.Find(roads, []);
    Check(plans.Any(p => p.Kind == "drop" && p.MainIn == c) && plans.Any(p => p.Kind == "entry" && p.MainIn == a && p.Ramp == b && p.MainOut == c),
          $"Zusammenführung/Spurende nicht erkannt ({string.Join(",", plans.Select(p => p.Kind))})");
    Check(c.Runs.All(x => x.Lanes == 2) && roads[3].Runs.All(x => x.Lanes == 1), "Spurzahlen nach dem Teilen falsch");
}

// Einmündung: von Westen Straße mit Abbiegespur (2 Spuren zum Knoten, 1 weg), Nebenstraße links
// (Norden, z negativ), gegenüber Landstraße 1+1 nach Osten
static RunEntry TRun(int a, int b, int? fwd = null, int? bwd = null, bool oneway = false) =>
    new() { From = a, To = b, Highway = "primary", Role = "core", Oneway = oneway,
            Lanes = oneway ? 1 : fwd + bwd ?? 2, LanesForward = fwd, LanesBackward = bwd };

static EndInfo TNode(long id) => new() { Kind = "node", OsmNode = id };

static void TurnTHatchedArm()
{
    var t = MakeRoad("t", [[-400, 0, 0], [-200, 0, 0], [0, 0, 0]], TRun(0, 2, 2, 1)); t.End = TNode(1);
    var e = MakeRoad("e", [[0, 0, 0], [100, 0, 0], [200, 0, 0], [300, 0, 0]], TRun(0, 3)); e.Start = TNode(1);
    var s = MakeRoad("s", [[0, 0, 0], [0, 0, -150], [0, 0, -300]], TRun(0, 2)); s.Start = TNode(1);
    var roads = new List<RoadEntry> { t, e, s };
    Junctions.Reset();
    var plans = Junctions.Find(roads, (_, _) => "ger1", []);
    var tt = plans.SingleOrDefault(p => p.Def == Junctions.TTURN);
    var hp = plans.SingleOrDefault(p => p.Def == Junctions.HATCH11);
    Check(tt is not null && hp is not null, $"Einmündung/Übergang nicht geplant ({plans.Count} Pläne)");
    Check(roads.Count == 4 && tt!.Arms[1].Road == e && hp!.Arms[0].Road == e && hp.Arms[1].Road == roads[3],
          "Arm gegenüber der Abbiegespur nicht für den Übergang geteilt");
    double he = Interchanges.ArcLengthOf(e);
    Check(he > 50 && he < 80, $"Stück mit Sperrfläche {he:0} m (Prefab-Arm + Bogen + halber Übergang erwartet)");
    Check(e.Runs.All(x => x.Hatched && x.Role == "core"), "Sperrfläche fehlt oder Rolle geändert");
    Check(roads[3].Runs.All(x => !x.Hatched) && t.Runs.All(x => !x.Hatched) && s.Runs.All(x => !x.Hatched),
          "Sperrfläche außerhalb des Stücks vor dem Übergang");
}

static void SlipDroppedInHatchedStretch()
{
    // Rechtsabbieger vom gegenüberliegenden Arm (40 m vor der Einmündung) in die Nebenstraße
    List<RoadEntry> Roads(bool turnLane)
    {
        var t = MakeRoad("t", [[-400, 0, 0], [0, 0, 0]], turnLane ? TRun(0, 1, 2, 1) : TRun(0, 1)); t.End = TNode(1);
        var e1 = MakeRoad("e1", [[0, 0, 0], [40, 0, 0]], TRun(0, 1)); e1.Start = TNode(1); e1.End = TNode(2);
        var e2 = MakeRoad("e2", [[40, 0, 0], [400, 0, 0]], TRun(0, 1)); e2.Start = TNode(2);
        var s1 = MakeRoad("s1", [[0, 0, 0], [0, 0, -40]], TRun(0, 1)); s1.Start = TNode(1); s1.End = TNode(3);
        var s2 = MakeRoad("s2", [[0, 0, -40], [0, 0, -400]], TRun(0, 1)); s2.Start = TNode(3);
        var slip = MakeRoad("slip", [[40, 0, 0], [20, 0, -8], [8, 0, -20], [0, 0, -40]], TRun(0, 3, oneway: true));
        slip.Start = TNode(2); slip.End = TNode(3);
        return [t, e1, e2, s1, s2, slip];
    }
    var warnings = new List<string>();
    var with = Roads(true);
    Check(Junctions.PrepareSlips(with, warnings) == 1 && with.Count == 3 && with.All(r => r.Id != "slip"),
          $"Rechtsabbieger nicht weggelassen ({with.Count} Straßen)");
    Check(warnings.Any(w => w.Contains("Sperrfläche")), "Hinweis auf die Sperrfläche fehlt");
    var without = Roads(false);
    Check(Junctions.PrepareSlips(without, []) == 0 && without.Count == 6, "ohne Abbiegespur wird der Rechtsabbieger angeschlossen");
}

static void SettleKeepsFillBelowPrefab()
{
    // Prefab-Fahrbahn: Spurmitten bei z 12, x 0…28, Höhe 0 (= Fläche − Rand). Füllstück nach Osten bei
    // z 0, Südseite: die Reihe 11 m (unter dem Prefab) liegt tief, die Reihe 17 m (Hang daneben) 3 m hoch.
    // Die Fläche dazwischen lag über der Fahrbahn („Erde auf der Straße“).
    Terrain Fill()
    {
        var t = Terrain.Add(new Map(), Vector3.Zero, new Vector3(28, 0, 0), "dirt", 22, 22);
        var q = t.Right.Terrain.QuadData;
        q.Offsets.Clear();
        t.Left.Terrain.QuadData.Offsets.Clear();
        float[] zs = [0, 1, 2, 3, 5, 11, 17, 22];
        for (int j = 0; j < zs.Length; j++)
            for (int i = 0; i <= 2; i++)
                q.Offsets.Add(new VertexData { X = (ushort)i, Y = (ushort)j, Data = new Vector3(14 * i, zs[j] >= 17 ? 3f : -0.5f, zs[j]) });
        return t;
    }
    // höchste Füllfläche (beide Dreiecksteilungen) im Bereich der Prefab-Fahrbahn
    float Worst(Terrain t)
    {
        var v = t.Right.Terrain.QuadData.Offsets.ToDictionary(o => (o.X, o.Y), o => t.Node.Position + o.Data);
        float worst = float.NegativeInfinity;
        for (float x = 0; x <= 28; x += 0.5f)
            for (float z = 9.5f; z <= 14.5f; z += 0.25f)
                foreach (var ((i, j), a) in v)
                {
                    if (!v.TryGetValue(((ushort)(i + 1), j), out var b) || !v.TryGetValue(((ushort)(i + 1), (ushort)(j + 1)), out var c) ||
                        !v.TryGetValue((i, (ushort)(j + 1)), out var d)) continue;
                    if (x < a.X || x > b.X || z < a.Z || z > d.Z) continue;
                    foreach (var (p, q, r) in new[] { (a, b, c), (a, c, d), (a, b, d), (b, c, d) })
                    {
                        float den = (q.Z - r.Z) * (p.X - r.X) + (r.X - q.X) * (p.Z - r.Z);
                        float w1 = ((q.Z - r.Z) * (x - r.X) + (r.X - q.X) * (z - r.Z)) / den;
                        float w2 = ((r.Z - p.Z) * (x - r.X) + (p.X - r.X) * (z - r.Z)) / den;
                        if (w1 < -1e-4f || w2 < -1e-4f || 1 - w1 - w2 < -1e-4f) continue;
                        worst = MathF.Max(worst, w1 * p.Y + w2 * q.Y + (1 - w1 - w2) * r.Y);
                    }
                }
        return worst;
    }
    var lanes = Enumerable.Range(0, 15).Select(k => new Vector3(2 * k, 0, 12)).ToList();
    var before = Fill();
    float w0 = Worst(before);
    Check(w0 > 1, $"Prüfaufbau: Füllfläche sollte über der Fahrbahn liegen ({w0:0.00})");
    var without = new CoverCap();
    without.Settle([before]);
    Check(MathF.Abs(Worst(before) - w0) < 1e-3f, "ohne Prefab-Fahrbahn sollte Settle nichts ändern");
    var t = Fill();
    var cc = new CoverCap();
    cc.AddPrefabLanes(lanes);
    cc.Settle([t]);
    float w = Worst(t);
    Check(w <= CoverCap.SettleTol + 0.01f, $"Füllgelände {w:0.00} m über der erlaubten Höhe der Prefab-Fahrbahn");
    // der Hang daneben bleibt Hang (nicht bis auf Fahrbahnhöhe gezogen), weiter weg unverändert
    var offs = t.Right.Terrain.QuadData.Offsets;
    float near = offs.Where(o => o.Y == 6).Min(o => o.Data.Y), far = offs.Where(o => o.Y == 7).Min(o => o.Data.Y);
    Check(near > 0.5f && far > 2.99f, $"Hang zu stark abgesenkt: 17 m daneben {near:0.00}, 22 m daneben {far:0.00}");
}

static void FuelInline()
{
    // Landstraße 600 m nach Osten, Tankstelle 30 m südlich, Zufahrt als Service-Weg aufs Gelände
    var r = MakeRoad("r", [[-300, 0, 0], [0, 0, 0], [300, 0, 0]], TRun(0, 2)); r.Start = TNode(1); r.End = TNode(2);
    var svc = MakeRoad("s", [[0, 0, 0], [0, 0, 30]], new RunEntry { From = 0, To = 1, Highway = "service", Role = "connector" });
    svc.Start = TNode(3); svc.End = TNode(4);
    // Service-Weg hängt mitten an der Straße: Straße dort teilen wie im Zwischenformat
    var r2 = MakeRoad("r2", [[0, 0, 0], [300, 0, 0]], TRun(0, 1)); r2.Start = TNode(3); r2.End = TNode(2);
    r = MakeRoad("r", [[-300, 0, 0], [0, 0, 0]], TRun(0, 1)); r.Start = TNode(1); r.End = TNode(3);
    var roads = new List<RoadEntry> { r, r2, svc };
    Junctions.Reset();
    Junctions.Stations = [new AreaEntry { Kind = "fuel", OsmWay = 7, Points = [[-20, 20], [20, 20], [20, 45], [-20, 45], [-20, 20]] }];
    var w = new List<string>();
    int n = Junctions.PrepareFuel(roads, w);
    var plans = Junctions.Find(roads, (_, _) => "ger1", []);
    Check(n == 1 && plans.Count(p => p.Def == Junctions.GASR) == 1, $"kein Tankstellen-Prefab geplant ({n}, {string.Join(" | ", w)})");
    Check(!roads.Contains(svc) && roads.Count == 2, $"Geländeweg nicht ersetzt oder Straße nicht verbunden ({roads.Count} Straßen)");
    Junctions.Stations = [];
}

static void FuelSingleAccess()
{
    // 150 m Straße (zu kurz für die Durchfahrt), Anschluss des Geländes 10 m vom Ostende –
    // die Einmündung muss weiter nach Westen, damit das T-Prefab (Arme 18 m) Platz hat
    var r = MakeRoad("r", [[-100, 0, 0], [40, 0, 0]], TRun(0, 1)); r.Start = TNode(1); r.End = TNode(3);
    var r2 = MakeRoad("r2", [[40, 0, 0], [50, 0, 0]], TRun(0, 1)); r2.Start = TNode(3); r2.End = TNode(2);
    var svc = MakeRoad("s", [[40, 0, 0], [40, 0, 30]], new RunEntry { From = 0, To = 1, Highway = "service", Role = "connector" });
    svc.Start = TNode(3); svc.End = TNode(4);
    var roads = new List<RoadEntry> { r, r2, svc };
    Junctions.Reset();
    Junctions.Stations = [new AreaEntry { Kind = "fuel", OsmWay = 8, Points = [[-60, 10], [50, 10], [50, 80], [-60, 80], [-60, 10]] }];
    var w = new List<string>();
    int n = Junctions.PrepareFuel(roads, w);
    var plans = Junctions.Find(roads, (_, _) => "ger1", []);
    var gas = plans.SingleOrDefault(p => p.Def == Junctions.GASB);
    var t = plans.SingleOrDefault(p => p.Def == Junctions.TR1);
    Check(n == 1 && gas is not null && t is not null, $"Tankstelle/Einmündung nicht geplant ({string.Join(" | ", w)}; {plans.Count} Pläne)");
    Check(!roads.Contains(svc), "Geländeweg nicht ersetzt");
    // Einmündung mit mindestens 20 m bis zu den Straßenenden (T-Arm 18 m + Rest)
    Check(t!.Anchor.X >= -100 + 20 - 0.5f && t.Anchor.X <= 50 - 20 + 0.5f, $"Einmündung bei x {t.Anchor.X:0} zu nah am Straßenende");
    Check(gas!.Anchor.Z > 15, $"Tankstelle nicht auf der Seite des Geländes (z {gas.Anchor.Z:0})");
    Junctions.Stations = [];
}

static void PlainLaneChangeKeepsLine()
{
    // Einmündung bei x 0, 26 m Landstraße nach Osten, ab Knoten 10 Abbiegespur (2+1) 40 m bis zur
    // Kreuzung bei x 66 – für den 60-m-Übergang ist kein Platz
    List<RoadEntry> Roads()
    {
        RoadEntry R(string id, double[][] pts, RunEntry run, long a, long b) { var r = MakeRoad(id, pts, run); r.Start = TNode(a); r.End = TNode(b); return r; }
        return [
            R("w", [[-300, 0, 0], [0, 0, 0]], TRun(0, 1), 5, 1),
            R("s", [[0, 0, 0], [0, 0, -300]], TRun(0, 1), 1, 6),
            R("a", [[0, 0, 0], [13, 0, 0], [26, 0, 0]], TRun(0, 2), 1, 10),
            R("b", [[26, 0, 0], [36, 0, 0], [46, 0, 0], [56, 0, 0], [66, 0, 0]], TRun(0, 4, 2, 1), 10, 2),
            R("n2", [[66, 0, 0], [66, 0, -300]], TRun(0, 1), 2, 7),
            R("s2", [[66, 0, 0], [66, 0, 300]], TRun(0, 1), 2, 8),
            R("e2", [[66, 0, 0], [366, 0, 0]], TRun(0, 1), 2, 9),
        ];
    }
    var cfg = MappingConfig.Load(ShippedMapping());
    var roads = Roads();
    var plain = Junctions.PlainLaneChanges(roads);
    Check(plain.SetEquals([10L]), $"Wechsel ohne Platz nicht erkannt ({string.Join(",", plain)})");
    LaneCentering.Apply(roads, cfg.Resolve, plain);
    var a = roads.Single(r => r.Id == "a"); var b = roads.Single(r => r.Id == "b");
    Check(a.Points.All(p => Math.Abs(p[2]) < 1e-3), $"Landstraße versetzt: z {string.Join(" ", a.Points.Select(p => p[2].ToString("0.00")))}");
    var zs = b.Points.Select(p => p[2]).ToList();
    bool mono = zs.Zip(zs.Skip(1), (x, y) => y - x).All(d => d <= 1e-3) || zs.Zip(zs.Skip(1), (x, y) => y - x).All(d => d >= -1e-3);
    Check(Math.Abs(zs[0]) < 1e-3 && mono, $"2+1-Stück: Anfang nicht in der Linie oder S-Bogen (z {string.Join(" ", zs.Select(z => z.ToString("0.00")))})");
    Check(Math.Abs(zs[^1] + 2.375) < 0.05, $"2+1-Stück: Ende an der Kreuzung nicht mittig versetzt (z {zs[^1]:0.00})");
    // dort setzt Find keinen Übergang
    Junctions.Reset();
    var plans = Junctions.Find(roads, (r, st) => cfg.Resolve(st ? r.Runs[0] : r.Runs[^1]).RoadType, []);
    Check(!plans.Any(p => p.OsmNode == 10), "Übergang am Wechsel ohne Platz geplant");
    // Gegenprobe: bisher sprang die Linie am Wechsel um 2,375 m (S-Bogen auf dem kurzen Stück)
    var old = Roads();
    Junctions.Plain21.Clear();
    LaneCentering.Apply(old, cfg.Resolve);
    var a0 = old.Single(r => r.Id == "a");
    Check(Math.Abs(a0.Points[^1][2] + 2.375) < 0.05, $"Prüfaufbau: ohne die Regel sollte der Wechselknoten versetzt sein (z {a0.Points[^1][2]:0.00})");
}

static void MinorWaysDoNotBlockJunction()
{
    // Kreuzung K (x 0): Straße nach Westen und Osten, Service-Weg nach Norden, Fußweg nach Süden.
    // 4 m östlich Knoten F, an dem nur ein Feldweg nach Süden abzweigt; die Straße läuft weiter bis x 300.
    RoadEntry R(string id, double[][] pts, string hw, long a, long b)
    {
        var r = MakeRoad(id, pts, new RunEntry { From = 0, To = pts.Length - 1, Highway = hw, Role = "connector" });
        r.Start = TNode(a); r.End = TNode(b); return r;
    }
    var w = R("w", [[-300, 0, 0], [0, 0, 0]], "residential", 5, 1);
    var k = R("k", [[0, 0, 0], [4, 0, 0]], "residential", 1, 2);
    var e = R("e", [[4, 0, 0], [300, 0, 0]], "residential", 2, 6);
    var n = R("n", [[0, 0, 0], [0, 0, -200]], "service", 1, 7);
    var fw = R("fw", [[0, 0, 0], [0, 0, 7], [-60, 0, 8]], "footway", 1, 8);
    var tr = R("tr", [[4, 0, 0], [4, 0, 100]], "track", 2, 9);
    var roads = new List<RoadEntry> { w, k, e, n, fw, tr };
    var cfg = MappingConfig.Load(ShippedMapping());
    var warn = new List<string>();
    Junctions.DetachMinorWays(roads, run => cfg.Resolve(run).Width / 2, warn);
    Check(fw.Start?.Kind == "trimmed" && tr.Start?.Kind == "trimmed", $"Wege nicht gelöst ({fw.Start?.Kind}, {tr.Start?.Kind}; {string.Join(" | ", warn)})");
    Check(fw.Points[0][2] > 4 && tr.Points[0][2] > 4, $"Wege nicht bis an den Fahrbahnrand gekürzt (z {fw.Points[0][2]:0.0}, {tr.Points[0][2]:0.0})");
    Check(!roads.Contains(e) || !roads.Contains(k), "kurzer Arm nicht mit seiner Fortsetzung verbunden");
    var arm = roads.Contains(k) ? k : e;
    Check(Interchanges.ArcLengthOf(arm) > 290, $"verbundener Arm {Interchanges.ArcLengthOf(arm):0} m");
    Junctions.Reset();
    var plans = Junctions.Find(roads, (_, _) => "ger1", []);
    Check(plans.Any(p => p.OsmNode == 1 && p.Def == Junctions.TR1), $"kein T-Prefab an der Kreuzung ({plans.Count} Pläne)");
    // Gegenprobe: ohne Lösen verhinderte der Fußweg das Prefab
    var w2 = R("w", [[-300, 0, 0], [0, 0, 0]], "residential", 5, 1); var e2 = R("e", [[0, 0, 0], [300, 0, 0]], "residential", 1, 6);
    var n2 = R("n", [[0, 0, 0], [0, 0, -200]], "service", 1, 7); var f2 = R("fw", [[0, 0, 0], [0, 0, 7], [-60, 0, 8]], "footway", 1, 8);
    Junctions.Reset();
    Check(!Junctions.Find([w2, e2, n2, f2], (_, _) => "ger1", []).Any(p => p.OsmNode == 1), "Prüfaufbau: mit Fußweg am Knoten sollte es kein Prefab geben");
}
