using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Switchyard;

/// <summary>
/// On Windows every process the gateway starts goes into its own Job Object. A process tree there is
/// only found through parent pids, which break as soon as a middle process exits: `npm run dev` is
/// cmd → npm → cmd → node, and killing the tree left vite holding its port. Ending the job ends every
/// process in it. The job is also set to end with its last handle, so when the gateway itself dies,
/// Windows stops its children.
/// </summary>
static class WindowsJob
{
    static readonly ConcurrentDictionary<int, IntPtr> Jobs = new();

    /// <summary>Puts a just-started process (and so everything it starts from now on) into a new job.</summary>
    public static void Contain(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())
            || !AssignProcessToJobObject(job, process.Handle))
        {
            CloseHandle(job);
            return;
        }
        Jobs[process.Id] = job;
    }

    /// <summary>Ends every process in the job of <paramref name="pid"/>; false when it has none.</summary>
    public static bool Terminate(int pid)
    {
        if (!Jobs.TryRemove(pid, out var job))
            return false;
        TerminateJobObject(job, 1);
        CloseHandle(job);
        return true;
    }

    /// <summary>After the process exited: closing the job also ends anything it left running.</summary>
    public static void Release(int pid)
    {
        if (Jobs.TryRemove(pid, out var job))
            CloseHandle(job);
    }

    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
