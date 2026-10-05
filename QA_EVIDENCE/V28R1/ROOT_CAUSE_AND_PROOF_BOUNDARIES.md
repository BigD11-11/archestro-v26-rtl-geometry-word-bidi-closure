# V28R1 Root Cause and Proof Boundaries

## Historical V28 observation

The owner run stopped displaying progress at 66% for 14:58 and saved no report. In the V28 implementation, 66% is emitted immediately before `MergeReportDtos`, `ValidateDtoEvidence`, and `ValidateParticipantOwners`; 76% is emitted immediately before the synthesis request. The preserved old run does not contain per-stage timestamps, so it cannot identify which internal 66-to-76 operation was the exact last completed substage. No inference that synthesis itself caused the historical stop is justified from that display alone.

## R1 instrumentation and bounded behavior

V28R1 writes sanitized stage, elapsed-time, count, and request-duration records for extraction, merge, validation, synthesis, JSON parse/repair, language normalization, and save. It does not write prompts, transcript text, model responses, API keys, or report prose. The exact transitions observed on the reproduced meeting are recorded below.

The first installed same-meeting replay used Local / Qwen3-4B-Q4_K_M with cloud disabled. The target was a 62-second meeting with 31 evidence lines and one extraction chunk. At `2026-10-05T20:34:20Z`, instrumentation recorded `extract-start`, then `model-request-start; stage=chunk; maxTokens=760`, followed by `extract-await-start; taskCount=1`. No chunk completion, merge, or validation stage was entered. At `2026-10-05T20:37:20Z`, that request terminated at 180.06 seconds with `MeetingReportTimeoutException(stage=chunk)`; no report was saved, the three pre-existing report files remained byte-identical, and SQLite integrity remained `ok` with 44 logical rows. This proves the new replay's last entered stage and last completed stage. It does not retroactively identify the inner 66-to-76 substage of the historical uninstrumented V28 run.

Local model requests are bounded to 180 seconds; a report is bounded to 8 minutes. The window owns and cancels the active operation. Failed extraction never saves a Ready report. Synthesis failure may finish from validated evidence through deterministic summary fallback. A language-invalid result cannot save. Save uses staged files and restores the prior cache if a later output fails to commit.

The bounded follow-up adds one Local-only compact extraction retry after the first request timeout (380 output tokens, same evidence and context). The retry bypasses the warm server once and uses the existing one-shot CLI fallback, avoiding a second wait on the observed warm-server timeout mode. The retry remains subject to the same per-request timeout, evidence validation, report-wide deadline, and no-save-on-extraction-failure rule. Safe runtime markers identify server startup/health, request start/response/cancellation, and fallback without recording request content. The CLI route is a targeted hypothesis and remains subject to the same-meeting owner replay evidence below.

## Historical exactness

The phrase “66% stuck” identifies the user-visible boundary, not a measured inner method. V28R1 will report exact new-run last-entered/last-completed markers; historical substage remains unavailable because the old run lacked instrumentation.
