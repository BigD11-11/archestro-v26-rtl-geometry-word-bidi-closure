# Customer release 1.1.0 — Internal build V29

Release type: backward-compatible feature release from the accepted V28R3 / 1.0.0 baseline.

## Added

- AI usage ledger entries tied to meeting/report IDs and operations, storing provider/model, time, response ID, elapsed time, input/cache-hit/cache-miss/output/reasoning/total tokens, and pricing snapshot references. Prompts, transcripts, and generated response text are excluded from the ledger.
- Local requests are recorded at zero API cost. Cloud costs use provider-returned token counts and immutable, versioned price and FX snapshots. Historical usage retains the exact rates used when it was recorded.
- Settings usage dashboard for today, this month, and all time, with USD/SAR totals and provider/model breakdown; CSV export contains accounting metadata only.
- Synthetic Arabic and English Word visual QA fixtures with embedded images, captions, bilingual content, and tables.

## Compatibility and data

The database change is additive and transactional. Existing meetings, categories, and important marks are compared semantically against the pre-upgrade backup. Customer release 1.0.0 artifacts remain immutable.

## Version policy

- Bug fix: 1.1.1
- Compatible feature: 1.2.0
- Breaking release: 2.0.0

## Customer release 1.2.0 — Internal build V30

Release type: backward-compatible feature release. The 1.1.0 release history and its accepted artifacts remain unchanged.

### Added and verified

- System Status Pulse with six live readiness signals; packaged Local Qwen model detection now checks the packaged runtime as well as owner model paths.
- Google Drive Desktop discovery, Archestro Inbox folder watching, intake queue, and duplicate-file suppression.
- Groq Whisper Large V3 Turbo cloud transcription path with Arabic/English segment parsing, bounded retries, cancellation, and duration-based cost estimation.
- DeepSeek cloud intelligence path and Local Qwen intelligence, with Offline, Cloud Fast, and Custom processing modes.
- Local transcription and intelligence defaults; system audio capture readiness and `native-whisper` runtime QA.
- Bilingual Arabic/English UX and Word outputs; physical WPF layout, DOCX structure, and Microsoft Word visual checks.
- Customer offline package with local models/runtimes, first-run clean data, and no owner meetings, settings, keys, logs, cache, or cost history.

### Release evidence and limits

See `DELIVERY/V1_2_0_FINAL/FINAL_CUSTOMER_RELEASE_REPORT.md`. Live cloud calls were not made without a run-authorized credential. No physical Google Drive Desktop root was present during QA. `native-whisper` passed on retry after one transient failure whose swallowed exception was not exposed. The exact offline package SHA-256 is recorded in the report and manifest.

### Version policy

- Bug fix: 1.2.1
- Compatible feature: 1.3.0
- Breaking release: 2.0.0
