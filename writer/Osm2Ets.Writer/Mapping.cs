// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using System.Text.RegularExpressions;
using TruckLib;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Zuordnung OSM-Attribute → ETS2-Straßentyp. Wird aus einer JSONC-Datei gelesen
/// (Kommentare mit // erlaubt).
/// </summary>
public sealed class MappingConfig
{
    public StyleSpec Default { get; set; } = new();
    public List<RuleSpec> Rules { get; set; } = [];
    /// <summary>Überschreibt Werte auf Brücken (z. B. kein Gelände).</summary>
    public StyleSpec? Bridge { get; set; }
    /// <summary>Überschreibt Werte in Tunneln.</summary>
    public StyleSpec? Tunnel { get; set; }
    /// <summary>Überschreibt Werte auf Wegen ohne Kfz-Zugang (access=private|no), zusätzlich zur Regel.</summary>
    public StyleSpec? Restricted { get; set; }
    /// <summary>Überschreibt Werte auf Sichtstücken (Rolle "visible": Straßen höherer Stufen, die man von der
    /// Strecke aus sieht – nur ein Stück davon, mit offenem Ende), zusätzlich zur Regel.</summary>
    public StyleSpec? Visible { get; set; }
    /// <summary>Brückenkörper für Paare paralleler Autobahnbrücken über Straßen (siehe BridgeBodies).</summary>
    public BridgeBodySpec? BridgeBody { get; set; }
    /// <summary>Brückenkörper je Straßentyp für alle übrigen Brücken ("*" = alle anderen Typen).</summary>
    public Dictionary<string, BridgeBodyType>? BridgeBodies { get; set; }
    public MapSpec Map { get; set; } = new();

    // Strenger als beim Zwischenformat: unbekannte Felder (Tippfehler) sind Fehler.
    private static readonly JsonSerializerOptions StrictOptions = new(IntermediateFile.JsonOptions)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public static MappingConfig Load(string path)
    {
        var text = File.ReadAllText(path);
        MappingConfig? cfg;
        try { cfg = JsonSerializer.Deserialize<MappingConfig>(text, StrictOptions); }
        catch (JsonException ex) { throw new InvalidDataException($"{path}: {ex.Message}", ex); }
        if (cfg is null)
            throw new InvalidDataException($"{path}: leere Zuordnung");
        cfg.Validate(path);
        return cfg;
    }

    public void Validate(string source)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(Default.RoadType))
            errors.Add("default.road_type fehlt");
        Default.Validate("default", errors);
        for (int i = 0; i < Rules.Count; i++)
        {
            var r = Rules[i];
            var name = $"rules[{i}]" + (r.Name is null ? "" : $" ({r.Name})");
            r.Validate(name, errors);
            if (r.Match is null)
                errors.Add($"{name}: match fehlt");
            else
                r.Match.Validate(name, errors);
        }
        Bridge?.Validate("bridge", errors);
        Tunnel?.Validate("tunnel", errors);
        Restricted?.Validate("restricted", errors);
        Visible?.Validate("visible", errors);
        BridgeBody?.Validate("bridge_body", errors);
        foreach (var (type, b) in BridgeBodies ?? [])
            b.Validate($"bridge_bodies.{type}", errors);
        if (errors.Count > 0)
            throw new InvalidDataException($"{source}:\n  " + string.Join("\n  ", errors));
    }

    public const string VisibleRole = "visible";

    /// <summary>Brückenkörper für ein Brückenstück dieses Stils (Typ, sonst "*"), oder null.</summary>
    public BridgeBodyType? BodyFor(ResolvedStyle style)
    {
        if (BridgeBodies is null) return null;
        if (!BridgeBodies.TryGetValue(style.RoadType, out var b) && !BridgeBodies.TryGetValue("*", out b)) return null;
        if (b.Railing.Length == 0) return null;
        if (b.MaxWidth is float w && style.Width > w) return null;
        return b;
    }

    /// <summary>Bestimmt den Stil für ein Straßenstück mit den Attributen des Laufs.</summary>
    public ResolvedStyle Resolve(RunEntry run)
    {
        var style = Default.ToResolved(null);
        foreach (var rule in Rules)
        {
            if (rule.Match!.Matches(run))
            {
                style = rule.ToResolved(style);
                break;
            }
        }
        if (run.Bridge && Bridge is not null)
            style = Bridge.ToResolved(style);
        else if (run.Tunnel && Tunnel is not null)
            style = Tunnel.ToResolved(style);
        if (run.Access is not null && Restricted is not null)
            style = Restricted.ToResolved(style);
        if (run.Role == VisibleRole && Visible is not null)
            style = Visible.ToResolved(style);
        return style;
    }
}

/// <summary>Brückenkörper (Railing mit Überbau, Widerlagern, Brüstung) für einen Straßentyp.</summary>
public sealed class BridgeBodyType
{
    /// <summary>Railing-Token; leer = ohne Körper.</summary>
    public string Railing { get; set; } = "";
    /// <summary>Straßenseite, an der das Railing sitzt (das Modell ist zur Außenseite hin gespiegelt).</summary>
    public string Side { get; set; } = "left";
    /// <summary>Versatz ab der Straßenlinie (m).</summary>
    public float Offset { get; set; }
    /// <summary>Nur für Straßen bis zu dieser Modellbreite (m), z. B. weil die Brüstungen sonst auf der Fahrbahn stünden.</summary>
    public float? MaxWidth { get; set; }
    /// <summary>Das Modell hat eigene Brüstungen: kein zusätzliches Geländer.</summary>
    public bool Parapet { get; set; } = true;

    internal void Validate(string name, List<string> errors)
    {
        if (Railing.Length > 0) StyleSpec.CheckToken(Railing, $"{name}.railing", errors);
        if (Side is not ("left" or "right")) errors.Add($"{name}.side: left oder right erwartet");
        if (Offset is < -30 or > 30) errors.Add($"{name}.offset: −30–30 erwartet");
    }
}

public sealed class BridgeBodySpec
{
    /// <summary>Railing-Token des Brückenkörpers (SCS-Karte: dlc_it_8, Italia-DLC).</summary>
    public string Railing { get; set; } = "";
    /// <summary>Versatz über den halben Abstand der Fahrbahnlinien hinaus zur Gegenfahrbahn (m).</summary>
    public float ExtraOffset { get; set; } = 2f;
    /// <summary>Abstand der Fahrbahnlinien (m), in dem zwei Brücken als Paar gelten.</summary>
    public float MinGap { get; set; } = 12f;
    public float MaxGap { get; set; } = 30f;
    /// <summary>Längere Brücken bekommen keinen Körper (m).</summary>
    public float MaxLength { get; set; } = 200f;
    /// <summary>So weit dürfen Anfang und Ende der beiden Brücken längs auseinanderliegen (m).</summary>
    public float MaxEndOffset { get; set; } = 8f;

    internal void Validate(string name, List<string> errors)
    {
        if (Railing.Length == 0) errors.Add($"{name}.railing fehlt");
        StyleSpec.CheckToken(Railing, $"{name}.railing", errors);
        if (MinGap < 0 || MaxGap < MinGap) errors.Add($"{name}: min_gap ≤ max_gap erwartet");
        if (ExtraOffset is < -20 or > 20) errors.Add($"{name}.extra_offset: −20–20 erwartet");
    }
}

public sealed class MapSpec
{
    /// <summary>Kartenmaßstab außerorts; null = Maßstab aus dem Zwischenformat.</summary>
    public float? NormalScale { get; set; }
    /// <summary>Kartenmaßstab innerorts; null = Maßstab aus dem Zwischenformat.</summary>
    public float? CityScale { get; set; }
}

public class StyleSpec
{
    public string? RoadType { get; set; }
    public float? TerrainLeft { get; set; }
    public float? TerrainRight { get; set; }
    /// <summary>Ungefähre Fahrbahnbreite in Metern (für den Geländeabstand zu Nachbarstraßen).</summary>
    public float? Width { get; set; }
    /// <summary>Seitliche Verschiebung der ETS2-Straßenlinie gegenüber der OSM-Linie (m, + = rechts in
    /// Fahrtrichtung), damit die Fahrspuren des Modells mittig auf der OSM-Linie liegen.</summary>
    public float? Offset { get; set; }
    /// <summary>Abstand des linken Modellrands von der OSM-Linie (m) – für Gegenfahrbahnen.</summary>
    public float? EdgeLeft { get; set; }
    /// <summary>Mitte des Modells rechts der ETS2-Straßenlinie (m), z. B. ger13 +1,75 (−4,5…8,0).</summary>
    public float? ModelCenter { get; set; }
    /// <summary>Unsichtbare Begrenzung am Straßenrand (ETS2 "boundary").</summary>
    public bool? Boundary { get; set; }
    /// <summary>Auf der Karte/im Navi sichtbar (ETS2 "show in UI map"); false für Fuß-/Radwege usw.</summary>
    public bool? ShowInUiMap { get; set; }
    /// <summary>Navi meidet die Straße (ETS2 "GPS avoid").</summary>
    public bool? GpsAvoid { get; set; }
    /// <summary>Geheimstraße (ETS2 "secret"): erst nach dem Befahren auf der Karte.</summary>
    public bool? Secret { get; set; }
    /// <summary>KI-Verkehr auf dieser Straße.</summary>
    public bool? AiVehicles { get; set; }
    public string? Resolution { get; set; }
    public SideSpec? Left { get; set; }
    public SideSpec? Right { get; set; }
    /// <summary>Geländer an beiden Modellrändern (Railing-Token, z. B. für Brücken), "" = keins.</summary>
    public string? Railing { get; set; }
    /// <summary>Abstand des Geländers vom Modellrand nach außen (m).</summary>
    public float? RailingOffset { get; set; }
    /// <summary>Geländer auch am linken Rand (false für Richtungsfahrbahnen: links liegt der
    /// Mittelstreifen bzw. der Brückenkörper – in der SCS-Karte nur außen).</summary>
    public bool? RailingLeft { get; set; }

    internal void Validate(string name, List<string> errors)
    {
        CheckToken(RoadType, $"{name}.road_type", errors);
        if (Railing is { Length: > 0 }) CheckToken(Railing, $"{name}.railing", errors);
        if (RailingOffset is < -5 or > 10) errors.Add($"{name}.railing_offset: −5–10 erwartet");
        if (TerrainLeft is < 0 or > 1000) errors.Add($"{name}.terrain_left: 0–1000 erwartet");
        if (TerrainRight is < 0 or > 1000) errors.Add($"{name}.terrain_right: 0–1000 erwartet");
        if (Width is <= 0 or > 60) errors.Add($"{name}.width: 0–60 erwartet");
        if (Offset is < -20 or > 20) errors.Add($"{name}.offset: −20–20 erwartet");
        if (EdgeLeft is < 0 or > 40) errors.Add($"{name}.edge_left: 0–40 erwartet");
        if (Resolution is not null && !Enum.TryParse<RoadResolution>(Resolution, true, out _))
            errors.Add($"{name}.resolution: erlaubt sind {string.Join(", ", Enum.GetNames<RoadResolution>())}");
        Left?.Validate($"{name}.left", errors);
        Right?.Validate($"{name}.right", errors);
    }

    internal static void CheckToken(string? value, string name, List<string> errors)
    {
        if (value is not null && !Token.IsValidToken(value))
            errors.Add($"{name}: '{value}' ist kein gültiger Token (höchstens 12 Zeichen aus a–z, 0–9, _)");
    }

    internal ResolvedStyle ToResolved(ResolvedStyle? baseStyle)
    {
        return new ResolvedStyle(
            RoadType ?? baseStyle?.RoadType ?? throw new InvalidOperationException("road_type fehlt"),
            TerrainLeft ?? baseStyle?.TerrainLeft ?? 0f,
            TerrainRight ?? baseStyle?.TerrainRight ?? 0f,
            Resolution is not null ? Enum.Parse<RoadResolution>(Resolution, true)
                                   : baseStyle?.Resolution ?? RoadResolution.HighPoly,
            SideSpec.Merge(baseStyle?.Left, Left),
            SideSpec.Merge(baseStyle?.Right, Right),
            Width ?? baseStyle?.Width ?? 8f,
            Boundary ?? baseStyle?.Boundary ?? true,
            Offset ?? (RoadType is null ? baseStyle?.Offset : null) ?? 0f,
            EdgeLeft ?? (RoadType is null && Width is null ? baseStyle?.EdgeLeftRaw : null) ?? -1f,
            ModelCenter ?? (RoadType is null ? baseStyle?.ModelCenter : null) ?? 0f,
            ShowInUiMap ?? baseStyle?.ShowInUiMap,
            GpsAvoid ?? baseStyle?.GpsAvoid,
            Secret ?? baseStyle?.Secret,
            AiVehicles ?? baseStyle?.AiVehicles,
            Railing is not null ? (Railing.Length > 0 ? Railing : null) : baseStyle?.Railing,
            RailingOffset ?? baseStyle?.RailingOffset ?? 1f,
            RailingLeft: RailingLeft ?? baseStyle?.RailingLeft ?? true);
    }
}

public sealed class RuleSpec : StyleSpec
{
    public string? Name { get; set; }
    public MatchSpec? Match { get; set; }
}

public sealed class MatchSpec
{
    public List<string>? Highway { get; set; }
    public List<string>? Role { get; set; }
    public bool? Oneway { get; set; }
    public bool? Bridge { get; set; }
    public bool? Tunnel { get; set; }
    public int? LanesMin { get; set; }
    public int? LanesMax { get; set; }
    /// <summary>Regulärer Ausdruck auf das OSM-ref-Tag.</summary>
    public string? Ref { get; set; }
    /// <summary>OSM service=* (z. B. parking_aisle, driveway).</summary>
    public List<string>? Service { get; set; }
    /// <summary>Gesperrter Kfz-Zugang (private, no); "none" = frei.</summary>
    public List<string>? Access { get; set; }

    private Regex? refRegex;

    internal void Validate(string name, List<string> errors)
    {
        if (Ref is not null)
        {
            try { refRegex = new Regex(Ref, RegexOptions.CultureInvariant); }
            catch (ArgumentException ex) { errors.Add($"{name}.match.ref: {ex.Message}"); }
        }
    }

    public bool Matches(RunEntry run)
    {
        if (Highway is not null && !Highway.Contains(run.Highway)) return false;
        if (Role is not null && !Role.Contains(run.Role)) return false;
        if (Oneway is not null && Oneway != run.Oneway) return false;
        if (Bridge is not null && Bridge != run.Bridge) return false;
        if (Tunnel is not null && Tunnel != run.Tunnel) return false;
        if (LanesMin is not null && (run.Lanes is null || run.Lanes < LanesMin)) return false;
        if (LanesMax is not null && (run.Lanes is null || run.Lanes > LanesMax)) return false;
        if (refRegex is not null && !refRegex.IsMatch(run.Ref ?? "")) return false;
        if (Service is not null && (run.Service is null || !Service.Contains(run.Service))) return false;
        if (Access is not null && !Access.Contains(run.Access ?? "none")) return false;
        return true;
    }
}

public sealed record SideSpec
{
    public string? Variant { get; init; }
    public string? Look { get; init; }
    public string? TrafficRule { get; init; }
    public string? LeftEdge { get; init; }
    public string? RightEdge { get; init; }

    internal void Validate(string name, List<string> errors)
    {
        StyleSpec.CheckToken(Variant, $"{name}.variant", errors);
        StyleSpec.CheckToken(Look, $"{name}.look", errors);
        StyleSpec.CheckToken(TrafficRule, $"{name}.traffic_rule", errors);
        StyleSpec.CheckToken(LeftEdge, $"{name}.left_edge", errors);
        StyleSpec.CheckToken(RightEdge, $"{name}.right_edge", errors);
    }

    internal static SideSpec? Merge(SideSpec? a, SideSpec? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return new SideSpec
        {
            Variant = b.Variant ?? a.Variant,
            Look = b.Look ?? a.Look,
            TrafficRule = b.TrafficRule ?? a.TrafficRule,
            LeftEdge = b.LeftEdge ?? a.LeftEdge,
            RightEdge = b.RightEdge ?? a.RightEdge,
        };
    }

    internal void ApplyTo(RoadSide side)
    {
        if (Variant is not null) side.Variant = Variant;
        if (Look is not null) side.Look = Look;
        if (TrafficRule is not null) side.TrafficRule = TrafficRule;
        if (LeftEdge is not null) side.LeftEdge = LeftEdge;
        if (RightEdge is not null) side.RightEdge = RightEdge;
    }
}

/// <summary>Fertig aufgelöster Stil eines Straßenstücks (Wertgleichheit für Folgestücke).</summary>
public sealed record ResolvedStyle(
    string RoadType,
    float TerrainLeft,
    float TerrainRight,
    RoadResolution Resolution,
    SideSpec? Left,
    SideSpec? Right,
    float Width = 8f,
    bool Boundary = true,
    float Offset = 0f,
    float EdgeLeftRaw = -1f,
    float ModelCenter = 0f,
    bool? ShowInUiMap = null,
    bool? GpsAvoid = null,
    bool? Secret = null,
    bool? AiVehicles = null,
    string? Railing = null,
    float RailingOffset = 1f,
    string? Body = null,
    float BodyOffset = 0f,
    bool RailingLeft = true,
    bool BodyRight = false,
    bool BodyParapet = false)
{
    /// <summary>Linker Modellrand ab OSM-Linie; ohne Angabe halbe Breite.</summary>
    public float EdgeLeft => EdgeLeftRaw >= 0 ? EdgeLeftRaw : Width / 2;
}
