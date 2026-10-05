using TaskbarExtras.Actions;

namespace TaskbarExtras.App;

/// <summary>One row in the menu. Either wraps an <see cref="IAction"/> or is a separator.</summary>
public sealed class MenuItemViewModel
{
    public IAction? Action { get; private init; }
    public bool IsSeparator { get; private init; }

    public string DisplayName => Action?.DisplayName ?? string.Empty;
    public bool IsEnabled => Action?.CanExecute() ?? false;

    public static MenuItemViewModel From(IAction action) => new() { Action = action };
    public static MenuItemViewModel Separator() => new() { IsSeparator = true };
}
