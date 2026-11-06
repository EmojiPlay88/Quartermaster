// Ported from VASSAL Embellishment.java, FreeRotator.java, Hideable.java, Obscurable.java, DynamicProperty.java, IncrementProperty.java and Labeler.java
// (mySetState readers, myGetState writers and key-event state changes) (LGPL-2.1).
using System.Globalization;

namespace Quartermaster.Vassal;

/// <summary>
/// Pure readers and writers for per-trait state strings (the VASSAL mySetState / myGetState / myKeyEvent sides). A state of "" means the trait's default.
/// </summary>
public static class TraitState
{
    /// <summary>
    /// Embellishment: the state is one signed integer; negative means inactive, magnitude is the 1-based level.
    /// Default (no state) is active at level 1 when the layer is always active, otherwise inactive at level 1.
    /// The value is clamped to +-image count. Level 0 means no image (inactive).
    /// </summary>
    public static (int Level, bool Active) LayerLevel(LayerDef def, string? state)
    {
        var count = Math.Max(def.Images.Count, def.LevelNames.Count);
        count = Math.Max(count, 1);
        var value = string.IsNullOrEmpty(state)
            ? (def.AlwaysActive ? 1 : -1)
            : new SequenceDecoder(state, ';').NextInt(-1);
        value = Math.Min(Math.Max(-count, value), count);
        return (Math.Abs(value), value > 0);
    }

    /// <summary>
    /// FreeRotator: rotation in degrees clockwise on screen. Free rotation (ValidAngles == 1) stores the VASSAL angle
    /// (counter-clockwise positive) so the result is its negation; fixed facings store an index i giving i*360/n clockwise.
    /// </summary>
    public static double RotationAngle(RotatorDef def, string? state)
    {
        var n = Math.Max(def.ValidAngles, 1);
        if (string.IsNullOrWhiteSpace(state)) return 0;
        if (n == 1)
        {
            return double.TryParse(state.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? 0.0 - a : 0;
        }
        if (!int.TryParse(state.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var idx)) idx = 0;
        idx = Math.Min(Math.Max(idx, 0), n - 1);
        return idx * (360.0 / n);
    }

    /// <summary>Hideable: state is the hider's side/name, or "null" when visible.</summary>
    public static bool IsHidden(HideableDef def, string? state) => !string.IsNullOrEmpty(state) && state != "null";

    /// <summary>Obscurable: the first ';' token is the obscurer, or "null" when not obscured.</summary>
    public static bool IsObscured(ObscurableDef def, string? state)
    {
        if (string.IsNullOrEmpty(state)) return false;
        var first = new SequenceDecoder(state, ';').NextToken("null");
        return first != "null" && first.Length > 0;
    }

    /// <summary>Labeler: the state is the label text, trimmed, with self-references to the label's own property removed.</summary>
    public static string LabelText(LabelDef def, string? state)
    {
        var s = (state ?? "").Trim();
        if (def.Name.Length == 0) return s;
        var token = "$" + def.Name + "$";
        int i;
        while ((i = s.IndexOf(token, StringComparison.Ordinal)) >= 0) s = s.Remove(i, token.Length);
        return s;
    }

    // ---- write side ----

    private static int LayerCount(LayerDef def) => Math.Max(Math.Max(def.Images.Count, def.LevelNames.Count), 1);

    /// <summary>Embellishment myGetState: one signed integer, magnitude is the level and the sign is the active flag. The level is clamped to 1..image count (Embellishment never writes 0); the sign carries the active flag.</summary>
    public static string LayerSetLevel(LayerDef def, string? state, int level, bool active)
    {
        var l = Math.Min(Math.Max(level, 1), LayerCount(def));
        return (active ? l : -l).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Embellishment activate key: flips the sign of the value.</summary>
    public static string LayerToggleActive(LayerDef def, string? state)
    {
        var (level, active) = LayerLevel(def, state);
        return LayerSetLevel(def, state, level, !active);
    }

    /// <summary>Embellishment doIncrease / doDecrease: steps the level by +-1, wrapping when the layer loops, otherwise clamping.</summary>
    public static string LayerStep(LayerDef def, string? state, int delta)
    {
        var (level, active) = LayerLevel(def, state);
        var n = LayerCount(def);
        var v = Math.Abs(level);
        if (delta >= 0)
        {
            if (++v > n) v = def.Loop ? 1 : n;
        }
        else
        {
            if (--v < 1) v = def.Loop ? n : 1;
        }
        return LayerSetLevel(def, state, v, active);
    }

    /// <summary>Embellishment reset key: level |ResetLevel|, active when ResetLevel is positive.</summary>
    public static string LayerReset(LayerDef def, string? state) => LayerSetLevel(def, state, Math.Abs(def.ResetLevel), def.ResetLevel > 0);

    /// <summary>FreeRotator rotate CW / CCW for fixed facings: angleIndex moves by +-1 modulo the number of angles. Free rotation is returned unchanged.</summary>
    public static string RotationStep(RotatorDef def, string? state, int delta)
    {
        var n = Math.Max(def.ValidAngles, 1);
        if (n == 1) return state ?? "";
        if (!int.TryParse((state ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var idx)) idx = 0;
        idx = Math.Min(Math.Max(idx, 0), n - 1);
        idx = (((idx + delta) % n) + n) % n;
        return idx.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Hideable: toggles between visible ("null") and hidden by <paramref name="playerId"/>.</summary>
    public static string HiddenToggle(HideableDef def, string? state, string playerId) => IsHidden(def, state) ? "null" : playerId;

    /// <summary>Obscurable: toggles between not obscured ("null") and obscured by <paramref name="playerId"/> (no display options).</summary>
    public static string ObscuredToggle(ObscurableDef def, string? state, string playerId)
        => IsObscured(def, state) ? "null" : SequenceEncoder.Encode(new[] { playerId, "" }, ';');

    /// <summary>DynamicProperty: the state is the value itself.</summary>
    public static string DynamicValue(DynamicPropertyDef def, string? state) => state ?? "";

    public static string DynamicSet(DynamicPropertyDef def, string value) => value ?? "";

    /// <summary>
    /// IncrementProperty.getNewValue: adds <paramref name="incr"/> to the integer value (blank counts as 0), wrapping within
    /// Min..Max when numeric and wrapping, clamping when numeric only. A non-integer old value is returned unchanged.
    /// </summary>
    public static string DynamicIncrement(DynamicPropertyDef def, string? state, int incr)
    {
        int value;
        if (string.IsNullOrEmpty(state)) value = 0;
        else if (!int.TryParse(state, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value)) return state;

        if (!def.Numeric) value += incr;
        else if (def.Wrap)
        {
            var range = def.Max - def.Min + 1;
            if (range > 0) value = def.Min + ((value - def.Min + incr) % range + range) % range;
        }
        else
        {
            value += incr;
            value = Math.Min(def.Max, value);
            value = Math.Max(def.Min, value);
        }
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
