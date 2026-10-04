# CODEX V27 — Owner Visual RTL + Word RTL Closure

Status: EXECUTE NOW
Owner: Mohammed
Scope: bounded corrective engineering only
Installed authority before this task: V26 target technical PASS
Taste Pass: HOLD
Aims: NO TOUCH

## Objective

Fix the two owner-proven defects that remain after V26:

1. Arabic Meeting Report is still physically left-origin in the real owner UI even though synthetic geometry QA passed.
2. Exported Arabic DOCX still renders Arabic paragraphs from the left in real Microsoft Word and opens in Compatibility Mode.

Do not restart product engineering. Do not redesign unrelated UI. Do not remove features. Preserve all V26 behavior except the minimum corrections needed for these two defects.

## Evidence / current truth

V26 exact source/proof authority is already in this repo:

`V26_ROOT_RTL_GEOMETRY_WORD_BIDI_CLOSURE_SOURCE_AND_PROOF.zip`

Frozen V26 product hashes:
- App.xaml.cs
  `60049F147E77EC7A0100D915BCE6EEC4A04EA3F4EF64DA4E3B2BD6904DD30FD7`
- IntelligenceWindow.xaml
  `EE7B6BFD224CF41D036A053FBBA1A19A6FC6E48FFA60999583B4E7DB4DA6A82C`
- IntelligenceWindow.xaml.cs
  `21F135D640C55231917832D9CB31842C97DFDFD852472D811F32413444F0D79B`
- MeetingReportWordExporter.cs
  `975372D8691C7949AF7FB98F070DE3A9DFAEC569F133C2A2B4CE3C706BF5454E`
- SelfTestService.cs
  `F7D31F67A0AF8BA81405E84ADE5234650F7E1DC6EA44D7B37C3C13B62E47835B`

Owner screenshots after installed V26 PASS prove:
- Arabic report nav buttons still appear physically on the LEFT.
- Paired cards are visually reversed from the intended Arabic physical order.
- therefore the current off-screen coordinate fixture is not authoritative for the real rendered composition.

The exact exported DOCX inspected after V26 has:
- paragraph w:bidi present
- run w:rtl present
- table w:bidiVisual present
- section w:bidi present

but also:
- no `word/settings.xml`
- no `w:themeFontLang w:bidi="ar-SA"`
- Word opens it in Compatibility Mode
- Arabic paragraphs render from the LEFT in real Microsoft Word

## Root-cause hypothesis to verify, not blindly assume

WPF:
- Window/parent layout uses inherited `FlowDirection=RightToLeft`.
- report code also manually swaps Grid columns / uses explicit Right alignment.
- WPF FlowDirection affects non-text layout and Grid interpretation, so applying inherited RTL plus manual physical swapping can double-mirror geometry.

Microsoft reference:
- https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/bidirectional-features-in-wpf-overview
- https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/use-automatic-layout-overview

Required design direction:
- physical geometry shell must use an explicit stable coordinate system
- content/text direction may independently be RTL
- do not rely on inherited RTL plus manual physical swap simultaneously

Preferred pattern for report area:
- physical shell / Grid geometry = explicit LeftToRight
- Arabic content within cards/buttons = RightToLeft
- nav group physically anchored right in Arabic
- first logical Arabic card physically right
- English remains physical left + content LTR
- validate real arranged coordinates on the exact production visual tree, not a simplified synthetic composition

Word:
- investigate the exact OpenXML semantics used by Microsoft Word, not validator-only correctness.
- add DocumentSettingsPart / `word/settings.xml`
- set `ThemeFontLanguages.Bidi = "ar-SA"`
- add modern Word compatibility mode
- use a logical RTL-safe paragraph justification that real Word renders at the right/start edge
- retain paragraph bidi, run rtl, section bidi, table bidiVisual
- preserve Latin technical tokens such as API in logical order

Useful Microsoft / OpenXML references:
- https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.wordprocessing.themefontlanguages
- https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.wordprocessing.justificationvalues
- https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.wordprocessing.bidi
- https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.wordprocessing.righttolefttext

## Mandatory execution method

1. Unpack the V26 source/proof ZIP and inspect actual source.
2. Inspect other product UI patterns in available evidence/source that already render Arabic correctly and reuse their layout principle where relevant.
3. Research the exact WPF and OpenXML behavior before editing.
4. Make the smallest product-source delta possible.
5. Do NOT reuse or trust any prior V27 attempt blindly. Derive your own patch from V26 authority.
6. Add/upgrade tests so they validate the exact production tree and serialized DOCX package, not proxy conditions.
7. Build and publish on Windows.
8. Run:
   - structural/event checks
   - authoritative RTL layout QA
   - v13.8.9 QA
   - v13.8 matrix
   - self-test
   - OpenXmlValidator
9. Generate Arabic and English DOCX fixtures and inspect serialized XML.
10. If feasible in the environment, render/capture the actual WPF report visual tree. If not feasible, clearly mark that owner visual verification remains required.
11. Produce a complete source/proof package and a final closure report.

## Required UI assertions

For Arabic:
- report nav group physical right edge aligns with report content right edge
- three nav controls are fully visible, non-overlapping, complete boxes
- first logical report card physically occupies the right column
- second logical report card physically occupies the left column
- report content stretches to available width
- card text/headings are RTL/right-start
- no inherited parent FlowDirection may silently re-mirror explicitly assigned physical columns

For English:
- nav group physically left
- first logical card physically left
- content LTR
- no regression

## Required Word assertions

Arabic output must prove in serialized package:
- `word/settings.xml` exists
- `w:themeFontLang w:bidi="ar-SA"` exists
- explicit modern compatibility setting exists
- normal Arabic body paragraphs carry `w:bidi`
- Arabic runs carry `w:rtl`
- RTL tables carry `w:bidiVisual`
- section carries `w:bidi`
- paragraph justification uses a real-Word-safe RTL logical start/right-edge behavior
- title/header centering remains intentional where designed
- Latin tokens remain logical, not reversed
- OpenXmlValidator blocking errors = 0

## Acceptance gates

Do not claim final success from unit/static QA alone.

Your result must clearly separate:
- CODE / BUILD PASS
- SYNTHETIC / AUTOMATED LAYOUT PASS
- REAL OWNER VISUAL ACCEPTANCE = still pending unless you can genuinely render the same WPF/Word environment

Do not emit `READY_FOR_CODEX_TASTE_PASS`.

## Deliverables

Commit all work to this branch.

Return:
1. exact changed files and hashes
2. root cause confirmed/rejected
3. build/publish results
4. QA matrix
5. WPF physical coordinate evidence
6. DOCX serialized XML evidence
7. exact generated Arabic/English DOCX fixtures
8. any screenshots/render evidence available
9. one final ZIP/source package
10. final marker:
   - `V27_READY_FOR_OWNER_VISUAL_SMOKE` only if all non-owner gates pass
   - otherwise `V27_BLOCKED_WITH_EXACT_EVIDENCE`

## Hard constraints

- Aims = NO TOUCH
- no Taste Pass
- no feature removal
- no unrelated redesign
- no storage/data-contract changes
- no AI model/runtime changes
- no recording/transcription/speaker changes
- preserve owner data
