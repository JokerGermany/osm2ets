// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

public static class Program
{
    private const string Usage = """
        osm2ets-writer – schreibt das osm2ets-Zwischenformat als ETS2-Karte (TruckLib, Kartenformat 907 = ETS2 1.59–1.61)

          write  --input <roads.ets.json> --out <ordner> [--name <kartenname>] [--mapping <mapping.jsonc>]
                 [--force] [--verify] [--no-layer-files] [--game <ETS2-Spielordner> | --prefab-dir <ordner>]
                 Mit --game (oder entpackten .ppd-Dateien in --prefab-dir) werden an Autobahn-
                 Anschlussstellen SCS-Prefabs eingesetzt.
                 Schreibt <ordner>/<kartenname>.mbd und den Sektorordner <ordner>/<kartenname>/.
                 Achtung: Mit --force werden vorhandene Sektordateien (.base, .aux, .data, .desc, .snd, .layer)
                 in diesem Sektorordner gelöscht.
                 Standardmäßig wird je Sektor eine leere .layer-Datei angelegt (--no-layer-files: nicht).
          looks  --game <ETS2-Spielordner> [--filter <text>] [--all]
                 Listet die Straßentypen (road_look) aus den .scs-Archiven, um mapping.jsonc zu füllen.
          prefabs --game <ETS2-Spielordner> [--filter <text>] [--all]
                 Listet Prefabs (/def/world/prefab*.sii) – z. B. Spurübergänge und Anschlussstellen.
          extract --game <ETS2-Spielordner> --paths <datei mit Pfaden> --out <ordner>
                 Kopiert einzelne Dateien (z. B. /prefab2/…/x.ppd) aus den .scs-Archiven.
          info   --map <karte.mbd>
                 Öffnet eine Karte und zeigt Kennzahlen.

        Rückgabewerte von write: 0 = alles geschrieben, 1 = geschrieben, aber Straßen übersprungen,
        2 = Aufruf-/Eingabefehler, 3 = Prüfung (--verify) fehlgeschlagen, 4 = nichts geschrieben.
        """;

    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }
        try
        {
            var opts = ParseOptions(args.Skip(1).ToArray());
            return args[0] switch
            {
                "write" => Write(opts),
                "looks" => Looks(opts, "road_look"),
                "prefabs" => Looks(opts, "prefab"),
                "extract" => Extract(opts),
                "info" => Info(opts),
                _ => throw new UsageException($"Unbekannter Befehl '{args[0]}'"),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}\n");
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException
                                       or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            return 2;
        }
    }

    private sealed class UsageException(string message) : Exception(message);

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var flags = new HashSet<string> { "--force", "--verify", "--all", "--no-layer-files" };
        var result = new Dictionary<string, string?>();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--"))
                throw new UsageException($"Unerwartetes Argument '{a}'");
            if (flags.Contains(a))
            {
                result[a] = null;
                continue;
            }
            if (i + 1 >= args.Length)
                throw new UsageException($"{a} braucht einen Wert");
            result[a] = args[++i];
        }
        return result;
    }

    private static string Required(Dictionary<string, string?> o, string key) =>
        o.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : throw new UsageException($"{key} fehlt");

    private static int Write(Dictionary<string, string?> o)
    {
        var inputPath = Required(o, "--input");
        var outDir = Required(o, "--out");
        var name = o.GetValueOrDefault("--name") ?? "osm2ets";
        if (!TruckLib.Token.IsValidToken(name))
            throw new UsageException($"--name '{name}': nur a–z, 0–9 und _ (höchstens 12 Zeichen)");
        var mappingPath = o.GetValueOrDefault("--mapping") ?? Path.Combine(AppContext.BaseDirectory, "mapping.jsonc");
        if (!File.Exists(mappingPath))
            throw new UsageException($"Zuordnungsdatei nicht gefunden: {mappingPath}");

        var mbd = Path.Combine(outDir, name + ".mbd");
        var sectorDir = Path.Combine(outDir, name);
        if (!o.ContainsKey("--force") && (File.Exists(mbd) || Directory.Exists(sectorDir)))
            throw new UsageException($"{mbd} bzw. {sectorDir} existiert schon. Mit --force überschreiben " +
                                     "(vorhandene Sektordateien darin werden dabei gelöscht).");

        var input = IntermediateFile.Load(inputPath);
        var mapping = MappingConfig.Load(mappingPath);
        Console.WriteLine($"Eingabe:  {inputPath} ({input.Roads.Count} Straßen, Stufe {input.Lod?.Name}, " +
                          $"Maßstab 1:{input.Projection.Scale:0.###})");
        Console.WriteLine($"Zuordnung: {mappingPath}");

        using var prefabs = (o.ContainsKey("--prefab-dir") || o.ContainsKey("--game"))
            ? new Interchanges.PpdSource(o.GetValueOrDefault("--prefab-dir"), o.GetValueOrDefault("--game"))
            : null;
        var (map, result) = MapWriter.Build(input, mapping, prefabs);
        if (result.RoadsWritten == 0)
        {
            Console.Error.WriteLine("Fehler: keine einzige Straße schreibbar – Karte nicht gespeichert.");
            foreach (var w in result.Warnings.Take(20))
                Console.Error.WriteLine($"  {w}");
            return 4;
        }
        Directory.CreateDirectory(outDir);
        map.Save(outDir, name, true);
        int layerFiles = o.ContainsKey("--no-layer-files") ? 0 : MapWriter.EnsureLayerFiles(sectorDir);

        Console.WriteLine($"Geschrieben: {mbd}");
        Console.WriteLine($"  Straßen {result.RoadsWritten}, übersprungen {result.RoadsSkipped}, " +
                          $"Straßenstücke {result.Items}, Typwechsel {result.StyleChanges}");
        Console.WriteLine($"  Verbindungen an Knoten {result.Connections} (umgedrehte Zweirichtungsstraßen {result.Reversed})");
        Console.WriteLine($"  Gelände wegen Nachbarstraßen verkleinert: {result.TerrainClamped} Abschnitte");
        Console.WriteLine($"  Sektoren {Directory.EnumerateFiles(sectorDir, "*.base").Count()}, " +
                          $"leere .layer-Dateien angelegt: {layerFiles}");
        Console.WriteLine($"  Maßstab NormalScale={map.NormalScale:0.###} CityScale={map.CityScale:0.###}");
        Console.WriteLine($"  Startpunkt (Run Map): {result.StartPosition} auf {result.StartRoad ?? "-"}");
        foreach (var w in result.Warnings.Take(50))
            Console.WriteLine($"  Warnung: {w}");
        if (result.Warnings.Count > 50)
            Console.WriteLine($"  ... und {result.Warnings.Count - 50} weitere Warnungen");

        if (o.ContainsKey("--verify"))
        {
            var reopened = Map.Open(mbd);
            bool ok = reopened.MapItems.Count == map.MapItems.Count && reopened.Nodes.Count == map.Nodes.Count;
            Console.WriteLine($"Prüfung: erneut geladen – {reopened.MapItems.Count} Items, {reopened.Nodes.Count} Knoten " +
                              (ok ? "(stimmt überein)" : $"(ABWEICHUNG: erwartet {map.MapItems.Count}/{map.Nodes.Count})"));
            if (!ok) return 3;
        }
        Console.WriteLine("Nächster Schritt: Karte im ETS2-Editor öffnen und 'Map > Recompute map' ausführen " +
                          "(TruckLib berechnet keine Bounding-Boxes).");
        return result.RoadsSkipped > 0 ? 1 : 0;
    }

    private static int Looks(Dictionary<string, string?> o, string prefix)
    {
        var gameDir = Required(o, "--game");
        if (!Directory.Exists(gameDir))
            throw new UsageException($"Ordner nicht gefunden: {gameDir}");
        var warnings = new List<string>();
        var looks = LookLister.List(gameDir, o.GetValueOrDefault("--filter"), o.ContainsKey("--all"), warnings, prefix);
        Console.WriteLine("Archiv\tDatei\tKlasse\tUnit\tToken\tAttribute");
        foreach (var l in looks)
            Console.WriteLine($"{l.Archive}\t{l.File}\t{l.UnitClass}\t{l.UnitName}\t" +
                              $"{(l.ValidToken ? l.TokenCandidate : "(" + l.TokenCandidate + "?)")}\t{l.Summary}");
        foreach (var w in warnings)
            Console.Error.WriteLine($"Warnung: {w}");
        Console.Error.WriteLine($"{looks.Count} Einträge.");
        return 0;
    }

    private static int Extract(Dictionary<string, string?> o)
    {
        var gameDir = Required(o, "--game");
        var listFile = Required(o, "--paths");
        var outDir = Required(o, "--out");
        var wanted = File.ReadAllLines(listFile).Select(l => l.Trim()).Where(l => l.StartsWith('/')).Distinct().ToList();
        var archives = Directory.EnumerateFiles(gameDir, "*.scs").OrderBy(p => p, StringComparer.Ordinal).ToList();
        var found = new HashSet<string>();
        foreach (var archive in archives)
        {
            TruckLib.HashFs.IHashFsReader reader;
            try { reader = TruckLib.HashFs.HashFsReader.Open(archive); } catch { continue; }
            using (reader)
            {
                foreach (var path in wanted)
                {
                    if (found.Contains(path)) continue;
                    if (reader.TryGetEntry(path, out var entry) != TruckLib.HashFs.EntryType.File) continue;
                    var data = reader.Extract(entry, path);
                    var target = Path.Combine(outDir, path.TrimStart('/'));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, data[0]);
                    found.Add(path);
                    Console.WriteLine($"{Path.GetFileName(archive)}\t{path}\t{data[0].Length} Bytes");
                }
            }
        }
        foreach (var p in wanted.Where(p => !found.Contains(p)))
            Console.Error.WriteLine($"nicht gefunden: {p}");
        return found.Count == wanted.Count ? 0 : 1;
    }

    private static int Info(Dictionary<string, string?> o)
    {
        var path = Required(o, "--map");
        if (!File.Exists(path))
            throw new UsageException($"Datei nicht gefunden: {path}");
        var map = Map.Open(path);
        Console.WriteLine($"Karte: {path}");
        Console.WriteLine($"  Items {map.MapItems.Count}, Knoten {map.Nodes.Count}, Sektoren {map.Sectors.Count}");
        foreach (var g in map.MapItems.Values.GroupBy(i => i.ItemType).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key}: {g.Count()}");
        var roadTypes = map.MapItems.Values.OfType<Road>().GroupBy(r => r.RoadType.ToString())
                                    .OrderByDescending(g => g.Count());
        foreach (var g in roadTypes)
            Console.WriteLine($"  Straßentyp {g.Key}: {g.Count()}");
        // Steigungen der Straßenstücke (Kontrolle für Höhenstufen)
        var grades = map.MapItems.Values.OfType<Road>().Select(r =>
        {
            var d = r.ForwardNode.Position - r.Node.Position;
            float h = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
            return (Grade: h > 0 ? MathF.Abs(d.Y) / h : 0, Len: h, Pos: r.Node.Position);
        }).OrderByDescending(g => g.Grade).ToList();
        if (grades.Count > 0)
        {
            Console.WriteLine($"  Steigung: max {grades[0].Grade:P1} bei {grades[0].Pos} ({grades[0].Len:0.0} m), " +
                              $"> 8 %: {grades.Count(g => g.Grade > 0.08)}, > 12 %: {grades.Count(g => g.Grade > 0.12)}");
            foreach (var g in grades.Take(5)) Console.WriteLine($"    {g.Grade:P1}  {g.Len,6:0.0} m  {g.Pos}");
        }
        Console.WriteLine($"  NormalScale {map.NormalScale}, CityScale {map.CityScale}");
        Console.WriteLine($"  Startpunkt {map.StartPosition}");
        return 0;
    }
}
