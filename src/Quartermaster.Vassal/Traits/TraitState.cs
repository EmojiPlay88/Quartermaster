// Ported from VASSAL Embellishment.java, FreeRotator.java, Hideable.java, Obscurable.java and Labeler.java (mySetState readers) (LGPL-2.1).
using System.Globalization;

namespace Quartermaster.Vassal;

/// <summary>
/// Pure readers for per-trait state strings (the VASSAL mySetState side). A state of "" means the trait's default.
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
}
