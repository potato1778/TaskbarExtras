using System.IO;

namespace TaskbarExtras.Shell;

/// <summary>
/// Lets this WinExe talk to the terminal that launched it.
///
/// <para>
/// A WinExe has no console of its own, so <c>TaskbarExtras.exe --quit</c> would exit silently and
/// the user could not tell whether the running instance had actually been told to stop. Attaching
/// to the parent console turns a command-line switch from a guess into something you can see the
/// result of.
/// </para>
/// </summary>
public static class ParentConsole
{
    /// <summary>Returns false when there is no parent console (e.g. launched from Explorer).</summary>
    public static bool TryAttach()
    {
        if (!NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS)) return false;
        try
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
