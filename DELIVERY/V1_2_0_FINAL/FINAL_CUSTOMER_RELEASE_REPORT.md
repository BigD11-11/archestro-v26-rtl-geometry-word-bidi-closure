# Archestro Meeting Vault — Customer 1.2.0 / Internal V30

**Result:** `ARCHESTRO_MEETING_VAULT_1_2_0_CUSTOMER_RELEASE_PASS`

**Release source commit:** `17f159d627df18c38b60f83cdf79d0a8b17f34d9`

**Runtime marker:** `1.2.0+V30.17f159d627df18c38b60f83cdf79d0a8b17f34d9`

**Owner installation:** updated and left running from the Desktop shortcut; Start Menu launch also verified.

**Owner data:** preserved; no business-table changes detected.

## Delivery artifacts

- Customer offline package: `ARCHSTRO_MEETING_VAULT_1_2_0_CUSTOMER_OFFLINE_PACKAGE.zip` (90 files; 3,763,321,163 bytes), SHA-256 `21128AC9A9914937611799D9837309BD2917E8956DC25044A42141D327185DCF`.
- Source ZIP: [ARCHSTRO_MEETING_VAULT_V30_SOURCE.zip](ARCHSTRO_MEETING_VAULT_V30_SOURCE.zip).
- Proof ZIP: [ARCHSTRO_MEETING_VAULT_V30_PROOF.zip](ARCHSTRO_MEETING_VAULT_V30_PROOF.zip).
- Result ZIP: [ARCHSTRO_MEETING_VAULT_V30_RESULT.zip](ARCHSTRO_MEETING_VAULT_V30_RESULT.zip).
- SHA-256 for all release artifacts: [ARTIFACT_SHA256.txt](ARTIFACT_SHA256.txt). Each source/proof/result archive passed ZIP integrity validation.
- Installed executable SHA-256: `720F6E4C841F76B772A556A65CED3E3AE2B003D1F18C5E6C66F27517E2E0202B`.
- Customer package manifest: all 90 file hashes verified; executable hash matches the final installed binary.
- Package is self-contained `win-x64` and includes Local AI runtime/model assets. The clean install initialized an empty library with Local/Offline defaults and no cloud credentials.

## What shipped

- System Status Pulse with recording, system audio, intake, transcription, Local AI, and cloud readiness signals.
- Google Drive Desktop root discovery, Archestro Inbox, automatic watched-folder intake, queueing, and duplicate suppression.
- Groq Whisper Large V3 Turbo cloud transcription path with bounded retry and cancellation behavior, Arabic/English segments, and duration-based cost reporting.
- DeepSeek cloud intelligence path and Local Qwen intelligence; Offline, Cloud Fast, and Custom processing modes.
- Meeting/report/Ask usage ledger and cost views, building on immutable provider pricing/FX snapshots from 1.1.0. Local cost is zero; synthetic cloud usage/cost calculations are deterministic QA-only. Cached report reopen does not add a ledger record.
- Bilingual Arabic/English interface, System Audio capture, migration-compatible upgrade, and clean customer first-run setup.
- WhatsApp Business and the Mobile companion remain history-only and were not implemented in 1.2.0.
- Aims was not touched.

## Build, package, and QA

| Gate | Result | Evidence |
|---|---|---|
| Release build and win-x64 self-contained publish | PASS; 0 errors, 12 existing warnings | `QA_EVIDENCE/V30/FINAL_BUILD_PUBLISH.txt` |
| Offline regression and cloud guardrails | PASS | `QA_EVIDENCE/V30/FINAL_REGRESSION.json` |
| Inbox, status, configured Drive root, Groq parsing/retry/cancel/cost | PASS with synthetic providers/paths | `EVIDENCE/FINAL_V30_QA.json` |
| DeepSeek/cloud provider guardrails | PASS for protocol shapes, redaction, retry/timeout/cancel/fallback and consent guards; 0 live provider calls | `EVIDENCE/FINAL_CLOUD_PROVIDER_QA.json` |
| Local Qwen report and zero-cost ledger | PASS; 6 report items, 142.119 s; cached reopen did not increment ledger | `QA_EVIDENCE/V30/FINAL_LOCAL_COST_QA.json` |
| Clean package install and first-run defaults | PASS; Local/Offline, cloud disabled, empty library | `QA_EVIDENCE/V30/FINAL_ARCHIVE_CONTENT_VERIFICATION.json`, `FINAL_UI_English.json` |
| `native-whisper` system QA | PASS on fresh retry; 89.5 s | `QA_EVIDENCE/V30/FINAL_SYSTEM_QA_RETRY.json` |
| Arabic/English physical WPF geometry | PASS; no geometry failures | `QA_EVIDENCE/V30/FINAL_RTL_LAYOUT_QA.json` |
| Arabic/English DOCX structural and Word visual checks | PASS; 5 tables each, Arabic BiDi, English LTR, images/captions, 0 OpenXML validation errors | `EVIDENCE/FINAL_WORD_VISUAL_OPENXML_VISUAL_PROOF.json` and `VISUALS/WORD_*_PAGE.png` |
| Owner migration/data integrity | PASS; 13 meetings, 10 categories, 8 important marks, 15 FTS rows unchanged; integrity `ok`, FK errors 0 | `QA_EVIDENCE/V30/OWNER_DB_AFTER_UPGRADE_SEMANTIC_PROOF.json` |
| Desktop and Start Menu shortcuts | PASS; both target installed executable; both launched; Desktop instance left running | `QA_EVIDENCE/V30/OWNER_SHORTCUTS_SAFE_PROOF.json` |
| Privacy scan of clean customer package | PASS; 90 files, 0 forbidden names, 0 sensitive text matches | `QA_EVIDENCE/V30/PACKAGE_PRIVACY_SCAN.json` |
| Taste Pass | PASS for bilingual layout, readiness hierarchy, typography, and empty-library state; low-storage warning is visible and truthful | `VISUALS/APP_ARABIC.png`, `VISUALS/APP_ENGLISH.png`, cropped Word page screenshots |

## Owner upgrade and data safety

Before updating the owner installation, a full database/WAL/SHM/settings backup was written to a local recovery directory. That backup and its manifest are private owner artifacts and are intentionally not included in GitHub or delivery ZIPs. The installed product was updated to V30 without changing the business data. Post-upgrade SQLite `integrity_check` returned `ok`; `foreign_key_check` returned zero errors. Semantic row counts and SHA-256 values for meetings, categories, important marks, meeting FTS, and the usage ledger match the pre-upgrade audit. Settings remain Local / Offline, cloud disabled, with no encrypted Groq or intelligence key saved.

Both Desktop and Start Menu links target the installed V30 executable. Both launch paths were exercised. The final app process was launched from the Desktop shortcut and remains running. A sanitized owner proof is in `EVIDENCE/OWNER_UPGRADE_SAFE_PROOF.json`; exact owner paths, settings contents, databases, and backups are deliberately excluded from the public evidence bundle.

## Arabic/English and Taste evidence

The physical WPF geometry QA ran the production Intelligence window at 1536×864 DIPs. Arabic flow is RTL while the report shell stays LTR to prevent double mirroring; the report button occupies the rightmost 160 DIPs (`x=1364`, `right=1524`), and the corresponding English button is leftmost (`x=12`, `right=172`). No geometry failures were reported.

Synthetic Arabic and English DOCX fixtures were opened in desktop Microsoft Word. The Arabic fixture has section/paragraph/run/table BiDi semantics; the English fixture is LTR. Both have five tables, two images with captions, and zero OpenXML validator errors. Screenshots included in public evidence are page crops only and omit the signed-in Office chrome. See `VISUALS/` and `EVIDENCE/`.

## Known validation boundaries

- **Cloud live smoke:** no live Groq/DeepSeek API request was made because this run had no authorized test credential. The cloud code paths, parsing, guardrails, retry/cancellation, and synthetic cost behavior passed. Do not read this report as evidence of live account authentication or vendor availability.
- **Google Drive Desktop:** this machine had zero actual synced Drive roots. The configured synthetic-root discovery and watcher tests passed; a real owner-synced folder smoke was unavailable.
- **Native transcription:** a preceding run failed during transcript finalization; its retry passed. The failure's hidden exception was not exposed, so root cause is not claimed.
- **Storage:** QA host showed about 2.7 GB free and the UI correctly displayed a low-storage warning. This is a host condition, not a clean-package privacy or integrity failure.

These limits are also recorded in `PROJECT_STATUS.md` and the QA matrix. They are retained to keep synthetic tests distinct from live service/owner evidence.

## Checkpoints and release history

- V29 / Customer 1.1.0 and accepted V28R3 / Customer 1.0.0 artifacts were not modified.
- V30 implementation commits: `cf887f658e648ca67ef55a5371ceccbb2a5ae1cd`, `de1a06d087493de8faad44a028c1e2eb5d7a3019`, `17f159d627df18c38b60f83cdf79d0a8b17f34d9`.
- Release metadata and evidence are on `codex/v30-full-closeout`; source/proof/result archive checksums are in `ARTIFACT_SHA256.txt`.
