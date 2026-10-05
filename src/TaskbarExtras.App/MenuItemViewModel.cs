using TaskbarExtras.Actions;

namespace TaskbarExtras.App;

/// <summary>
/// One row in the menu. Either wraps an <see cref="IAction"/>, is a separator, or is an
/// app-level command supplied by the host (see <see cref="Command"/>).
/// </summary>
public sealed class MenuItemViewModel
{
    public IAction? Action { get; private init; }
    public bool IsSeparator { get; private init; }

    /// <summary>Set only for host-supplied rows, which have no <see cref="IAction"/>.</summary>
    public string? Label { get; private init; }
    public Action? Invoke { get; private init; }

    public string DisplayName => Label ?? Action?.DisplayName ?? string.Empty;

    public bool IsEnabled => Invoke is not null || (Action?.CanExecute() ?? false);

    public static MenuItemViewModel From(IAction action) => new() { Action = action };

    public static MenuItemViewModel Separator() => new() { IsSeparator = true };

    /// <summary>
    /// A row that is not a shell action. Used for "Exit TaskbarExtras": quitting the app is not
    /// something the Windows 10 taskbar menu could do, so it does not belong in the action
    /// registry — but this app has no main window, so the user still needs a way out.
    /// </summary>
    public static MenuItemViewModel Command(string label, Action invoke) =>
        new() { Label = label, Invoke = invoke };
}
