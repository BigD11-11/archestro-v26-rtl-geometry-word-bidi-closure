#!/usr/bin/env python3
"""Privacy scan for a customer ZIP; emits only match counts, never matched text."""
import json
import re
import sys
import zipfile

package = sys.argv[1]
forbidden_path = re.compile(
    r"(^|/)(meeting-vault\.db(?:-wal|-shm)?|settings\.json|logs?|reports?|transcripts?|recordings?|cache|cost-history)(/|$)|\.(wav|m4a|mp3|flac|aac)$",
    re.IGNORECASE,
)
owner_path = re.compile(r"[A-Z]:\\Users\\[^\\]+\\(Documents|AppData|Desktop)\\", re.IGNORECASE)
secret = re.compile(r"sk-[a-f0-9]{32,}|(?i:api[_ -]?key)\s*[:=]\s*[A-Za-z0-9_./+=-]{20,}")
text_exts = {".md", ".txt", ".json", ".xml", ".config", ".ps1", ".cmd", ".bat", ".cs", ".yml", ".yaml"}
result = {"package": package.rsplit("/", 1)[-1].rsplit("\\", 1)[-1], "entryCount": 0,
          "forbiddenPathMatches": 0, "ownerPathMatches": 0, "secretPatternMatches": 0,
          "matchedEntryNames": [], "pass": False}
with zipfile.ZipFile(package) as archive:
    for entry in archive.infolist():
        name = entry.filename.replace("\\", "/")
        if entry.is_dir():
            continue
        result["entryCount"] += 1
        matched = False
        if forbidden_path.search(name):
            result["forbiddenPathMatches"] += 1
            matched = True
        if owner_path.search(name):
            result["ownerPathMatches"] += 1
            matched = True
        if matched:
            result["matchedEntryNames"].append(name)
        ext = "." + name.rsplit(".", 1)[-1].lower() if "." in name else ""
        if ext in text_exts and entry.file_size <= 2_000_000:
            data = archive.read(entry)
            result["ownerPathMatches"] += len(owner_path.findall(data.decode("utf-8", errors="ignore")))
            result["secretPatternMatches"] += len(secret.findall(data.decode("utf-8", errors="ignore")))
result["pass"] = result["forbiddenPathMatches"] == 0 and result["ownerPathMatches"] == 0 and result["secretPatternMatches"] == 0
print(json.dumps(result, indent=2))
