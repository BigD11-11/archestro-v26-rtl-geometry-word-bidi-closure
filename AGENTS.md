# AGENTS.md

## Project rules

- Installed authority before this branch is V26 target technical PASS.
- This branch is a bounded V27 corrective task for owner-visible Arabic RTL geometry and Microsoft Word RTL rendering only.
- Aims is out of scope: NO TOUCH.
- Taste Pass is locked.
- Do not remove features or change unrelated product behavior.
- Treat owner screenshots / real Microsoft Word behavior as higher authority than synthetic layout assumptions.
- Preserve the five V26 baseline file hashes until intentionally changed by this task.
- Prefer minimal deltas over architectural rewrites.
- Build and test on Windows before declaring readiness.
- Any result must distinguish automated PASS from owner visual acceptance.

## Code Review Rules

### RTL geometry
- Flag any design that combines inherited RightToLeft layout mirroring with manual physical column swapping without an explicit stable physical coordinate shell.
- Physical card/nav placement must be proven using arranged coordinates from the actual production visual tree.

### Word RTL
- Flag Arabic DOCX output that lacks DocumentSettingsPart/themeFontLang bidi metadata or relies only on right justification without true paragraph/run/section/table BiDi semantics.
- Real Microsoft Word rendering remains the acceptance target; validator-only success is insufficient.

### Scope
- Flag changes to recording, transcription, speaker, AI runtime/model, storage contracts, unrelated navigation, or Aims.
