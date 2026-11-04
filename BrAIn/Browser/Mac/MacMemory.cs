using System.Runtime.InteropServices;
using System.Text;

namespace BrAIn.Browser.Mac;

/// <summary>
/// How much memory BrAIn is using right now, the way Activity Monitor counts it: the "physical footprint" of the app
/// <i>plus</i> its WebKit helper processes (web content, networking, GPU). On macOS those helpers are not children of the app —
/// WebKit starts them through launchd, so their parent is pid 1 — which is why <c>ps</c>-style tree walks miss them; what ties
/// them to us is the "responsible process" macOS records for each (the same value Activity Monitor groups by). For a Finder /
/// Dock launch that is the app itself; when started from a terminal it is the terminal, which then also owns our helpers, so a
/// helper only counts when it is a <c>com.apple.WebKit.*</c> process orphaned to launchd (or a real child of ours).
/// One full scan of the process table takes about 2 ms.
/// </summary>
internal static unsafe partial class MacMemory
{
    private const string Lib = "/usr/lib/libSystem.B.dylib";

    [LibraryImport(Lib)] private static partial int proc_listallpids(int* buffer, int bufferSize);
    [LibraryImport(Lib)] private static partial int proc_pid_rusage(int pid, int flavor, byte* buffer);
    [LibraryImport(Lib)] private static partial int proc_name(int pid, byte* buffer, uint size);
    [LibraryImport(Lib)] private static partial int proc_pidinfo(int pid, int flavor, ulong arg, byte* buffer, int size);
    [LibraryImport(Lib)] private static partial int responsibility_get_pid_responsible_for_pid(int pid);

    private const int PROC_PIDTBSDINFO = 3, BsdInfoSize = 136;
    private const int PhysFootprintOffset = 72; // rusage_info_v0: uuid[16] + 7 uint64 fields, then ri_phys_footprint
    private static bool _noResponsible;          // the private call is missing on this macOS: count the app and its real children only

    private static int Responsible(int pid)
    {
        if (_noResponsible) return -1;
        try { return responsibility_get_pid_responsible_for_pid(pid); }
        catch (EntryPointNotFoundException) { _noResponsible = true; return -1; }
    }

    private static int ParentOf(int pid)
    {
        var b = stackalloc byte[256];
        return proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, b, BsdInfoSize) == BsdInfoSize ? (int)*(uint*)(b + 16) : -1;
    }

    private static string NameOf(int pid)
    {
        var b = stackalloc byte[128];
        var n = proc_name(pid, b, 128);
        return n > 0 ? Encoding.UTF8.GetString(b, n) : "";
    }

    private static ulong Footprint(int pid)
    {
        var b = stackalloc byte[256];
        // flavor 0 (RUSAGE_INFO_V0) is enough, and the same field sits at the same offset in every newer flavor
        return proc_pid_rusage(pid, 0, b) == 0 ? *(ulong*)(b + PhysFootprintOffset) : 0;
    }

    /// <summary>The summed footprint, in MB, of BrAIn and its WebKit helper processes; null when it cannot be read.</summary>
    internal static double? TreeFootprintMb()
    {
        try
        {
            var me = Environment.ProcessId;
            var myResp = Responsible(me);
            var pids = new int[4096];
            int n;
            fixed (int* p = pids) n = proc_listallpids(p, pids.Length * sizeof(int));
            if (n <= 0) return null;
            ulong total = 0;
            for (var i = 0; i < n && i < pids.Length; i++)
            {
                var pid = pids[i];
                if (pid <= 0) continue;
                if (pid != me)
                {
                    if (myResp > 0 && Responsible(pid) != myResp) continue;           // someone else's process
                    var parent = ParentOf(pid);
                    var ours = parent == me                                            // a real child of the app
                        || (myResp > 0 && parent == 1 && NameOf(pid).StartsWith("com.apple.WebKit.", StringComparison.Ordinal));
                    if (!ours) continue;
                }
                total += Footprint(pid);
            }
            return total / 1048576.0;
        }
        catch { return null; }
    }
}
