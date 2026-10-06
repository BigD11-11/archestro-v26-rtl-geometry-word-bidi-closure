using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Archestro.MeetingVault.Services;

internal sealed class WindowsJobObjectThrottle : IDisposable
{
    private IntPtr _job;
    private readonly object _gate = new();

    private WindowsJobObjectThrottle(IntPtr job)
    {
        _job = job;
    }

    public static WindowsJobObjectThrottle? TryAttach(
        Process process,
        int cpuCapPercent)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return null;

        try
        {
            var throttle = new WindowsJobObjectThrottle(job);

            if (!throttle.UpdateCpuCap(cpuCapPercent))
            {
                throttle.Dispose();
                return null;
            }

            if (!AssignProcessToJobObject(job, process.Handle))
            {
                throttle.Dispose();
                return null;
            }

            return throttle;
        }
        catch
        {
            if (job != IntPtr.Zero)
                CloseHandle(job);

            return null;
        }
    }

    public bool UpdateCpuCap(int cpuCapPercent)
    {
        lock (_gate)
        {
            if (_job == IntPtr.Zero)
                return false;

            var cap = Math.Clamp(cpuCapPercent, 5, 80);

            var cpuInfo = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags =
                    JOB_OBJECT_CPU_RATE_CONTROL_ENABLE |
                    JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                CpuRate = (uint)(cap * 100)
            };

            return SetInformationJobObject(
                _job,
                JobObjectCpuRateControlInformation,
                ref cpuInfo,
                (uint)Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            var job = _job;
            _job = IntPtr.Zero;

            if (job != IntPtr.Zero)
                CloseHandle(job);
        }
    }

    private const int JobObjectCpuRateControlInformation = 15;
    private const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
    private const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(
        IntPtr lpJobAttributes,
        string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int JobObjectInformationClass,
        ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        IntPtr hJob,
        IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
