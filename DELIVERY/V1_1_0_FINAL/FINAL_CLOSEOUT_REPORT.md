# Archestro Meeting Vault 1.1.0 / V29 — Final Closeout

**Release result: `ARCHESTRO_MEETING_VAULT_1_1_0_CUSTOMER_RELEASE_PASS`**

**Build identity:** CustomerVersion 1.1.0 · InternalBuild V29 · win-x64 self-contained .NET 10 WPF · source `d80fc3a0638888978c5628f252ea87ad353b025c` · build time `2026-10-06T06:04:40Z`.

## Release artifacts

- Customer offline package: `ARCHSTRO_MEETING_VAULT_1_1_0_CUSTOMER_OFFLINE_PACKAGE.zip`
- Package SHA-256: `9E3E618AB2D40EA24DACCC56D90F6D119F34456CB8755902774CBD43A443CD47` · 3,761,421,378 bytes.
- Published executable SHA-256: `8D4463450CA627CBC3D470A12E18A1FE206EBF1980BED2495DB66C5EFF0DE38E` · FileVersion `1.1.0.0` · ProductVersion `1.1.0+V29`.

## Implemented

- AI usage ledger records provider, model, operation, response ID, elapsed time, provider-returned input/cache-hit/cache-miss/output/reasoning/total token counts, immutable pricing/FX snapshot IDs, USD/SAR amount, and status. It never stores prompts, transcripts, or generated response text.
- Local API cost is zero. Cloud cost uses the provider's returned token counts and a versioned price and FX snapshot, so later price changes do not rewrite recorded history. Duplicate report reopen is idempotent and does not create another charge.
- Settings dashboard shows Today, This Month, and All Time, tokens, USD/SAR totals, and provider/model breakdown. Export contains accounting metadata only.
- WPF startup no longer runs WASAPI endpoint initialization on the dispatcher. `dotnet-dump` showed the UI thread blocked in `IAudioClient.Initialize` from `AudioMeterService.Start`; initialization and refresh now run as a single-flight background operation, and closing does not wait on a stuck optional meter probe.

## Verification matrix

| Gate | Result | Evidence |
|---|---|---|
| Release build and win-x64 publish | PASS | Executable hash and assembly metadata above |
| Offline self-test | PASS | Final installed executable, exit 0 |
| System QA | PASS | Real Windows WASAPI mic/loopback Start/Stop/Pause/Resume, lossless master/M4A, SQLite persistence, configured `native-whisper` runtime/model/dependencies |
| Local AI report | PASS | Actual Qwen3-4B-Q4_K_M; 6 report items; 2 ledger rows; `LOCAL_ZERO`; 150,799 ms; no external network; cached reopen added no row |
| Cost accounting | PASS | Response-usage parsing and snapshot arithmetic fixtures; cache/no-double-count behavior; Local zero-cost ledger |
| Word Arabic / English | PASS | Final executable `--word-visual-qa` exit 0; 5 tables per fixture; Arabic section and tables BiDi; English LTR; two images and captions per report; zero OpenXML validator errors |
| Live Microsoft Word visual review | PASS | Actual Word screenshots in `DELIVERY/V1_1_0_VISUAL_QA/WORD_*_PAGE_*.png`; screen capture verifies `WINWORD.EXE`, masks the Office account area; no clipping found |
| Final package clean install | PASS | Extracted this exact ZIP, ran `Install.ps1`, launched 1.1.0/V29; UI Automation observed `0 meetings`, Local (default), zero usage/cost; SQLite `integrity_check=ok`, zero FK violations; native bridge and Local model present |
| Personal install and shortcuts | PASS | Existing install updated in place; Desktop and Start Menu targets match the installed executable; Start Menu launch reached `startup-ready`; Settings persisted Local (default), Cloud disabled, no key |
| Data migration/preservation | PASS | Transactional additive migration with backup; current semantic hashes match pre-upgrade backup for meetings 13/13, categories 10/10, important marks 8/8; SQLite integrity `ok`; FK violations 0; ledger rows 0 |
| 1.1.0 package privacy | PASS | 90 entries; forbidden/private path matches 0; owner path matches 0; secret-pattern matches 0 |
| Taste Pass | PASS | Reviewed synthetic Word pages and Settings/empty-library UI; Arabic right-edge flow and image/table placement correct, English left-edge flow correct, no clipped content |

## Baseline and privacy

The accepted 1.0.0 ZIP was re-hashed and remains unchanged at `9EB8006CDAFC56587782A6661CABAC3DB2575F7FBE2DD2EB280727B7941F52C0`. Its scan found one static third-party sherpa-onnx demo WAV at `App/SpeakerModels/0-four-speakers-zh.wav`; it is not a user recording or meeting artifact. No owner DB, settings, reports, logs, paths, or keys were found. The 1.1.0 package omits that demo asset and passed with zero privacy-scan matches.

Owner data remains local and is not included in any customer/source/proof/result package. The personal settings and logs scan found zero `sk-...` plaintext patterns; the encrypted cloud key field is empty, Local remains default, and cloud is disabled. No live cloud request was made in this release QA; provider response parsing and pricing math were tested offline with deterministic fixtures.

## Evidence index

- `DELIVERY/V1_1_0_VISUAL_QA/OPENXML_VISUAL_PROOF.json`
- `DELIVERY/V1_1_0_VISUAL_QA/WORD_CAPTURE_MANIFEST.json`
- `DELIVERY/V1_1_0_VISUAL_QA/LOCAL_AI_COST_QA.json`
- `DELIVERY/V1_1_0_VISUAL_QA/SYSTEM_QA_NATIVE_AUDIO_PROOF.json`
- `DELIVERY/V1_1_0_VISUAL_QA/CLEAN_INSTALL_UI_PROOF.json`
- `DELIVERY/V1_1_0_VISUAL_QA/OWNER_INSTALLED_UI_PROOF.json`
- `DELIVERY/V1_1_0_VISUAL_QA/DATA_INTEGRITY_PROOF.json`
- `DELIVERY/V1_1_0_VISUAL_QA/PRIVACY_SCAN_V1_0_0.md` and `PRIVACY_SCAN_V1_1_0.json`

`VERSION.md` and `RELEASE_NOTES.md` preserve the 1.0.0 baseline and document the 1.1.0 feature release. The release package, source/proof ZIP, and result ZIP hashes are also listed in `ARTIFACT_SHA256_MANIFEST.json` after package creation.
