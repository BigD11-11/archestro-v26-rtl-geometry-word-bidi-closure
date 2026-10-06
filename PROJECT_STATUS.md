# Project status — Archestro Meeting Vault

**Current release:** 1.2.0 / V30

**Release state:** `ARCHESTRO_MEETING_VAULT_1_2_0_CUSTOMER_RELEASE_PASS`

**Release source commit:** `17f159d627df18c38b60f83cdf79d0a8b17f34d9`

**Customer package SHA-256:** `21128AC9A9914937611799D9837309BD2917E8956DC25044A42141D327185DCF`

## Current result

- Windows x64 Release publish and clean offline package verification: PASS.
- Clean package install and Local/Offline first-run defaults: PASS.
- Owner upgrade and both shortcuts: PASS; owner database integrity and foreign keys pass, and business-table semantic hashes match pre-upgrade.
- Local Qwen report and zero-cost ledger QA: PASS; cached reopen did not add ledger entries.
- Arabic/English WPF geometry and DOCX/Word visual checks: PASS.
- Inbox watcher/deduplication, synthetic Google Drive configured-root detection, Groq transport/cost/cancellation guards, and system status signals: PASS in deterministic QA.
- `native-whisper` system QA: PASS on retry. One preceding run failed during transcript finalization; the service did not expose its swallowed exception.

## Acceptance limits recorded

- No live Groq or DeepSeek request was sent because this run had no authorized test credential in the clean QA environment.
- The device exposed no actual Google Drive Desktop root. Synthetic configured-root detection passed; a real synced-folder owner smoke remains unobserved.
- Desktop reported low available storage during visual QA. The package and owner install are valid; the warning is retained as a device condition.
- WhatsApp Business and mobile companion remain history-only and are outside 1.2.0.

See [`DELIVERY/V1_2_0_FINAL/FINAL_CUSTOMER_RELEASE_REPORT.md`](DELIVERY/V1_2_0_FINAL/FINAL_CUSTOMER_RELEASE_REPORT.md) and its QA matrix for evidence and exact artifact hashes.
