using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>UI-free edits for the workbench Visibility tab. Pure; a no-op returns the same pack instance.</summary>
public static class VisibilityEdit
{
    public static RulesPack SetPropertyVisibility(RulesPack p, string id, Visibility v)
    {
        var i = p.Properties.FindIndex(x => x.Id == id);
        if (i < 0 || p.Properties[i].Visibility == v) return p;
        var list = p.Properties.ToList();
        list[i] = list[i] with { Visibility = v };
        return p with { Properties = list };
    }

    public static RulesPack SetHolderOwner(RulesPack p, string id, string? sideId)
    {
        if (string.IsNullOrEmpty(sideId)) sideId = null;
        var i = p.Holders.FindIndex(x => x.Id == id);
        if (i < 0 || p.Holders[i].SideId == sideId) return p;
        var list = p.Holders.ToList();
        list[i] = list[i] with { SideId = sideId };
        return p with { Holders = list };
    }

    public static string? MapOwnerOf(RulesPack p, string mapId) => p.MapOwners.FirstOrDefault(m => m.MapId == mapId)?.SideId;

    public static RulesPack SetMapOwner(RulesPack p, string mapId, string? sideId)
    {
        if (string.IsNullOrEmpty(sideId)) sideId = null;
        if (MapOwnerOf(p, mapId) == sideId) return p;
        var list = p.MapOwners.Where(m => m.MapId != mapId).ToList();
        if (sideId != null) list.Add(new MapOwner { MapId = mapId, SideId = sideId });
        return p with { MapOwners = list };
    }

    public static RulesPack SetPrivateRolls(RulesPack p, string formulaId, bool on)
    {
        var i = p.Formulas.FindIndex(x => x.Id == formulaId);
        if (i < 0 || p.Formulas[i].PrivateRolls == on) return p;
        var list = p.Formulas.ToList();
        list[i] = list[i] with { PrivateRolls = on };
        return p with { Formulas = list };
    }
}
