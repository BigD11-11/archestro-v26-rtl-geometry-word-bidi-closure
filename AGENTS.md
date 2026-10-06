# AGENTS.md

## Product authority

- Accepted CustomerVersion 1.1.0 / V29 is immutable; do not replace or rewrite its package, report, hashes, or release history.
- Active release task: CustomerVersion 1.2.0 / InternalBuild V30, based on the accepted V29 product.
- Aims is out of scope: NO TOUCH.
- WhatsApp Business and Mobile companion are history/future scope only; do not implement them in V30.
- Preserve owner data. Back up DB/WAL/SHM/settings and verify semantic equality before and after owner migration.
- Never place private owner records, credentials, logs, or settings in GitHub evidence or customer packages.

## V30 scope

- System Status Pulse, WASAPI audio health, Drive Desktop folder detection, Archestro Inbox and canonical intake queue.
- Groq Whisper Large V3 Turbo, DeepSeek cloud intelligence, native-whisper and Local Qwen.
- Cloud Fast / Offline / Custom, local/cloud usage and immutable cost accounting, bilingual Arabic/English UX.
- Additive migration, clean offline customer package, clean install, owner upgrade, shortcuts, regression, and Taste Pass.

## Engineering and acceptance

- Keep local Offline mode the clean-install default and guarantee no cloud transfer while it is selected.
- Keep cloud keys DPAPI-protected for the current Windows user; never include secrets in logs, screenshots, commits, or packages.
- File intake watches only the configured folder, uses a stable-file guard and SHA-256 dedupe, and never deletes the source by default.
- Test on Windows with Release build and win-x64 self-contained publish. Run the named regression gates and inspect the final UI in Arabic and English.
- Do not declare release PASS until installation, owner data, shortcuts, clean package, privacy scan, regression, and Taste Pass all have evidence.
