// SPDX-License-Identifier: GPL-2.0-or-later
using TruckLib;
using TruckLib.HashFs;
using TruckLib.Sii;

namespace Osm2Ets.Writer;

/// <summary>
/// Listet die Straßentypen (road_look-Units) aus den .scs-Archiven des Spiels.
/// Grundlage: /def/world/road_look.sii (alte Straßen) und
/// /def/world/road_look.template.sii (Vorlagen) laut map-docs von sk-zk.
/// </summary>
public static class LookLister
{
    public sealed record LookEntry(string Archive, string File, string UnitClass, string UnitName,
                                   string TokenCandidate, bool ValidToken, string Summary);

    public static List<LookEntry> List(string gameDir, string? filter, bool allAttributes, List<string> warnings,
                                       string filePrefix = "road_look")
    {
        var result = new List<LookEntry>();
        var archives = Directory.EnumerateFiles(gameDir, "*.scs", SearchOption.TopDirectoryOnly)
                                .OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (archives.Count == 0)
            warnings.Add($"Keine .scs-Dateien in {gameDir} gefunden – ist das der Spielordner?");

        foreach (var archive in archives)
        {
            IHashFsReader reader;
            try { reader = HashFsReader.Open(archive); }
            catch (Exception ex)
            {
                warnings.Add($"{Path.GetFileName(archive)}: nicht lesbar ({ex.GetType().Name}: {ex.Message})");
                continue;
            }
            using (reader)
            {
                if (reader.TryGetEntry("/def/world", out _) != EntryType.Directory)
                    continue;
                DirectoryListing listing;
                try { listing = reader.GetDirectoryListing("/def/world", filesOnly: true); }
                catch (Exception ex)
                {
                    warnings.Add($"{Path.GetFileName(archive)}: Verzeichnis /def/world nicht lesbar ({ex.Message})");
                    continue;
                }
                var files = listing.Files
                    .Where(f => Path.GetFileName(f).StartsWith(filePrefix, StringComparison.Ordinal)
                                && f.EndsWith(".sii", StringComparison.Ordinal))
                    .OrderBy(f => f, StringComparer.Ordinal);
                foreach (var file in files)
                {
                    SiiFile sii;
                    try { sii = SiiFile.Open(file, reader, ignoreMissingIncludes: true); }
                    catch (Exception ex)
                    {
                        warnings.Add($"{Path.GetFileName(archive)}{file}: nicht lesbar ({ex.GetType().Name}: {ex.Message})");
                        continue;
                    }
                    foreach (var unit in sii.Units)
                    {
                        if (filter is not null
                            && !unit.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                            && !Summarize(unit, true).Contains(filter, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var candidate = unit.Name.Contains('.') ? unit.Name[(unit.Name.LastIndexOf('.') + 1)..] : unit.Name;
                        result.Add(new LookEntry(Path.GetFileName(archive), file, unit.Class, unit.Name,
                            candidate, Token.IsValidToken(candidate), Summarize(unit, allAttributes)));
                    }
                }
            }
        }
        return result;
    }

    private static string Summarize(Unit unit, bool all)
    {
        IEnumerable<KeyValuePair<string, dynamic>> attrs = unit.Attributes;
        if (!all)
            attrs = attrs.Where(a => a.Key is "name" or "lanes_left" or "lanes_right" or "road_size" or "road_offset"
                                     or "model_desc" or "category");
        return string.Join("; ", attrs.Select(a => $"{a.Key}={Format((object?)a.Value)}"));
    }

    private static string Format(object? value)
    {
        var text = value switch
        {
            null => "null",
            string s => s,
            System.Collections.IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Format)) + "]",
            _ => value.ToString() ?? "",
        };
        return text.Length > 80 ? text[..77] + "..." : text;
    }
}
