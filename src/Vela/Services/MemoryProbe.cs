using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Vela.Services;

public readonly record struct MemorySnapshot(long WorkingSetBytes, int ProcessCount)
{
    public double Megabytes => WorkingSetBytes / 1024d / 1024d;
}

/// <summary>
/// Sums the working set of this process plus every child process (WebView2 browser,
/// GPU and renderer processes), which is what the user actually pays for.
/// </summary>
public static class MemoryProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        ref ProcessBasicInformation processInformation, int processInformationLength,
        out int returnLength);

    public static MemorySnapshot Measure(int rootPid)
    {
        var parentOf = new Dictionary<int, int>(512);
        var workingSet = new Dictionary<int, long>(512);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var pid = process.Id;
                parentOf[pid] = GetParentPid(process);
                workingSet[pid] = process.WorkingSet64;
            }
            catch
            {
                // Access denied / process exited between enumeration and query.
            }
            finally
            {
                process.Dispose();
            }
        }

        long total = 0;
        var count = 0;

        foreach (var (pid, _) in workingSet)
        {
            if (!HasAncestor(pid, rootPid, parentOf)) continue;
            total += workingSet[pid];
            count++;
        }

        return new MemorySnapshot(total, count);
    }

    private static bool HasAncestor(int pid, int rootPid, Dictionary<int, int> parentOf)
    {
        var current = pid;
        for (var guard = 0; guard < 64; guard++)
        {
            if (current == rootPid) return true;
            if (!parentOf.TryGetValue(current, out var parent) || parent <= 0) return false;
            current = parent;
        }

        return false;
    }

    private static int GetParentPid(Process process)
    {
        var info = new ProcessBasicInformation();
        var status = NtQueryInformationProcess(
            process.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _);

        return status == 0 ? (int)info.InheritedFromUniqueProcessId.ToInt64() : 0;
    }
}
