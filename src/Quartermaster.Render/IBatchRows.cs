namespace Quartermaster.Render;

/// <summary>
/// What a batch grid window reads and does, whichever grid it shows: the rows of a pending run (<see cref="BatchModel"/>) or a menu batch
/// over a selection, a stack or repeated runs of one counter (<see cref="MenuBatchModel"/>, B4).
/// </summary>
public interface IBatchRows
{
    /// <summary>The window title.</summary>
    string Title { get; }

    /// <summary>The variable columns, in binding order.</summary>
    IReadOnlyList<string> Variables { get; }

    int RowCount { get; }

    BatchRowView Row(int i);

    /// <summary>The dropdown of a column for one row (its counter's choices and candidates); null = free text.</summary>
    IReadOnlyList<string>? ChoicesFor(string variable, int row);

    /// <summary>True for a column whose cell is a dropdown.</summary>
    bool IsChoiceColumn(string variable);

    void SetCell(int row, string variable, string text);

    void CalculateAll();

    /// <summary>Applies the rows; returns how many were applied.</summary>
    int ApplyAll();

    /// <summary>The dialog model behind a row.</summary>
    FormulaDialogModel ModelFor(int row);

    /// <summary>The (ledger key, property id) pairs a row would write.</summary>
    IReadOnlyList<(string Key, string PropertyId)> OutputsOf(int row);

    /// <summary>Drops cached row views (the game state or rules changed). Typed values are kept.</summary>
    void Invalidate();

    /// <summary>True for an online guest's grid: the host calculates and applies.</summary>
    bool HostCalculates { get; }

    /// <summary>The rows still open (not applied, not skipped).</summary>
    IReadOnlyList<int> OpenRows();
}
