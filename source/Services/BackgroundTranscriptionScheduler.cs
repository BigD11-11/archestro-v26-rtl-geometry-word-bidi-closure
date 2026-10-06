using System.Runtime.InteropServices;
using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public static class BackgroundTranscriptionScheduler
{
    private static int _recordingActive;

    public static bool IsRecordingActive =>
        Volatile.Read(ref _recordingActive) != 0;

    public static void SetRecordingActive(bool active) =>
        Volatile.Write(ref _recordingActive, active ? 1 : 0);

    public static async Task WaitForSmartWindowAsync(
        AppSettings settings,
        DateTimeOffset queuedAt,
        Func<bool> manualStartRequested,
        Action<string> setStatus,
        CancellationToken ct)
    {
        if (!settings.BackgroundTranscriptionQuietMode)
            return;

        var delaySeconds = settings.FirstTranscriptFastPathEnabled
            ? Math.Clamp(settings.FirstTranscriptFastStartDelaySeconds, 3, 12)
            : Math.Clamp(settings.BackgroundTranscriptionStartDelaySeconds, 10, 600);

        // V13.8.6: automatic and manual paths share one realistic memory gate.
        // The prior split (3+ GB auto vs 2 GB manual) made auto-start wait forever
        // while the exact same machine could transcribe immediately after a click.
        var configuredFloorMb = Math.Min(
            settings.BackgroundTranscriptionMinimumFreeMemoryMb,
            settings.ManualTranscriptionMinimumFreeMemoryMb);
        var unifiedMinimumMb = Math.Clamp(configuredFloorMb, 1536, 2304);
        var unifiedMinimumFreeBytes = (ulong)unifiedMinimumMb * 1024UL * 1024UL;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (IsRecordingActive)
            {
                setStatus("Queued • recording has priority");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            var manual = manualStartRequested();
            if (!manual)
            {
                var elapsed = DateTimeOffset.Now - queuedAt;
                var remaining = delaySeconds - (int)Math.Floor(elapsed.TotalSeconds);

                if (remaining > 0)
                {
                    setStatus($"Queued • auto-start in {remaining}s");
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }
            }

            setStatus(manual ? "Starting transcript now…" : "Starting automatic transcript…");

            var available = GetAvailablePhysicalMemory();
            if (available > 0 && available < unifiedMinimumFreeBytes)
            {
                var freeGb = available / 1024d / 1024d / 1024d;
                var neededGb = unifiedMinimumFreeBytes / 1024d / 1024d / 1024d;
                setStatus($"Waiting for memory • {freeGb:0.0} GB free / {neededGb:0.0} GB needed");
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }

            // The native process governor already constrains CPU priority/caps while the
            // user is active. Do not add a second pre-start CPU barrier on the fast path;
            // it caused countdown-complete jobs to appear frozen instead of starting.
            if (!settings.FirstTranscriptFastPathEnabled && !manual)
            {
                var maxCpu = Math.Clamp(settings.BackgroundTranscriptionMaxSystemCpuPercent, 20, 95);
                var cpu = await GetSystemCpuUsagePercentAsync(ct);
                if (cpu >= 0 && cpu > maxCpu)
                {
                    setStatus($"Queued • waiting for lighter system load ({cpu:0}% CPU)");
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    continue;
                }
            }

            setStatus(manual
                ? "Transcribing now • manual priority"
                : "Transcribing now • automatic fast start");
            return;
        }
    }

    private static async Task<double> GetSystemCpuUsagePercentAsync(
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return -1;

        if (!TryGetSystemTimes(out var idle1, out var kernel1, out var user1))
            return -1;

        await Task.Delay(650, ct);

        if (!TryGetSystemTimes(out var idle2, out var kernel2, out var user2))
            return -1;

        var idle = idle2 - idle1;
        var kernel = kernel2 - kernel1;
        var user = user2 - user1;
        var total = kernel + user;

        if (total <= 0)
            return -1;

        var busy = total - idle;
        return Math.Clamp(busy * 100.0 / total, 0, 100);
    }

    private static bool TryGetSystemTimes(
        out ulong idle,
        out ulong kernel,
        out ulong user)
    {
        idle = kernel = user = 0;

        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            return false;

        idle = ToUInt64(idleFt);
        kernel = ToUInt64(kernelFt);
        user = ToUInt64(userFt);
        return true;
    }

    private static ulong ToUInt64(FILETIME value) =>
        ((ulong)value.dwHighDateTime << 32) | value.dwLowDateTime;

    private static ulong GetAvailablePhysicalMemory()
    {
        if (!OperatingSystem.IsWindows())
            return ulong.MaxValue;

        var status = new MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
        };

        if (!GlobalMemoryStatusEx(ref status))
            return 0;

        return status.ullAvailPhys;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FILETIME lpIdleTime,
        out FILETIME lpKernelTime,
        out FILETIME lpUserTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
