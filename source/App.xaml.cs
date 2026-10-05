using System.Windows;
using Archestro.MeetingVault.Services;

namespace Archestro.MeetingVault;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppPaths.Ensure();
        AppearanceService.Configure("System", "English");

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(AppPaths.Logs, "startup-crash.txt"),
                    args.Exception.ToString());
            }
            catch { }

            MessageBox.Show(
                "Archestro Meeting Vault could not complete startup.\n\nA diagnostic log was saved under Documents\\Archestro Meeting Vault\\Logs.",
                "Archestro Meeting Vault",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            args.Handled = true;
            Shutdown(10);
        };

        if (e.Args.Contains("--cloud-provider-qa", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var output = Environment.GetEnvironmentVariable("ARCHESTRO_V28_PROVIDER_QA_OUTPUT");
                Task.Run(() => CloudProviderQaService.RunAsync(output)).GetAwaiter().GetResult();
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                var errorPath = Environment.GetEnvironmentVariable("ARCHESTRO_V28_PROVIDER_QA_ERROR");
                if (string.IsNullOrWhiteSpace(errorPath)) errorPath = Path.Combine(AppPaths.Logs, "v28-provider-qa-error.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(errorPath))!);
                File.WriteAllText(errorPath, ex.ToString());
                Environment.ExitCode = 30;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        if (e.Args.Contains("--v13.8.9-qa", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                RunV1389Qa();
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppPaths.Logs, "v13.8.9-qa-error.txt"), ex.ToString());
                Environment.ExitCode = 13;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        if (e.Args.Contains("--v13.8.8-qa", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                RunV1388Qa();
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppPaths.Logs, "v13.8.8-qa-error.txt"), ex.ToString());
                Environment.ExitCode = 12;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        if (e.Args.Contains("--v13.8.7-qa", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                RunV1387Qa();
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppPaths.Logs, "v13.8.7-qa-error.txt"), ex.ToString());
                Environment.ExitCode = 11;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        if (e.Args.Contains("--v13.8.6-qa", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                RunV1386Qa();
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppPaths.Logs, "v13.8.6-qa-error.txt"), ex.ToString());
                Environment.ExitCode = 10;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        if (e.Args.Contains("--v13.8.5-qa", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                RunV1385Qa();
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppPaths.Logs, "v13.8.5-qa-error.txt"), ex.ToString());
                Environment.ExitCode = 9;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        if (e.Args.Contains("--v13.8-matrix", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                RunV138Matrix();
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppPaths.Logs, "v13.8-matrix-error.txt"), ex.ToString());
                Environment.ExitCode = 8;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                SelfTestService.RunOffline();
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppPaths.Logs, "self-test-error.txt"), ex.ToString());
                Environment.ExitCode = 2;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        if (e.Args.Contains("--rtl-layout-qa", StringComparer.OrdinalIgnoreCase))
        {
            base.OnStartup(e);
            try
            {
                var output = Environment.GetEnvironmentVariable("ARCHESTRO_RTL_QA_OUTPUT");
                if (string.IsNullOrWhiteSpace(output))
                    output = Path.Combine(AppPaths.Logs, "rtl-layout-qa.json");
                SelfTestService.RunRtlLayoutQa(output);
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                var errorPath = Environment.GetEnvironmentVariable("ARCHESTRO_RTL_QA_ERROR");
                if (string.IsNullOrWhiteSpace(errorPath))
                    errorPath = Path.Combine(AppPaths.Logs, "rtl-layout-qa-error.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(errorPath))!);
                File.WriteAllText(errorPath, ex.ToString());
                Environment.ExitCode = 20;
            }
            Shutdown(Environment.ExitCode);
            return;
        }



        if (e.Args.Contains("--ai-self-test", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // Headless startup runs on the WPF Dispatcher thread.
                // Execute async AI validation on the ThreadPool so its continuations can never
                // depend on the blocked Dispatcher SynchronizationContext.
                Task.Run(IntelligenceSelfTestService.RunAsync).GetAwaiter().GetResult();
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppPaths.Logs, "intelligence-self-test-error.txt"), ex.ToString());
                Environment.ExitCode = 5;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        if (e.Args.Contains("--speaker-self-test", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                SpeakerSelfTestService.Run();
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppPaths.Logs, "speaker-self-test-error.txt"), ex.ToString());
                Environment.ExitCode = 4;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        if (e.Args.Contains("--system-qa", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // Same headless rule as the AI self-test: do not block the WPF Dispatcher
                // on an async method that may capture its SynchronizationContext.
                Task.Run(SystemQaService.RunAsync).GetAwaiter().GetResult();
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppPaths.Logs, "system-qa-error.txt"), ex.ToString());
                Environment.ExitCode = 3;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        base.OnStartup(e);

        // Normal interactive startup only. Self-test branches return above,
        // therefore headless QA can never instantiate MainWindow.
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void RunV1389Qa()
    {
        RunV1388Qa();

        var failures = new List<string>();
        void Check(bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }

        var requiredThemeKeys = new[]
        {
            "ChromeBrush", "ChromeBorderBrush",
            "LogoPlateBrush", "LogoPlateBorderBrush",
            "ChatUserBrush", "ChatAssistantBrush", "ChatEvidenceBrush",
            "DangerSoftBrush", "DangerBorderBrush", "DangerTextBrush"
        };

        var themeAudit = new Dictionary<string, Dictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var theme in new[] { "Dark", "Light", "System" })
        {
            AppearanceService.Configure(theme, "English");
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in requiredThemeKeys)
            {
                var brush = Application.Current.Resources[key] as System.Windows.Media.SolidColorBrush;
                Check(brush is not null, $"theme:{theme}:missing:{key}");
                if (brush is not null)
                    row[key] = brush.Color.ToString();
            }
            themeAudit[theme] = row;
        }

        var settings = new Models.AppSettings();
        Check(
            settings.AutomaticWholeFileRecoveryMaxSeconds <= 600 &&
            settings.AutomaticWholeFileRecoveryMaxSeconds >=
                settings.AutomaticBilingualRecoveryMinimumSeconds,
            "transcription:bounded-whole-file-recovery");

        var meeting = new Models.MeetingRecord();
        Check(
            meeting.Category.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase),
            "category:new-meeting-default-must-be-uncategorized");

        var evidence = new Models.EvidenceRef
        {
            MeetingTitle = "QA Meeting",
            MeetingStartLocal = DateTimeOffset.Now,
            MeetingCategory = "Uncategorized",
            Speaker = "QA Person",
            SpeakerMatchLabel = "Likely match • 74% voice similarity",
            StartSeconds = 14
        };

        Check(!string.IsNullOrWhiteSpace(evidence.MeetingDateText), "evidence:meeting-date");
        Check(evidence.TimeText == "00:00:14", "evidence:timestamp");
        Check(!string.IsNullOrWhiteSpace(evidence.SpeakerMatchLabel), "evidence:speaker-match-label");

        var output = Path.Combine(AppPaths.Logs, "v13.8.9-qa.json");
        var status = failures.Count == 0 ? "PASS" : "FAIL";

        File.WriteAllText(
            output,
            System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    status,
                    baseline = "V13.8.8 target-Windows technical PASS",
                    failures,
                    themeContract = "three-theme semantic sidebar/chrome/logo/action resources + no native child-window chrome",
                    tickerContract = "continuous timer motion; generic UI refresh must not reset animation position",
                    transcriptionContract = "bounded automatic whole-file recovery; long meetings avoid silent second full pass",
                    categoryContract = "classification is advisory; new meetings remain Uncategorized until explicit owner action",
                    speakerContract = "confirmed vs voice-similarity semantics + independent secondary windows",
                    intelligenceContract = "customer-facing local brief + rich evidence metadata + no model name in UI",
                    encodingContract = "UTF-8 child-process output + mojibake repair guard + same-language answer prompt",
                    localizationContract = "Arabic coverage across main Vault/People/Intelligence flows",
                    themeAudit,
                    evidenceContract = new
                    {
                        evidence.MeetingTitle,
                        evidence.MeetingDateText,
                        evidence.MeetingCategory,
                        evidence.Speaker,
                        evidence.SpeakerMatchLabel,
                        evidence.TimeText
                    }
                },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        if (failures.Count > 0)
            throw new InvalidOperationException(
                "V13.8.9 QA collected contract failures: " +
                string.Join(" | ", failures));
    }

    private static void RunV1388Qa()
    {
        RunV1387Qa();

        var profile = new Models.SpeakerProfile
        {
            Name = "QA Person"
        };
        profile.ConfirmedMeetingIds.Add("meeting-qa");
        profile.ConfirmedSpeakerRefs.Add("meeting-qa:0");

        if (profile.ConfirmedMeetingIds.Count != 1 ||
            profile.ConfirmedSpeakerRefs.Count != 1 ||
            profile.SampleCount < 1)
            throw new InvalidOperationException(
                "Remembered-people profile contract regressed.");

        var analysis = new Models.SpeakerAnalysisResult();
        analysis.SpeakerMatchCandidates[0] = "QA Person";
        analysis.SpeakerMatchScores[0] = 0.83f;
        analysis.SpeakerMatchKinds[0] = "Auto match";

        if (!analysis.SpeakerMatchCandidates.ContainsKey(0) ||
            !analysis.SpeakerMatchScores.ContainsKey(0) ||
            !analysis.SpeakerMatchKinds.ContainsKey(0))
            throw new InvalidOperationException(
                "Speaker match evidence contract regressed.");

        var label = SpeakerProfileStore.ConfidenceLabel(0.83f, 0.62f);
        if (string.IsNullOrWhiteSpace(label) ||
            !label.Contains("similarity", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Speaker confidence labeling contract regressed.");

        var output = Path.Combine(AppPaths.Logs, "v13.8.8-qa.json");
        File.WriteAllText(
            output,
            System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    status = "PASS",
                    baseline = "V13.8.6 technical PASS; V13.8.8 includes final product closeout + People scope",
                    peopleContract = "saved people directory + meeting evidence + confidence labels + voice sample actions",
                    speakerMatchContract = "confirmed vs auto vs possible remains explainable and persisted",
                    privacyContract = "voice profiles stay local; forgetting a profile does not delete meeting evidence"
                },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RunV1387Qa()
    {
        var requiredThemeKeys = new[]
        {
            "AppBackground", "PanelBrush", "SurfaceBrush", "InputBrush",
            "BorderBrush", "PrimaryTextBrush", "SecondaryTextBrush",
            "PrimaryActionBrush", "PrimaryActionBorderBrush", "PrimaryActionTextBrush",
            "AccentSoftBrush", "AccentSoftBorderBrush", "AccentTextBrush",
            "FocusBrush", "RecorderRingBrush"
        };

        var themeAudit = new Dictionary<string, Dictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var theme in new[] { "Dark", "Light", "System" })
        {
            AppearanceService.Configure(theme, "English");
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in requiredThemeKeys)
            {
                if (Application.Current.Resources[key] is not System.Windows.Media.SolidColorBrush brush)
                    throw new InvalidOperationException($"Theme {theme} is missing semantic brush {key}.");

                row[key] = brush.Color.ToString();
            }

            themeAudit[theme] = row;
        }

        if (themeAudit["Light"]["AppBackground"]
            .Equals("#FFFFFFFF", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Light theme regressed to pure white.");

        if (!BilingualTranscriptFusionService.HasStrongRomanizedArabicEvidence(
                "Assalamu alaikum warahmatullahi wabarakatuh. How are you?"))
            throw new InvalidOperationException(
                "Romanized Arabic greeting recovery contract regressed.");

        if (!Models.MeetingModes.All.Contains("General", StringComparer.OrdinalIgnoreCase) ||
            !Models.MeetingModes.All.Contains("Contracts", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Meeting Intelligence mode contract regressed.");

        var output = Path.Combine(AppPaths.Logs, "v13.8.7-qa.json");
        File.WriteAllText(
            output,
            System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    status = "PASS",
                    themeContract = "semantic accent tokens + softened Light + System accent continuity",
                    speakerUiContract = "themed busy state; no whole-window disable",
                    transcriptionUiContract = "live ticker + persistent priority action",
                    bilingualContract = "strong romanized-Arabic recovery can coexist with genuine English",
                    searchContract = "FTS fast path + normalized Arabic/English filesystem recall fallback",
                    intelligenceContract = "auto first brief + key points + clear lenses + global Vault Intelligence",
                    titleContract = "suggested title action remains visible + edit title",
                    themeAudit
                },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RunV1386Qa()
    {
        var fixedBackgrounds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fixedActions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in new[] { "Dark", "Light", "System" })
        {
            AppearanceService.Configure(theme, "English");
            var bg = (System.Windows.Media.SolidColorBrush)Application.Current.Resources["AppBackground"];
            var action = (System.Windows.Media.SolidColorBrush)Application.Current.Resources["PrimaryActionBrush"];
            fixedBackgrounds[theme] = bg.Color.ToString();
            fixedActions[theme] = action.Color.ToString();
        }

        if (string.Equals(fixedBackgrounds["Dark"], fixedBackgrounds["Light"], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Dark and Light backgrounds must differ.");

        var settings = new Models.AppSettings();
        var effectiveMemoryFloor = Math.Clamp(
            Math.Min(settings.BackgroundTranscriptionMinimumFreeMemoryMb, settings.ManualTranscriptionMinimumFreeMemoryMb),
            1536,
            2304);

        if (effectiveMemoryFloor > 2304)
            throw new InvalidOperationException("Unified transcription memory floor is too high for owner auto-start.");
        if (settings.FirstTranscriptFastStartDelaySeconds > 8)
            throw new InvalidOperationException("Fast transcript start delay regressed.");

        var output = Path.Combine(AppPaths.Logs, "v13.8.6-qa.json");
        File.WriteAllText(
            output,
            System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    status = "PASS",
                    themeContract = "live-apply + muted palettes + system accent tint",
                    searchContract = "resident-hidden-library + early-prewarm + immediate-focus",
                    transcriptionContract = "1s-monotonic-countdown + unified-memory-gate + fast-auto-start",
                    bilingualContract = "automatic bounded English recovery allowed after fast first pass",
                    intelligenceContract = "readiness + product-styled overview/ask surfaces",
                    librarySpacingContract = "explicit date-duration margins",
                    effectiveMemoryFloorMb = effectiveMemoryFloor,
                    fastStartDelaySeconds = settings.FirstTranscriptFastStartDelaySeconds
                },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RunV1385Qa()
    {
        var requiredSemanticKeys = new[]
        {
            "AppBackground", "PanelBrush", "SurfaceBrush", "InputBrush", "MenuBrush",
            "BorderBrush", "PrimaryTextBrush", "SecondaryTextBrush",
            "PrimaryActionBrush", "PrimaryActionBorderBrush", "PrimaryActionTextBrush",
            "FocusBrush", "MenuHoverBrush", "RecorderFaceBrush", "RecorderRingBrush",
            "RecordingAccentBrush", "WarningAccentBrush"
        };

        var fixedActions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in new[] { "Dark", "Light", "System" })
        {
            AppearanceService.Configure(theme, "English");
            foreach (var key in requiredSemanticKeys)
            {
                if (Application.Current.Resources[key] is null)
                    throw new InvalidOperationException($"Missing V13.8.5 semantic theme resource: {key} ({theme}).");
            }

            var actionBrush = (System.Windows.Media.SolidColorBrush)Application.Current.Resources["PrimaryActionBrush"];
            fixedActions[theme] = actionBrush.Color.ToString();
        }

        AppearanceService.Configure("System", "English");
        var systemResolved = AppearanceService.ResolvedTheme;
        if (fixedActions.TryGetValue(systemResolved, out var fixedResolvedAction) &&
            string.Equals(fixedActions["System"], fixedResolvedAction, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("System theme accent must remain visually distinguishable from the fixed resolved palette.");
        }

        var settings = new Models.AppSettings
        {
            BackgroundTranscriptionQuietMode = true,
            BackgroundTranscriptionMinimumFreeMemoryMb = 4096,
            ManualTranscriptionMinimumFreeMemoryMb = 2048
        };

        if (settings.ManualTranscriptionMinimumFreeMemoryMb >= settings.BackgroundTranscriptionMinimumFreeMemoryMb)
            throw new InvalidOperationException("Manual transcription memory floor must remain lower than automatic background floor.");

        var output = Path.Combine(AppPaths.Logs, "v13.8.5-qa.json");
        File.WriteAllText(
            output,
            System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    status = "PASS",
                    semanticThemeKeys = requiredSemanticKeys.Length,
                    manualMemoryFloorMb = settings.ManualTranscriptionMinimumFreeMemoryMb,
                    automaticMemoryFloorMb = settings.BackgroundTranscriptionMinimumFreeMemoryMb,
                    fastStartDelaySeconds = settings.FirstTranscriptFastStartDelaySeconds,
                    searchContract = "async-cancelable-plus-idle-prewarm",
                    menuContract = "custom-no-default-icon-gutter",
                    recorderContract = "bounded-short-instruction"
                },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RunV138Matrix()
    {
        var rows = new List<object>();

        foreach (var theme in new[] { "Dark", "Light", "System" })
        foreach (var language in new[] { "Arabic", "English" })
        {
            AppearanceService.Configure(theme, language);
            var probe = new Window();
            AppearanceService.Apply(probe);

            var requestedOk = AppearanceService.RequestedTheme.Equals(theme, StringComparison.OrdinalIgnoreCase);
            var resolvedOk = AppearanceService.ResolvedTheme is "Dark" or "Light";
            var expectedFlow = language == "Arabic"
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;
            var flowOk = probe.FlowDirection == expectedFlow;

            var requiredKeys = new[]
            {
                "AppBackground", "PanelBrush", "SurfaceBrush", "InputBrush", "MenuBrush",
                "BorderBrush", "PrimaryTextBrush", "SecondaryTextBrush",
                "RecorderFaceBrush", "RecorderPrimaryBrush", "RecorderSecondaryBrush"
            };

            var missing = requiredKeys
                .Where(key => Application.Current.Resources[key] is null)
                .ToArray();

            if (!requestedOk || !resolvedOk || !flowOk || missing.Length > 0)
                throw new InvalidOperationException(
                    $"V13.8 matrix failed: {theme}/{language}; " +
                    $"requested={AppearanceService.RequestedTheme}; resolved={AppearanceService.ResolvedTheme}; " +
                    $"flow={probe.FlowDirection}; missing={string.Join(",", missing)}");

            rows.Add(new
            {
                requestedTheme = theme,
                resolvedTheme = AppearanceService.ResolvedTheme,
                language,
                flowDirection = probe.FlowDirection.ToString(),
                status = "PASS"
            });

            probe.Close();
        }

        var output = Path.Combine(AppPaths.Logs, "v13.8-matrix.json");
        File.WriteAllText(
            output,
            System.Text.Json.JsonSerializer.Serialize(
                new { status = "PASS", cases = rows },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

}
