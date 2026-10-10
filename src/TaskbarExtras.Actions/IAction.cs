namespace TaskbarExtras.Actions;

/// <summary>
/// One thing the user can pick from a menu. Adding a new menu entry means adding one
/// implementation and registering it — the menu UI never changes.
/// </summary>
public interface IAction
{
    /// <summary>Stable id used by configuration files. Never localise this.</summary>
    string Id { get; }

    /// <summary>Localised label shown in the menu.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Icon key. The skin decides what glyph or bitmap that maps to, so skins can restyle
    /// the menu without touching action code. Empty means "no icon".
    /// </summary>
    string IconKey { get; }

    /// <summary>
    /// Keyboard mnemonic, shown as the bracketed letter in <c>Show desktop (D)</c> and bound to
    /// that key while the menu is open. Windows has used these on its taskbar menus since
    /// Windows 95, and the letters come from the <i>English</i> wording even in localised
    /// builds — Windows 10's Chinese menu says <c>工具栏(T)</c>, not a Chinese initial. So this
    /// is deliberately not localised either.
    ///
    /// <para>Use <c>'\0'</c> for "no mnemonic".</para>
    /// </summary>
    char Mnemonic { get; }

    bool CanExecute();

    /// <summary>Returns true when the action actually did something.</summary>
    bool Execute();
}

/// <summary>Lookup of every known action, by id.</summary>
public sealed class ActionRegistry
{
    private readonly Dictionary<string, IAction> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IAction> _ordered = new();

    public IReadOnlyList<IAction> All => _ordered;

    public void Register(IAction action)
    {
        if (!_byId.TryAdd(action.Id, action)) return;
        _ordered.Add(action);
    }

    public IAction? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The default menu, in the same order the Windows 10 taskbar used.</summary>
    public static ActionRegistry CreateDefault()
    {
        var registry = new ActionRegistry();
        registry.Register(new ShowDesktopAction());
        registry.Register(new TaskManagerAction());
        registry.Register(new CascadeWindowsAction());
        registry.Register(new TileHorizontallyAction());
        registry.Register(new TileVerticallyAction());
        registry.Register(new TaskbarSettingsAction());
        return registry;
    }
}
