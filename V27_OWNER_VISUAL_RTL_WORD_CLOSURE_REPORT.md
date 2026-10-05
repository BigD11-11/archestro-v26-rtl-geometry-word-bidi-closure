# V27 Owner Visual RTL and Word Closure — Execution Report

**Outcome:** `V27_READY_FOR_OWNER_VISUAL_SMOKE`

## Scope and authority

Work was performed on `codex-v27-owner-visual-rtl-word-closure` for PR #1 in `BigD11-11/archestro-v26-rtl-geometry-word-bidi-closure`. The five frozen V26 source hashes were checked against the V26 source/proof and matched the task brief. V26 remains the installed authority; the V27 build was not installed. Aims were not touched, no named controls/events were removed, and no general redesign was made.

## Changes

- WPF report physical shells are explicitly `LeftToRight`; Arabic/English content direction is applied to text-bearing navigation buttons while explicit column ordering controls physical placement. This avoids the inherited Window RTL direction mirroring the physical shell on top of manual column mapping.
- Word export now creates `word/settings.xml` with compatibility mode 15 and `themeFontLang` (`bidi=ar-SA` for Arabic, `en-US` for English). Arabic paragraphs/body style use logical `start` justification with paragraph/run BiDi, section BiDi, and table `bidiVisual` retained.
- QA measures the real production `IntelligenceWindow` against its outer window origin and captures the full window visual tree; DOCX fixtures and serialized semantic checks are included.

## Build and publish

Windows `win-x64` Release build and publish both exited 0. Build had 0 errors and 8 existing warnings (`CS8602`, `CS0649`) in untouched `MainWindow.xaml.cs`. Published executable SHA-256: `27487224BAC3F7BC38479D61664790D34CB42BA99CEDA619BC5FAC442223DE3F` (211,876,471 bytes). It is deliberately not included in the source/proof ZIP; exact build/publish evidence is in `QA_EVIDENCE/BUILD_PUBLISH_HASHES.txt`.

## QA matrix

| Gate | Result |
|---|---|
| `--rtl-layout-qa` | PASS, exit 0 |
| `--self-test` | PASS, exit 0 |
| `--v13.8.9-qa` | PASS, exit 0 |
| `--v13.8-matrix` | PASS, exit 0 |
| OpenXmlValidator Arabic / English | 0 / 0 errors |
| Feature/control/event/Aims structural check | PASS; 99 named controls and 14 event bindings preserved; Aims untouched |
| Arabic/English full-window WPF screenshots | Captured and visually inspected |

Detailed outputs are under `QA_EVIDENCE/`.

## WPF physical geometry evidence

Coordinates are DIPs relative to the top-left of the actual production `IntelligenceWindow` at 1536 x 864. Arabic report content is x=12, width=1512, right=1524. Its nav host is x=1024, width=500, right=1524; buttons occupy [1364,1524], [1194,1354], [1024,1184], leaving 10 DIP gaps. The Topics card (logical first) is on the physical right at x=767.6, right=1507.2; Key Points is on the physical left at x=12, right=751.6. The physical report shell remains LTR while Arabic text content is RTL. English coordinates mirror this arrangement appropriately. Full measurements and failure arrays are in `QA_EVIDENCE/RTL_LAYOUT_COORDINATES_V27.json` (no failures).

## DOCX serialized semantic evidence

Independent inspection of ZIP part bytes shows Arabic `settings.xml` compatibilityMode=15 and `themeFontLang bidi=ar-SA`; 36/36 Arabic paragraphs carry BiDi, 32 non-centered Arabic paragraphs use logical `start`, BodyText has BiDi/start, 40/40 Arabic runs are RTL, section BiDi is present, and all 3 Arabic tables are `bidiVisual`. Arabic/English OpenXmlValidator counts are both zero. English uses compatibilityMode=15 and `themeFontLang bidi=en-US`, without Arabic BiDi. Fixtures and hashes are listed in `QA_EVIDENCE/WORD_BIDI_SEMANTIC_PROOF_V27.json`; representative raw serialized fragments are in `QA_EVIDENCE/WORD_BIDI_SERIALIZED_XML_EXCERPTS.md`.

## Rendering boundary

The WPF screenshots are automated captures of the production WPF window with deterministic QA data, not owner real-data acceptance. Microsoft Word is installed, but the prescribed `docx-win` smoke workflow could not start because its required `office_com_common.psm1` helper is absent; the document-session connector is also unavailable. Therefore this evidence proves serialized DOCX semantics and schema validity, not a live Microsoft Word rendered-page screenshot. Open the included Arabic and English fixtures in Microsoft Word for the requested owner visual smoke and confirm normal (non-Compatibility Mode) rendering.

## Changed files and hashes

See `QA_EVIDENCE/CHANGED_FILE_SHA256.txt` for SHA-256 values for each changed product source file and all evidence/package deliverables. The five source files are the only code inputs carried forward from V26; `App.xaml.cs` remains byte-identical.

## Delivery

Final source/proof package: `V27_OWNER_VISUAL_RTL_WORD_CLOSURE_SOURCE_AND_PROOF.zip` in this repository. The package contains the changed source, fixture DOCX files, coordinate and Word proofs, screenshots, QA matrix and this report. It excludes the 212 MB executable.
