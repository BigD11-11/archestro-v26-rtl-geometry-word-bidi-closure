using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public static class BackgroundTranscriptionRuntime
{
    private static int _activeUserCpuCapPercent = 20;
    private static int _idleCpuCapPercent = 70;
    private static int _accelerateAfterNoInputSeconds = 30;
    private static int _maxCpuThreads = 6;

    public static int ActiveUserCpuCapPercent =>
        Volatile.Read(ref _activeUserCpuCapPercent);

    public static int IdleCpuCapPercent =>
        Volatile.Read(ref _idleCpuCapPercent);

    public static int AccelerateAfterNoInputSeconds =>
        Volatile.Read(ref _accelerateAfterNoInputSeconds);

    public static int MaxCpuThreads =>
        Volatile.Read(ref _maxCpuThreads);

    public static void Configure(AppSettings settings)
    {
        Volatile.Write(
            ref _activeUserCpuCapPercent,
            Math.Clamp(
                settings.BackgroundTranscriptionActiveUserCpuCapPercent,
                5,
                40));

        Volatile.Write(
            ref _idleCpuCapPercent,
            Math.Clamp(
                settings.BackgroundTranscriptionIdleCpuCapPercent,
                10,
                70));

        Volatile.Write(
            ref _accelerateAfterNoInputSeconds,
            Math.Clamp(
                settings.BackgroundTranscriptionAccelerateAfterNoInputSeconds,
                10,
                600));

        Volatile.Write(
            ref _maxCpuThreads,
            Math.Clamp(
                settings.BackgroundTranscriptionMaxCpuThreads,
                1,
                8));
    }
}
