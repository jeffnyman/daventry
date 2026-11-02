namespace Daventry.Core.Hosting;

/// <summary>A saved game of the running game, as <see cref="Engine.ListSaves"/> lists them.</summary>
/// <param name="Slot">The slot, as <see cref="Engine.Save"/> and <see cref="Engine.RequestRestore"/> take it.</param>
/// <param name="Description">The name the player gave it.</param>
/// <param name="SavedAt">When it was saved, if the save says.</param>
/// <param name="FileName">Its name in the host's <c>ISaveStore</c>.</param>
public sealed record SaveSlot(int Slot, string Description, DateTime? SavedAt, string FileName);

/// <summary>The outcome of <see cref="Engine.Save"/>: whether the game was saved and, if not, why.</summary>
public sealed record SaveResult(int Slot, bool Succeeded, string? Message = null)
{
    /// <summary>A save that succeeded.</summary>
    public static SaveResult Saved(int slot) => new(slot, true);

    /// <summary>A save that did not, and why.</summary>
    public static SaveResult Failed(int slot, string message) => new(slot, false, message);
}

/// <summary>
/// The outcome of a restore asked for with <see cref="Engine.RequestRestore"/>. A restore that
/// failed leaves the game as it was before.
/// </summary>
public sealed record RestoreResult(int Slot, bool Succeeded, string? Message = null)
{
    /// <summary>A restore that succeeded.</summary>
    public static RestoreResult Restored(int slot) => new(slot, true);

    /// <summary>A restore that did not, and why.</summary>
    public static RestoreResult Failed(int slot, string message) => new(slot, false, message);
}
