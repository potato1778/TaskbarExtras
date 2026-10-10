using TaskbarExtras.Actions;

namespace TaskbarExtras.App;

/// <summary>
/// One row in the menu. Either wraps an <see cref="IAction"/>, is a separator, or is an
/// app-level command supplied by the host (see <see cref="Toggle"/> / <see cref="Command"/>).
/// </summary>
public sealed class MenuItemViewModel
{
    public IAction? Action { get; private init; }
    public bool IsSeparator { get; private init; }

    /// <summary>
    /// The final text to render, mnemonic suffix already included when the skin wants one.
    /// Composed here rather than in the template so the skin switch lives in one place.
    /// </summary>
    public string DisplayName { get; private init; } = string.Empty;

    /// <summary>
    /// Key that activates this row while the menu is open, or <c>'\0'</c> for none. The suffix is
    /// only <i>shown</i> when the skin asks for it — Windows 11 menus have no mnemonics, Windows
    /// 10 ones do — but the key stays bound either way.
    /// </summary>
    public char Mnemonic { get; private init; }

    /// <summary>Set only for host-supplied rows, which have no <see cref="IAction"/>.</summary>
    public Action? Invoke { get; private init; }

    /// <summary>
    /// True for on/off rows. Kept separate from <see cref="IsChecked"/> so that "not a toggle"
    /// and "a toggle that is off" are distinguishable.
    /// </summary>
    public bool IsToggle { get; private init; }

    public bool IsChecked { get; private init; }

    /// <summary>
    /// True for a non-interactive group label. Rendered dimmed and never clickable, so it is
    /// visually obvious that the rows underneath belong together.
    /// </summary>
    public bool IsHeading { get; private init; }

    /// <summary>Tick glyph, or empty when this row is not a checked toggle.</summary>
    public string CheckGlyph => IsToggle && IsChecked ? "\u2713" : string.Empty;

    public bool IsEnabled => !IsHeading && (Invoke is not null || (Action?.CanExecute() ?? false));

    public static MenuItemViewModel From(IAction action, bool withMnemonic) => new()
    {
        Action = action,
        Mnemonic = action.Mnemonic,
        DisplayName = Compose(action.DisplayName, action.Mnemonic, withMnemonic)
    };

    public static MenuItemViewModel Separator() => new() { IsSeparator = true };

    /// <summary>
    /// A pick-one-of-N row: exactly one member of the group carries the tick.
    ///
    /// <para>
    /// Distinct from <see cref="Toggle"/> because the semantics differ — a toggle flips itself,
    /// while a radio row selects and implicitly deselects its siblings. The menu is a flat list of
    /// rows with a reserved tick column (see the item template), so a real submenu would mean
    /// adding popup and hover handling to the template for a setting users touch once. Radio rows
    /// reuse the column that is already there and keep the skin switch reachable by mnemonic.
    /// </para>
    /// </summary>
    public static MenuItemViewModel Radio(
        string label, char mnemonic, bool isSelected, bool withMnemonic, Action invoke) => new()
    {
        DisplayName = Compose(label, mnemonic, withMnemonic),
        Mnemonic = mnemonic,
        Invoke = invoke,
        IsToggle = true,
        IsChecked = isSelected
    };

    /// <summary>
    /// A heading row: no tick, no action, not selectable. Used to group the rows below it, which
    /// is how the appearance entries announce themselves without a submenu.
    /// </summary>
    public static MenuItemViewModel Heading(string label, bool withMnemonic) => new()
    {
        DisplayName = Compose(label, '\0', withMnemonic),
        IsHeading = true
    };

    /// <summary>
    /// A setting row: click toggles it, the tick shows the state.
    ///
    /// <para>
    /// The state is read when the row is built rather than stored, and rows are rebuilt on every
    /// menu open — so a change made from the command line, or from Task Manager, shows up the
    /// next time the menu is opened instead of being remembered wrongly.
    /// </para>
    /// </summary>
    public static MenuItemViewModel Toggle(
        string label, char mnemonic, bool isChecked, bool withMnemonic, Action invoke) => new()
    {
        DisplayName = Compose(label, mnemonic, withMnemonic),
        Mnemonic = mnemonic,
        Invoke = invoke,
        IsToggle = true,
        IsChecked = isChecked
    };

    /// <summary>
    /// A row that is not a shell action. Used for "Exit TaskbarExtras": quitting the app is not
    /// something the Windows 10 taskbar menu could do, so it does not belong in the action
    /// registry — but this app has no main window, so the user still needs a way out.
    /// </summary>
    public static MenuItemViewModel Command(string label, char mnemonic, bool withMnemonic, Action invoke) => new()
    {
        DisplayName = Compose(label, mnemonic, withMnemonic),
        Mnemonic = mnemonic,
        Invoke = invoke
    };

    /// <summary>
    /// "Show desktop" + 'D' becomes "Show desktop(D)". No space before the bracket — that is how
    /// Windows writes it.
    /// </summary>
    private static string Compose(string label, char mnemonic, bool withMnemonic) =>
        withMnemonic && mnemonic != '\0' ? $"{label}({mnemonic})" : label;
}
