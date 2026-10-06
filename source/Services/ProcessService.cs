using System.Diagnostics;
using System.Text;

namespace Archestro.MeetingVault.Services;

public sealed record ProcessRunResult(
    int ExitCode,
    DateTimeOffset Started,
    DateTimeOffset Ended,
    string StandardOutput,
    string StandardError,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory)
{
    public TimeSpan Duration => Ended - Started;
}

public static class ProcessService
{
    public static async Task<int> RunAsync(
        string exe,
        IEnumerable<string> args,
        string? logPath,
        CancellationToken ct)
    {
        var result = await RunDetailedAsync(
            exe,
            args,
            logPath,
            workingDirectory: null,
            ct);

        return result.ExitCode;
    }

    public static async Task<ProcessRunResult> RunDetailedAsync(
        string exe,
        IEnumerable<string> args,
        string? logPath,
        string? workingDirectory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            throw new FileNotFoundException("Required executable was not found.", exe);

        var argumentList = args.Select(a => a ?? "").ToList();

        var resolvedWorkingDirectory =
            !string.IsNullOrWhiteSpace(workingDirectory) &&
            Directory.Exists(workingDirectory)
                ? workingDirectory
                : Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory;

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = resolvedWorkingDirectory
        };

        foreach (var arg in argumentList)
            psi.ArgumentList.Add(arg);

        var isBuzz =
            Path.GetFileName(exe).Equals(
                "Buzz.exe",
                StringComparison.OrdinalIgnoreCase);

        var isDirectWhisper =
            Path.GetFileName(exe).Equals(
                "python.exe",
                StringComparison.OrdinalIgnoreCase) &&
            argumentList.Any(a =>
                a.EndsWith(
                    "direct_whisper_worker.py",
                    StringComparison.OrdinalIgnoreCase));

        var isFasterWhisper =
            Path.GetFileName(exe).Equals(
                "python.exe",
                StringComparison.OrdinalIgnoreCase) &&
            argumentList.Any(a =>
                a.EndsWith(
                    "faster_whisper_worker.py",
                    StringComparison.OrdinalIgnoreCase));

        var isTranscriptionEngine = isBuzz || isDirectWhisper || isFasterWhisper;

        if (isTranscriptionEngine)
        {
            var threads = BackgroundTranscriptionRuntime.MaxCpuThreads.ToString();

            // Quality is unchanged. These variables only constrain CPU worker fan-out.
            psi.Environment["OMP_NUM_THREADS"] = threads;
            psi.Environment["MKL_NUM_THREADS"] = threads;
            psi.Environment["OPENBLAS_NUM_THREADS"] = threads;
            psi.Environment["NUMEXPR_NUM_THREADS"] = threads;
        }

        var started = DateTimeOffset.Now;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var p = new Process { StartInfo = psi };
        WindowsJobObjectThrottle? backgroundThrottle = null;
        CancellationTokenSource? governorCts = null;
        Task? governorTask = null;

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stdout.AppendLine(e.Data);
        };

        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(e.Data);
        };

        try
        {
            if (!p.Start())
                throw new InvalidOperationException($"Could not start process: {exe}");

            // HIGH-QUALITY QUIET MODE:
            // keep large-v3 exactly as configured, but make foreground Windows work win.
            if (isTranscriptionEngine)
            {
                try
                {
                    p.PriorityClass = ProcessPriorityClass.Idle;
                    p.PriorityBoostEnabled = false;
                }
                catch { }

                // Apply a hard CPU cap to Buzz and, where Windows allows it,
                // the worker processes that it creates in the same job object.
                backgroundThrottle = WindowsJobObjectThrottle.TryAttach(
                    p,
                    BackgroundTranscriptionRuntime.ActiveUserCpuCapPercent);

                if (backgroundThrottle is not null)
                {
                    governorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    governorTask = RunAdaptiveBuzzGovernorAsync(
                        p,
                        backgroundThrottle,
                        governorCts.Token);
                }
            }

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            await p.WaitForExitAsync(ct);

            // Ensure async output events are completely drained after process exit.
            try { p.WaitForExit(); } catch { }

            var ended = DateTimeOffset.Now;

            var result = new ProcessRunResult(
                p.ExitCode,
                started,
                ended,
                stdout.ToString(),
                stderr.ToString(),
                exe,
                argumentList,
                resolvedWorkingDirectory);

            if (!string.IsNullOrWhiteSpace(logPath))
                await WriteDetailedLogAsync(logPath, result, ct);

            if (governorCts is not null)
            {
                try { governorCts.Cancel(); } catch { }
            }

            if (governorTask is not null)
            {
                try { await governorTask; } catch { }
            }

            governorCts?.Dispose();
            governorCts = null;

            backgroundThrottle?.Dispose();
            backgroundThrottle = null;
            return result;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch { }

            if (governorCts is not null)
            {
                try { governorCts.Cancel(); } catch { }
            }

            if (governorTask is not null)
            {
                try { await governorTask; } catch { }
            }

            governorCts?.Dispose();
            governorCts = null;

            backgroundThrottle?.Dispose();
            backgroundThrottle = null;
            throw;
        }
        catch (Exception ex)
        {
            var ended = DateTimeOffset.Now;

            if (!string.IsNullOrWhiteSpace(logPath))
            {
                var failure = new StringBuilder();
                failure.AppendLine("PROCESS EXECUTION FAILED");
                failure.AppendLine($"Started: {started:o}");
                failure.AppendLine($"Ended: {ended:o}");
                failure.AppendLine($"Executable: {exe}");
                failure.AppendLine($"WorkingDirectory: {resolvedWorkingDirectory}");
                failure.AppendLine($"Arguments: {FormatArguments(argumentList)}");
                failure.AppendLine();
                failure.AppendLine(ex.ToString());

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                    await File.WriteAllTextAsync(logPath, failure.ToString(), ct);
                }
                catch { }
            }

            if (governorCts is not null)
            {
                try { governorCts.Cancel(); } catch { }
            }

            if (governorTask is not null)
            {
                try { await governorTask; } catch { }
            }

            governorCts?.Dispose();
            governorCts = null;

            backgroundThrottle?.Dispose();
            backgroundThrottle = null;
            throw;
        }
    }

    private static async Task RunAdaptiveBuzzGovernorAsync(
        Process process,
        WindowsJobObjectThrottle throttle,
        CancellationToken ct)
    {
        var lastCap = -1;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (process.HasExited)
                    return;

                var idle = SystemActivityService.GetUserIdleTime();

                var desiredCap =
                    BackgroundTranscriptionScheduler.IsRecordingActive
                        ? 1
                        : idle >= TimeSpan.FromSeconds(
                            BackgroundTranscriptionRuntime.AccelerateAfterNoInputSeconds)
                            ? BackgroundTranscriptionRuntime.IdleCpuCapPercent
                            : BackgroundTranscriptionRuntime.ActiveUserCpuCapPercent;

                if (desiredCap != lastCap)
                {
                    throttle.UpdateCpuCap(desiredCap);
                    lastCap = desiredCap;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    private static async Task WriteDetailedLogAsync(
        string logPath,
        ProcessRunResult result,
        CancellationToken ct)
    {
        var sb = new StringBuilder();

        sb.AppendLine("PROCESS EXECUTION RECEIPT");
        sb.AppendLine($"Started: {result.Started:o}");
        sb.AppendLine($"Ended: {result.Ended:o}");
        sb.AppendLine($"DurationSeconds: {result.Duration.TotalSeconds:F3}");
        sb.AppendLine($"ExitCode: {result.ExitCode}");
        sb.AppendLine($"Executable: {result.Executable}");
        sb.AppendLine($"WorkingDirectory: {result.WorkingDirectory}");
        sb.AppendLine($"Arguments: {FormatArguments(result.Arguments)}");

        var loggedArgs = result.Arguments ?? Array.Empty<string>();
        var isLoggedTranscriber =
            Path.GetFileName(result.Executable).Equals(
                "Buzz.exe",
                StringComparison.OrdinalIgnoreCase) ||
            (Path.GetFileName(result.Executable).Equals(
                "python.exe",
                StringComparison.OrdinalIgnoreCase) &&
             loggedArgs.Any(a =>
                a.EndsWith(
                    "direct_whisper_worker.py",
                    StringComparison.OrdinalIgnoreCase) ||
                a.EndsWith(
                    "faster_whisper_worker.py",
                    StringComparison.OrdinalIgnoreCase)));

        if (isLoggedTranscriber)
        {
            sb.AppendLine(
                $"BackgroundPolicy: Idle priority; adaptive CPU cap " +
                $"{BackgroundTranscriptionRuntime.ActiveUserCpuCapPercent}% active / " +
                $"{BackgroundTranscriptionRuntime.IdleCpuCapPercent}% idle; " +
                $"accelerate after {BackgroundTranscriptionRuntime.AccelerateAfterNoInputSeconds}s no input; " +
                $"max worker threads {BackgroundTranscriptionRuntime.MaxCpuThreads}");
        }

        sb.AppendLine();
        sb.AppendLine("----- STDOUT -----");
        sb.AppendLine(string.IsNullOrWhiteSpace(result.StandardOutput)
            ? "<empty>"
            : result.StandardOutput.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("----- STDERR -----");
        sb.AppendLine(string.IsNullOrWhiteSpace(result.StandardError)
            ? "<empty>"
            : result.StandardError.TrimEnd());
        sb.AppendLine();

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, sb.ToString(), ct);
    }

    public static string FormatArguments(IEnumerable<string> args) =>
        string.Join(
            " ",
            args.Select(a =>
                string.IsNullOrEmpty(a)
                    ? "\"\""
                    : a.Any(char.IsWhiteSpace) || a.Contains('"')
                        ? "\"" + a.Replace("\"", "\\\"") + "\""
                        : a));

    public static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            (!File.Exists(path) && !Directory.Exists(path)))
            throw new FileNotFoundException(
                "The requested file or folder does not exist.",
                path);

        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true
        });
    }
}
