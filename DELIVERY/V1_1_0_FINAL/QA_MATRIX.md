# V1.1.0 QA Matrix

| Area | Result | Verification |
|---|---|---|
| Release | PASS | Release build; win-x64 self-contained publish; 1.1.0.0 file version; 1.1.0+V29 product version |
| Startup | PASS | First-run WPF window visible and startup-ready after moving WASAPI metering off the dispatcher |
| System QA | PASS | Native WASAPI microphone and loopback; recording lifecycle; lossless/M4A; native-whisper bridge/model/dependencies; SQLite |
| Local AI | PASS | Actual Qwen3-4B-Q4_K_M report generation, 6 items, LOCAL_ZERO, 2 ledger rows, cache reopen no additional row |
| Cost | PASS | Provider token parsing, immutable pricing/FX snapshot accounting, dashboard periods and provider/model dimensions, Local zero cost |
| Word | PASS | Arabic 56 paragraphs, 55 RTL runs, 5/5 RTL tables, start/center image alignment, caption direction; English LTR; 0 validator errors |
| Word screenshots | PASS | Live Microsoft Word window capture, account region masked, Arabic and English pages visually reviewed |
| Clean install | PASS | Exact final ZIP extracted and installed; empty library; Local default; model/runtime present; DB integrity ok; FK 0 |
| Existing install | PASS | Updated in place; both shortcuts point to final executable; Start Menu launch and Settings persistence verified |
| Data preservation | PASS | Meetings/categories/marks semantically identical to pre-upgrade backup; 13/13, 10/10, 8/8; integrity ok; FK 0 |
| Package privacy | PASS | 90 ZIP entries; 0 private/owner-path matches; 0 secret-pattern matches |
| Taste Pass | PASS | Synthetic Arabic/English Word layout and clean app UI reviewed; no clipping/placement defects |
