// Field sets mirror VASSAL trait classes in VASSAL/counters/ (LGPL-2.1); field readers live in PieceType.cs.
using System.Globalization;

namespace Quartermaster.Vassal;

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    /// <summary>Parses "r,g,b" (a fourth alpha component is ignored) or "0xRRGGBB". Null, blank or malformed gives null.</summary>
    public static RgbColor? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
                ? new RgbColor((byte)(v >> 16), (byte)(v >> 8), (byte)v)
                : null;
        }
        var p = s.Split(',');
        if (p.Length < 3) return null;
        return byte.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var r)
            && byte.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var g)
            && byte.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var b)
            ? new RgbColor(r, g, b)
            : null;
    }
}

/// <summary>One layer of a piece's decorator chain. <see cref="RawType"/> is the layer's full type string.</summary>
public abstract record TraitDef(string Id, string RawType);

public sealed record BasicPieceDef(string RawType) : TraitDef("piece", RawType)
{
    public string CloneKey { get; init; } = "";
    public string DeleteKey { get; init; } = "";
    public string Image { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed record PrototypeRef(string RawType) : TraitDef("prototype", RawType)
{
    public string PrototypeName { get; init; } = "";
}

public sealed record LayerDef(string RawType) : TraitDef("emb2", RawType)
{
    public IReadOnlyList<string> Images { get; init; } = new[] { "" };
    public IReadOnlyList<string> LevelNames { get; init; } = new[] { "" };
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }
    public bool Loop { get; init; } = true;
    public bool AlwaysActive { get; init; } = true;
    public bool DrawUnderneath { get; init; }
    public string ActivateCommand { get; init; } = "";
    public string UpCommand { get; init; } = "";
    public string DownCommand { get; init; } = "";
    public string ResetCommand { get; init; } = "";
    public int ResetLevel { get; init; } = 1;
    public bool FollowProperty { get; init; }
    public string PropertyName { get; init; } = "";
    /// <summary>Value of the followed property that selects the first level (VASSAL firstLevelValue).</summary>
    public int FirstLevelValue { get; init; } = 1;
    public string Name { get; init; } = "";
}

public sealed record LabelDef(string RawType) : TraitDef("label", RawType)
{
    public string MenuCommand { get; init; } = "Change Label";
    public int FontSize { get; init; } = 10;
    public int FontStyle { get; init; }
    public string FontFamily { get; init; } = "Dialog";
    public RgbColor? Foreground { get; init; } = new RgbColor(0, 0, 0);
    public RgbColor? Background { get; init; }
    public char VerticalPos { get; init; } = 't';
    public char HorizontalPos { get; init; } = 'c';
    public int VerticalOffset { get; init; }
    public int HorizontalOffset { get; init; }
    public char VerticalJustification { get; init; } = 'b';
    public char HorizontalJustification { get; init; } = 'c';
    public string LabelFormat { get; init; } = "$pieceName$ ($label$)";
    /// <summary>The label's property name (VASSAL propertyName), which exposes the label text.</summary>
    public string Name { get; init; } = "TextLabel";
}

public sealed record MarkerDef(string RawType) : TraitDef("mark", RawType)
{
    public IReadOnlyList<string> Keys { get; init; } = Array.Empty<string>();
}

public enum SendToDestination { GridLocation, Location, Zone, Region, Counter, CounterCycle, CounterNearest }

public sealed record SendToDef(string RawType) : TraitDef("sendto", RawType)
{
    public string CommandName { get; init; } = "";
    public string MapName { get; init; } = "";
    public string BoardName { get; init; } = "";
    /// <summary>X and Y are VASSAL formatted strings (may contain $property$ references), kept as text.</summary>
    public string X { get; init; } = "0";
    public string Y { get; init; } = "0";
    public string GridLocation { get; init; } = "";
    public string ZoneName { get; init; } = "";
    public string RegionName { get; init; } = "";
    public SendToDestination DestinationType { get; init; } = SendToDestination.Location;
    public string Description { get; init; } = "";
}

public sealed record ImmobileDef(string RawType) : TraitDef("immob", RawType)
{
    public bool MoveLocked { get; init; }
    public bool SelectionLocked { get; init; }
    public bool ShiftToSelect { get; init; }
    public bool AltToSelect { get; init; }
}

public sealed record PropertySheetDef(string RawType) : TraitDef("propertysheet", RawType)
{
    public IReadOnlyList<(string Name, int Type)> Rows { get; init; } = Array.Empty<(string, int)>();
    public string MenuName { get; init; } = "";
}

public sealed record PlaceMarkerDef(string RawType) : TraitDef("placemark", RawType)
{
    public string CommandName { get; init; } = "";
    /// <summary>Raw markerSpec: either a nested piece definition (starts with "+/") or a slot path; "null" in the module becomes "".</summary>
    public string MarkerSpec { get; init; } = "";
    /// <summary>MarkerSpec when it is a slot path (does not start with "+"); otherwise "".</summary>
    public string MarkerSlotPath { get; init; } = "";
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }
    public bool AboveTarget { get; init; }
}

public sealed record CloneDef(string RawType) : TraitDef("clone", RawType)
{
    public string CommandName { get; init; } = "";
}

public sealed record DeleteDef(string RawType) : TraitDef("delete", RawType)
{
    public string CommandName { get; init; } = "";
}

public sealed record HideableDef(string RawType) : TraitDef("hide", RawType)
{
    public string CommandName { get; init; } = "Invisible";
    public double Transparency { get; init; } = 0.3;
}

public sealed record ObscurableDef(string RawType) : TraitDef("obs", RawType)
{
    public string CommandName { get; init; } = "Mask";
    public string MaskImage { get; init; } = "?";
    /// <summary>First character of the display-style token: I, B, P, G or 2.</summary>
    public char DisplayStyle { get; init; } = 'I';
    /// <summary>Remainder of the display-style token (image name for G).</summary>
    public string DisplayArg { get; init; } = "";
}

public sealed record DynamicPropertyDef(string RawType) : TraitDef("PROP", RawType)
{
    public string Key { get; init; } = "name";
    public bool Numeric { get; init; }
    public int Min { get; init; }
    public int Max { get; init; } = 100;
    public bool Wrap { get; init; }
    public IReadOnlyList<(string Menu, string Kind, string Expr)> Commands { get; init; } = Array.Empty<(string, string, string)>();
}

public sealed record RotatorDef(string RawType) : TraitDef("rotate", RawType)
{
    public int ValidAngles { get; init; } = 1;
    public string RotateCwCommand { get; init; } = "";
    public string RotateCcwCommand { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed record FootprintDef(string RawType) : TraitDef("footprint", RawType)
{
    public string CommandName { get; init; } = "Movement Trail";
    public bool InitiallyVisible { get; init; }
}

public sealed record MovementMarkableDef(string RawType) : TraitDef("markmoved", RawType)
{
    public string Image { get; init; } = "";
    public int XOffset { get; init; }
    public int YOffset { get; init; }
}

public sealed record NonRectangularDef(string RawType) : TraitDef("nonRect2", RawType)
{
    public double Scale { get; init; } = 1.0;
    public string ShapeSpec { get; init; } = "";
}

public sealed record ReplaceDef(string RawType) : TraitDef("replace", RawType)
{
    public string CommandName { get; init; } = "";
    public string MarkerSpec { get; init; } = "";
    public string MarkerSlotPath { get; init; } = "";
}

public sealed record InertTrait(string RawType) : TraitDef("return", RawType);

public sealed record UnknownTrait(string Id, string RawType) : TraitDef(Id, RawType);
