namespace Quartermaster.Render;

/// <summary>A tile pyramid on disk: 512 px PNG tiles at levels 0..<see cref="Levels"/>; level n is downscaled by 2^n.</summary>
public sealed class TileSet
{
    public const int TileSize = 512;

    public TileSet(string root, int width, int height, int levels, bool isPlaceholder = false)
    {
        Root = root; Width = width; Height = height; Levels = levels; IsPlaceholder = isPlaceholder;
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>Highest level index (levels run 0..Levels).</summary>
    public int Levels { get; }
    public string Root { get; }
    /// <summary>True when the board image could not be tiled; no tile files exist and the board should be drawn as a blank rectangle.</summary>
    public bool IsPlaceholder { get; }

    /// <summary>Highest level index for an image: the smallest n at which the whole image fits in 1024 px.</summary>
    public static int LevelsFor(int width, int height)
    {
        var n = 0;
        while (Math.Max(LevelDim(width, n), LevelDim(height, n)) > 1024) n++;
        return n;
    }

    private static int LevelDim(int v, int level) => Math.Max(1, (int)(((long)v + (1L << level) - 1) >> level));

    public int LevelWidth(int level) => LevelDim(Width, level);
    public int LevelHeight(int level) => LevelDim(Height, level);
    public int TilesX(int level) => (LevelWidth(level) + TileSize - 1) / TileSize;
    public int TilesY(int level) => (LevelHeight(level) + TileSize - 1) / TileSize;

    public string TilePath(int level, int tx, int ty) => Path.Combine(Root, level.ToString(), $"{tx}_{ty}.png");

    /// <summary>The largest n with 2^-n >= zoom, clamped to 0..Levels.</summary>
    public int ChooseLevel(float zoom)
    {
        var n = 0;
        while (n < Levels && Math.Pow(2, -(n + 1)) >= zoom - 1e-6) n++;
        return n;
    }
}
