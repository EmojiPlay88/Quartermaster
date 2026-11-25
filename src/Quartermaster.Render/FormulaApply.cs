using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>
/// Executes a formula run's commands (<see cref="FormulaRunner.Commands"/>) in order, seeding a placed piece right after its
/// <see cref="AddPiece"/> (<see cref="ModuleSession.SeedAfterAdd"/>) so the run's Placed writes override its sheet and starting values. The
/// caller opens the one action scope the whole run undoes in (spec B3: one run, one undo step).
/// </summary>
public static class FormulaApply
{
    /// <param name="exec">Executes one command (stamped and prepared as the caller does).</param>
    /// <returns>The commands executed, in order (for <see cref="FormulaRunner.AfterApply(IReadOnlyList{GameCommand})"/>).</returns>
    public static IReadOnlyList<GameCommand> Execute(ModuleSession s, IEnumerable<GameCommand> commands, Action<GameCommand> exec)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(exec);
        var executed = new List<GameCommand>();
        foreach (var c in commands ?? Array.Empty<GameCommand>())
        {
            if (c == null) continue;
            exec(c);
            executed.Add(c);
            if (c is AddPiece add) s.SeedAfterAdd(add);
        }
        return executed;
    }
}
