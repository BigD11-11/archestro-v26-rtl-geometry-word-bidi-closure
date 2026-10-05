# V28R1 Root Cause and Proof Boundaries

## Historical V28 observation

The owner run stopped displaying progress at 66% for 14:58 and saved no report. In the V28 implementation, 66% is emitted immediately before `MergeReportDtos`, `ValidateDtoEvidence`, and `ValidateParticipantOwners`; 76% is emitted immediately before the synthesis request. The preserved old run does not contain per-stage timestamps, so it cannot identify which internal 66-to-76 operation was the exact last completed substage. No inference that synthesis itself caused the historical stop is justified from that display alone.

## R1 instrumentation and bounded behavior

V28R1 writes sanitized stage, elapsed-time, count, and request-duration records for extraction, merge, validation, synthesis, JSON parse/repair, language normalization, and save. It does not write prompts, transcript text, model responses, API keys, or report prose. New live owner-run evidence will identify the exact stage transitions for the reproduced meeting.

Local model requests are bounded to 180 seconds; a report is bounded to 8 minutes. The window owns and cancels the active operation. Failed extraction never saves a Ready report. Synthesis failure may finish from validated evidence through deterministic summary fallback. A language-invalid result cannot save. Save uses staged files and restores the prior cache if a later output fails to commit.

## Historical exactness

The phrase “66% stuck” identifies the user-visible boundary, not a measured inner method. V28R1 will report exact new-run last-entered/last-completed markers; historical substage remains unavailable because the old run lacked instrumentation.
