# V28R1 Final Closeout

**Result:** `V28R1_OWNER_RUN_ROLLED_BACK_WITH_EXACT_BLOCKER`

V28R1 was built and published, staged and installed QA passed 7/7, and the same Arabic meeting completed a Local report. The Arabic report was reopened from cache and exported by the application; Microsoft Word 16.0 opened the DOCX. Its XML contains modern compatibility mode 15, `themeFontLang bidi="ar-SA"`, paragraph/run BiDi, one RTL section, and RTL tables.

## Exact blockers

1. The English Local report did not complete. Its extraction and one repair both returned malformed, truncated-looking JSON (714 characters each); the DTO count remained zero after 144.384 seconds. The existing Arabic report was not replaced. Therefore English DOCX and English live Word proof are unavailable.
2. The one-time secure in-app DeepSeek-key request received no user response. The app reported no API key saved, so Test Connection, Ask this meeting with DeepSeek, full DeepSeek report, timings, and post-test key removal could not run.
3. Ask-this-meeting responded, but the broad question did not match transcript terms and returned the no-match fallback; semantic answer quality is not proven.

## Rollback and integrity

The installed V28R1 candidate was preserved at `C:\Users\arefa\AppData\Local\Programs\Archestro\Meeting Vault.V28_FAILED_20261006`; V25 was restored and restarted with its recorded executable hash. The protected database, WAL, SHM, and preflight settings snapshot were restored before restart. Post-restart SQLite integrity is `ok`, foreign-key errors are zero, logical rows are 44, and database semantics match the preflight snapshot. V25 rewrote its settings schema on restart: the six V28 cloud-provider fields, including the encrypted-key field, are absent; there is no plaintext API token. V25 uses the local Qwen model by default.

No key, transcript, raw database, raw settings file, or meeting report text is included in this package. Aims were untouched. Taste Pass remains HOLD.

## Source and checkpoints

- Branch: `codex/v28r1-local-report-66-fix`
- HEAD: `fbede6ad2448528bbbd647f2ac69f11eff75869c`
- Candidate V28R1 executable SHA-256: `2E299E74369A329F0F3FEBA41F0993873F220355C8AE682F028F7C17BB083631`
- Restored V25 executable SHA-256: `76985810D7C9E8324BF0FCDC228277834CCEED7FC5674CEF3CB3042D9978EE81`
- Source/proof archive SHA-256: `7042C460A93A1EF1C755E87116ED9798D9CA6B2320FD4B1916C54824616AD52C`