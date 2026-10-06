using System.Runtime.InteropServices;

namespace Archestro.MeetingVault.Services;

public static class SystemActivityService
{
    public static TimeSpan GetUserIdleTime()
    {
        if (!OperatingSystem.IsWindows())
            return TimeSpan.MaxValue;

        var info = new LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
        };

        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;

        var now = unchecked((uint)Environment.TickCount);
        var elapsed = unchecked(now - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
