# V28 Final Functional Closeout

**Result: `V28_READY_FOR_OWNER_FUNCTIONAL_SMOKE`**

V28A and V28B implementation and machine QA are complete from V27 commit `e0c0d7769fc37b8f641ad43e5e40cea65218d03f`. V27 RTL geometry and Word changes were retained. V25 remains the installed authority; this candidate was built and tested in an isolated publish directory. No Aims files were touched, and no Taste Pass or general redesign was performed. Owner acceptance remains pending.

## V28A — functional gaps

- The audio import icon, existing import button, empty card, and supported drag/drop route through one import command. Unsupported drops are rejected without a library mutation; keyboard access remains on the button.
- RTL physical geometry is held in an LTR shell, while Arabic content and alignment are RTL. The actual production `IntelligenceWindow` QA records DIPs and screenshots for Arabic and English.
- Same-language legacy Arabic/English cached JSON uses the current UI flow immediately. It does not regenerate solely for layout. Current exporter output is used for a new export; historical DOCX bytes remain unchanged. Incompatible-language or transcript-hash-stale caches show refresh-required and cannot export.
- Sparse eight-second Arabic and English fixtures stop with `INSUFFICIENT_EVIDENCE`; they produce no Ready report and no invented report JSON.

## V28B — cloud acceleration

- Local remains the default. Settings expose Local, OpenAI, Gemini, DeepSeek, and Groq with provider/model/key/test/remove controls and an explicit cloud toggle.
- Ask-this-meeting, Ask-the-vault, and report generation use the provider router. OpenAI-compatible requests are shared across supported providers. Transcript/evidence text only is sent; audio is not sent.
- Keys are protected with Windows DPAPI CurrentUser. Consent is required before cloud requests. Cancellation and bounded retry/fallback behavior are covered with fake HTTP handlers; the final run made zero live provider calls and used no real credentials.
- Independent cloud extraction chunks use bounded concurrency after transcript hierarchy/evidence checks. Local behavior remains sequential.

## QA results

| Gate | Result |
| --- | --- |
| Release build, `win-x64` | PASS, 0 errors |
| Windows publish | PASS, 0 errors; 4 warning entries (`CS8602` x1, `CS0649` x3) |
| `--self-test` | PASS |
| `--rtl-layout-qa` | PASS; runtime executable SHA recorded |
| `--v13.8.9-qa` | PASS |
| `--v13.8-matrix` | PASS |
| `--cloud-provider-qa` | PASS; 0 live calls |
| Arabic/English DOCX OpenXML fixtures | PASS; 0 validator errors |
| Removed controls / event handlers / features | 0 / 0 / 0 |
| Aims | NO TOUCH |
| AI/Speaker runtime self-tests | SKIPPED: target model and fixture assets are absent; see `QA_EVIDENCE/V28/TARGET_ASSET_AVAILABILITY.json` |

The final published executable is `Archestro.MeetingVault.exe`, 211,978,871 bytes, SHA-256 `3E0CADFEBFEB2861438D7321AC491F07B9874A37E3891FAEBC2FC6E65DF8C5E1`, informational version `V28.0.0`. Build and publish logs and file hashes are in `QA_EVIDENCE/V28/`.

### WPF physical geometry

Coordinates below come from the actual production window at a 1536×864 DIP viewport. Arabic uses an LTR physical shell and RTL content: nav host x=1024, width=500, right=1524; Report x=1364, width=160, right=1524; Topics x=767.6, width=739.6, right=1507.2; Key Points x=12, width=739.6, right=751.6. Arabic report headings/items align right; evidence timestamp remains LTR (`00:00:52`). English nav and Report begin at x=12. Full values and screenshots are in `QA_EVIDENCE/V28/RTL_LAYOUT_COORDINATES_V28.json` and `QA_EVIDENCE/V28/screenshots/`.

### Word semantic proof

Arabic fixture: `word/settings.xml` exists; compatibility mode is 15; `themeFontLang bidi=ar-SA`; section BiDi is present; 36/36 Arabic paragraphs have `w:bidi`; 32 non-centered Arabic paragraphs use logical `start`; 40/40 Arabic runs have RTL semantics; and all 3 tables are visual RTL. `BodyText` has BiDi and logical `start`. English fixture has compatibility mode 15, `themeFontLang bidi=en-US`, and no Arabic BiDi leakage. Both fixtures preserve logical `API` and have zero OpenXML validator errors. Fixture hashes and counts are in `WORD_BIDI_SEMANTIC_PROOF_V28.json`.

Microsoft Word interactive rendering was not run: the local Word COM preflight script required by the installed Word workflow was unavailable. Serialized OpenXML evidence is not represented as a live Word visual pass; owner smoke remains pending.

## Commits and package

- V28A source implementation: `3a1339d`
- V28B evidence/package commit: this report, QA evidence, and source/proof bundle.
- Final package: `V28_FINAL_FUNCTIONAL_CLOSEOUT_SOURCE_AND_PROOF.zip`, containing the source snapshots, DOCX fixtures, QA matrix, coordinates, screenshots, provider tests, and build/hash evidence.

The Windows user log baseline was restored and its original file hashes verified. One newly created V28 provider-QA diagnostic (`v28-provider-qa-error.txt`) remains in the user Logs folder because local command policy rejected removal; it records a disposed fake-test response from an earlier failed QA attempt, contains no provider secret, and the final provider QA passes.
