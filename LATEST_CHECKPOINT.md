# Latest checkpoint — V30 / Customer 1.2.0

Date: 2026-10-06

Source commit: `17f159d627df18c38b60f83cdf79d0a8b17f34d9`

Checkpoint: implementation, Release publish, clean offline package, owner upgrade, and final QA evidence complete.

The final installed executable reports `1.2.0+V30.17f159d627df18c38b60f83cdf79d0a8b17f34d9` and SHA-256 `720F6E4C841F76B772A556A65CED3E3AE2B003D1F18C5E6C66F27517E2E0202B`. Desktop and Start Menu shortcuts target the installed executable. Both launch paths were exercised; the final owner process was left running after launching from the Desktop shortcut.

Business data audit: SQLite integrity `ok`, foreign-key errors `0`; 13 meetings, 10 categories, 8 important marks, and 15 FTS rows have unchanged semantic hashes. Settings are Local / Offline, cloud disabled, and no encrypted cloud keys are saved.

Detailed evidence: `DELIVERY/V1_2_0_FINAL/`. Known limitations are explicitly captured in the report: no live cloud credentials, no physical Google Drive root on this machine, one transient `native-whisper` QA failure before a passing retry, and a low-storage device warning.
