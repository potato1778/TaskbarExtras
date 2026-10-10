using System.Windows.Automation;

namespace TaskbarExtras.Shell;

/// <summary>
/// Where the taskbar's buttons actually are, according to UI Automation.
///
/// <para>
/// <b>Why this exists.</b> The obvious way to tell "right-clicked a task button" from
/// "right-clicked empty taskbar" is to look at which child window owns the point — that is what
/// <see cref="TaskbarInfo.IsOverEmptyTaskbarArea(int,int,out string?)"/> did, and it worked on
/// the machine it was written on. It is wrong in general, and the failure is silent.
/// </para>
/// <para>
/// Windows 11 draws the taskbar with XAML. The real buttons are not HWNDs, so their positions
/// appear nowhere in the window tree. What is left is <c>MSTaskSwWClass</c>, a container whose
/// rectangle stopped being updated — measured on this machine: it ends at x=875, while the last
/// task button actually ends at x=1139. Every button in between looked like empty background,
/// so right-clicking VMware, eduVPN, WeChat or Typora opened the replacement menu instead of
/// their jump lists. Reported by a user, reproduced, and the numbers above are from the
/// reproduction.
/// </para>
/// <para>
/// UI Automation sees through the XAML, because the buttons publish themselves as Button
/// elements with real bounding rectangles. That is the only reliable source, and it is cheap
/// enough to poll: a full enumeration with a <c>CacheRequest</c> costs about 17 ms.
/// </para>
/// </summary>
public static class TaskbarButtonMap
{
    /// <summary>
    /// How often the snapshot is rebuilt. The buttons only move when a window opens, closes or
    /// gets pinned, and a snapshot that is under a second old is never wrong for long enough to
    /// matter — whereas querying UI Automation on every right-click would put a cross-process
    /// call on the critical path of a low-level mouse hook, where the budget is 300 ms and
    /// blowing it gets the hook uninstalled by Windows.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Buttons wider than this multiple of the taskbar height are not buttons. UI Automation
    /// reports some containers as Button: measured here, a 668 px wide "Steam" element covering
    /// half the tray. Believing it would swallow most of the taskbar as "not empty".
    /// </summary>
    private const double MaxWidthToHeightRatio = 3.0;

    private static volatile ScreenRect[] _buttons = Array.Empty<ScreenRect>();
    private static volatile bool _running;
    private static Thread? _worker;

    /// <summary>Number of buttons in the current snapshot. Zero means UIA has produced nothing.</summary>
    public static int Count => _buttons.Length;

    /// <summary>
    /// Starts the background snapshot thread. Safe to call once; repeated calls do nothing.
    /// </summary>
    public static void Start()
    {
        if (_running) return;
        _running = true;

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "TaskbarButtonMap"
        };
        // UIA is happiest on an STA thread, and a dedicated one keeps the cross-process calls
        // off the UI thread entirely.
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public static void Stop() => _running = false;

    /// <summary>
    /// True when the point is inside one of the taskbar's buttons. Reads the last snapshot and
    /// never blocks, so it is safe to call from the mouse hook.
    /// </summary>
    public static bool IsOverAnyButton(int x, int y)
    {
        foreach (var rect in _buttons)
        {
            if (rect.Contains(x, y)) return true;
        }
        return false;
    }

    private static void WorkerLoop()
    {
        while (_running)
        {
            try
            {
                var snapshot = ReadButtons();
                // Never publish an empty snapshot: UIA can legitimately return nothing while the
                // taskbar is rebuilding, and a zero-length map would silently disable the check.
                if (snapshot.Length > 0) _buttons = snapshot;
            }
            catch
            {
                // Keep the previous snapshot. A stale map is far better than none.
            }

            Thread.Sleep(RefreshInterval);
        }
    }

    private static ScreenRect[] ReadButtons()
    {
        var taskbar = TaskbarInfo.Handle;
        if (taskbar == IntPtr.Zero) return Array.Empty<ScreenRect>();
        if (!WindowEnumerator.TryGetRect(taskbar, out var taskbarRect)) return Array.Empty<ScreenRect>();

        var element = AutomationElement.FromHandle(taskbar);
        if (element is null) return Array.Empty<ScreenRect>();

        var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
        var maxWidth = taskbarRect.Height * MaxWidthToHeightRatio;

        // One cross-process call instead of one per property per element.
        // Measured: 35 ms without, 17 ms with.
        var cache = new CacheRequest();
        cache.Add(AutomationElement.BoundingRectangleProperty);

        var list = new List<ScreenRect>();
        using (cache.Activate())
        {
            var found = element.FindAll(TreeScope.Descendants, condition);
            foreach (AutomationElement button in found)
            {
                var rect = button.Cached.BoundingRectangle;
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                if (rect.Width > maxWidth) continue;
                list.Add(new ScreenRect((int)rect.Left, (int)rect.Top, (int)rect.Right, (int)rect.Bottom));
            }
        }

        return list.ToArray();
    }
}
