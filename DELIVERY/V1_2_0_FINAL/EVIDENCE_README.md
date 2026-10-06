# V30 evidence bundle

All evidence in this directory is synthetic or sanitized. Owner databases, settings contents, backups, raw logs, original Word window screenshots, credentials, and local profile paths are excluded.

- `FINAL_QA_MATRIX.json`: release gates and validation boundaries.
- `EVIDENCE/`: build summary, sanitized runtime QA, migration/shortcut metadata, and Word/OpenXML proof.
- `VISUALS/APP_ARABIC.png` and `APP_ENGLISH.png`: clean empty-library product screenshots.
- `VISUALS/WORD_ARABIC_PAGE.png` and `WORD_ENGLISH_PAGE.png`: cropped synthetic Word page screenshots without Office account chrome.
- `FINAL_CUSTOMER_RELEASE_REPORT.md`: release report, caveats, and package hashes.

One `native-whisper` system test failure and the passing retry are both preserved in the private working evidence. The public bundle reports the failure/retry pair without reproducing private local filesystem paths. Live cloud calls and a real Drive Desktop folder were not available in this run.
