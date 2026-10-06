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
