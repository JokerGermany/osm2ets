// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;

namespace Osm2Ets.Writer;

/// <summary>Zwischenformat von osm2ets (osm2ets/intermediate, Version 1).</summary>
public sealed class IntermediateFile
{
    public const string ExpectedFormat = "osm2ets/intermediate";
    public const int ExpectedVersion = 1;

    public string Format { get; set; } = "";
    public int Version { get; set; }
    public string? Generator { get; set; }
    public string? Profile { get; set; }
    public LodInfo? Lod { get; set; }
    public ProjectionInfo Projection { get; set; } = new();
    public List<RoadEntry> Roads { get; set; } = [];
    public List<JunctionEntry> Junctions { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IntermediateFile Load(string path)
    {
        using var stream = File.OpenRead(path);
        var file = JsonSerializer.Deserialize<IntermediateFile>(stream, JsonOptions)
                   ?? throw new InvalidDataException($"{path}: leere Datei");
        if (file.Format != ExpectedFormat)
            throw new InvalidDataException($"{path}: unbekanntes Format '{file.Format}' (erwartet {ExpectedFormat})");
        if (file.Version != ExpectedVersion)
            throw new InvalidDataException($"{path}: Formatversion {file.Version} wird nicht unterstützt (erwartet {ExpectedVersion})");
        return file;
    }
}

public sealed class LodInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class ProjectionInfo
{
    public string Crs { get; set; } = "";
    public double Scale { get; set; } = 1;
    public double VerticalScale { get; set; } = 1;
}

public sealed class EndInfo
{
    public string Kind { get; set; } = "";
    public long? OsmNode { get; set; }
}

public sealed class RoadEntry
{
    public string Id { get; set; } = "";
    public int Chain { get; set; }
    public int Piece { get; set; }
    public string Role { get; set; } = "";
    public bool Closed { get; set; }
    public EndInfo? Start { get; set; }
    public EndInfo? End { get; set; }
    public double LengthM { get; set; }
    public List<double[]> Points { get; set; } = [];
    public List<RunEntry> Runs { get; set; } = [];
}

public sealed class RunEntry
{
    public int From { get; set; }
    public int To { get; set; }
    public string Highway { get; set; } = "";
    public string Role { get; set; } = "";
    public bool Oneway { get; set; }
    public int? Lanes { get; set; }
    public bool Bridge { get; set; }
    public bool Tunnel { get; set; }
    public int Layer { get; set; }
    public string? Ref { get; set; }
    public string? Maxspeed { get; set; }
    public List<long> OsmWays { get; set; } = [];
}

public sealed class JunctionEntry
{
    public int Id { get; set; }
    public long OsmNode { get; set; }
    public string Kind { get; set; } = "";
    public int Degree { get; set; }
    public double[] Position { get; set; } = [];
}
