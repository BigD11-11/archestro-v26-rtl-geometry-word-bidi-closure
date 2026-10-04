# Archestro Meeting Vault — V26 Runtime Recovery Owner-Run

Current installed authority before this run: **V25 PASS**.

This package changes only the owner-run wrapper/runtime layer. The exact five V26 product patch files are preserved byte-for-byte from the frozen Codex V26 source/proof authority.

## Why this recovery package exists
The first V26 owner attempt stopped before any mutation because the previous `START_HERE.cmd` depended on external `py/python` from PATH. The launcher also still showed a stale V15 title.

This recovery package removes that external dependency by carrying an application-private official CPython embeddable runtime and launching it by an exact package-relative path.

## Runtime
- CPython 3.13.15 Windows x64 embeddable distribution
- Source: python.org official embeddable package
- Expected archive SHA-256: `D1F04D990AEE1253D8569E8E5104E30FA9F5FA830899F14843448872D936A2CF`
- Expected archive size: `11,009,825` bytes
- No Python installation and no PATH change are required.

## Owner run
1. Close Archestro Meeting Vault.
2. Extract this ZIP completely.
3. Run `START_HERE.cmd`.
4. Wait for completion.
5. Upload only `Result\RESULT_TO_UPLOAD.zip`.

The runner preserves the proven 13-stage flow: preflight, current-baseline diagnostics, user-data safepoint, isolated source staging, structural QA, build/publish, staged runtime QA, canonical apply, install apply, installed QA, startup probe, post-data integrity and final rollback/result handling.

## Authority boundary
- V25 remains installed authority until this package returns target PASS.
- V26 product patch bytes are unchanged.
- `--rtl-layout-qa` remains an authoritative staged + installed gate.
- Aims = NO TOUCH.
- Taste Pass remains HOLD until V26 target PASS plus Mohammed visual acceptance.