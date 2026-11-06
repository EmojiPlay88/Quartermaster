using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>How the map grid overlay is shown by default.</summary>
public enum GridMode { ModuleDefault, Always, Never }

/// <summary>User settings. Immutable; <see cref="SettingsStore"/> loads, saves and announces changes.</summary>
public sealed record Settings
{
    static readonly IReadOnlyDictionary<string, bool> NoToolbar = new Dictionary<string, bool>();
    static readonly IReadOnlyDictionary<string, string> NoShortcuts = new Dictionary<string, string>();

    // General
    public bool ReopenLastModule { get; init; }
    public int AutosaveMinutes { get; init; } = 5;
    public bool ConfirmDelete { get; init; } = true;
    public bool GameMasterMode { get; init; }

    // Display
    public GridMode GridDefault { get; init; } = GridMode.ModuleDefault;
    /// <summary>Piece label size in points; 8 keeps the sizes the module asks for, other values scale them.</summary>
    public int LabelFontSize { get; init; } = 8;
    /// <summary>Selection outline colour, "#RRGGBB".</summary>
    public string HighlightColor { get; init; } = "#FFD700";
    public int TabMinWidth { get; init; } = 80;
    public int TabMaxWidth { get; init; } = 160;

    // Map
    public double WheelZoomStep { get; init; } = 1.2;
    public bool SnapOnDrop { get; init; } = true;
    public int DragThresholdPx { get; init; } = 4;
    public int HoverDelayMs { get; init; } = 600;

    // Dice & Log
    public bool PhysicalDiceDefault { get; init; }
    public bool ShowLastRollInStatus { get; init; } = true;
    public bool LogShowMoves { get; init; } = true;
    public string LogTimeFormat { get; init; } = "HH:mm:ss";

    // Game
    /// <summary>Difficulty preselected at game setup and shown in Settings → Game.</summary>
    public Difficulty LastDifficulty { get; init; } = Difficulty.SemiAuto;

    // Toolbar / Keyboard
    /// <summary>Toolbar item id to visibility; a missing key means visible.</summary>
    public IReadOnlyDictionary<string, bool> ToolbarVisible { get; init; } = NoToolbar;
    /// <summary>Command id to key string; "" means unbound.</summary>
    public IReadOnlyDictionary<string, string> ShortcutOverrides { get; init; } = NoShortcuts;

    // Global layout
    /// <summary>Fraction of the left panel given to the module tree.</summary>
    public double LeftSplitterDistance { get; init; } = 0.6;
    public string? LastModulePath { get; init; }

    public bool IsToolbarVisible(string id) => !ToolbarVisible.TryGetValue(id, out var v) || v;

    /// <summary>Selection colour as RGB; falls back to the default gold when <see cref="HighlightColor"/> is malformed.</summary>
    public (byte R, byte G, byte B) HighlightRgb => SettingsStore.TryParseColor(HighlightColor, out var c) ? c : ((byte)0xFF, (byte)0xD7, (byte)0x00);
}
