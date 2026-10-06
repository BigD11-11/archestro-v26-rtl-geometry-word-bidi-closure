from __future__ import annotations

import hashlib
import json
import re
import sys
import zipfile
from pathlib import Path


repo = Path(sys.argv[1]).resolve()
full_source = Path(sys.argv[2]).resolve()
customer_zip = Path(sys.argv[3]).resolve()
out_dir = repo / "DELIVERY" / "V1_1_0_FINAL"
visual = repo / "DELIVERY" / "V1_1_0_VISUAL_QA"
source_zip = out_dir / "V1_1_0_SOURCE_AND_PROOF.zip"
result_zip = out_dir / "V1_1_0_RESULT.zip"

blocked_parts = {".git", ".vs", "bin", "obj", "_runtime", "node_modules"}
allowed_source_exts = {
    ".cs", ".xaml", ".csproj", ".sln", ".json", ".md", ".resx",
    ".png", ".ico", ".config", ".props", ".targets", ".xml", ".ps1",
}
private_pattern = re.compile(rb"sk-[a-f0-9]{32,}", re.IGNORECASE)


def safe_source_files() -> list[Path]:
    files = []
    for path in full_source.rglob("*"):
        if not path.is_file():
            continue
        rel = path.relative_to(full_source)
        if any(part.lower() in blocked_parts for part in rel.parts):
            continue
        if "models" in {p.lower() for p in rel.parts} or "speakermodels" in {p.lower() for p in rel.parts}:
            continue
        if path.suffix.lower() not in allowed_source_exts:
            continue
        if path.suffix.lower() in {".cs", ".xaml", ".csproj", ".sln", ".json", ".md", ".config", ".xml", ".ps1"}:
            if private_pattern.search(path.read_bytes()):
                raise RuntimeError(f"Secret pattern found in source file: {rel.as_posix()}")
        files.append(path)
    return sorted(files)


def add_tree(archive: zipfile.ZipFile, root: Path, prefix: str, files: list[Path]) -> None:
    for path in files:
        rel = path.relative_to(root).as_posix()
        archive.write(path, f"{prefix}/{rel}")


def add_path(archive: zipfile.ZipFile, path: Path, name: str) -> None:
    archive.write(path, name)


source_files = safe_source_files()
evidence_files = sorted(p for p in visual.iterdir() if p.is_file())
release_docs = [
    repo / "VERSION.md",
    repo / "RELEASE_NOTES.md",
    repo / "COST_PRICING_SNAPSHOT.md",
    repo / "DELIVERY" / "V1_1_0_FINAL" / "FINAL_CLOSEOUT_REPORT.md",
    repo / "DELIVERY" / "V1_1_0_FINAL" / "QA_MATRIX.md",
    repo / "DELIVERY" / "V1_1_0_FINAL" / "package_closeout.py",
]
for item in release_docs:
    if not item.is_file():
        raise FileNotFoundError(item)

with zipfile.ZipFile(source_zip, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
    add_tree(archive, full_source, "source/Archestro.MeetingVault", source_files)
    add_tree(archive, repo / "DELIVERY", "DELIVERY", [p for p in evidence_files if p.name != "DATA_INTEGRITY_PROOF_CURRENT.json"])
    for path in release_docs:
        add_path(archive, path, path.relative_to(repo).as_posix())
    package_manifest = customer_zip.parent / "PACKAGE_MANIFEST.json"
    if package_manifest.is_file():
        add_path(archive, package_manifest, "customer-package/PACKAGE_MANIFEST.json")

result_names = [
    "FINAL_CLOSEOUT_REPORT.md", "QA_MATRIX.md", "package_closeout.py",
]
result_evidence = [
    "Arabic_Synthetic_Report.docx", "English_Synthetic_Report.docx",
    "WORD_AR_PAGE_1.png", "WORD_AR_PAGE_2.png", "WORD_EN_PAGE_1.png", "WORD_EN_PAGE_2.png",
    "WORD_CAPTURE_MANIFEST.json", "OPENXML_VISUAL_PROOF.json", "VISUAL_QA_REPORT.md",
    "LOCAL_AI_COST_QA.json", "SYSTEM_QA_NATIVE_AUDIO_PROOF.json",
    "CLEAN_INSTALL_UI_PROOF.json", "OWNER_INSTALLED_UI_PROOF.json",
    "DATA_INTEGRITY_PROOF.json", "PRIVACY_SCAN_V1_0_0.json", "PRIVACY_SCAN_V1_0_0.md",
    "PRIVACY_SCAN_V1_1_0.json",
]
with zipfile.ZipFile(result_zip, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
    for name in result_names:
        add_path(archive, out_dir / name, f"V1_1_0_FINAL/{name}")
    for name in result_evidence:
        add_path(archive, visual / name, f"V1_1_0_VISUAL_QA/{name}")
    for name in ("VERSION.md", "RELEASE_NOTES.md", "COST_PRICING_SNAPSHOT.md"):
        add_path(archive, repo / name, name)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


print(json.dumps({
    "sourceFiles": len(source_files),
    "sourceBytes": sum(p.stat().st_size for p in source_files),
    "sourceProofZip": {"path": source_zip.name, "bytes": source_zip.stat().st_size, "sha256": sha256(source_zip)},
    "resultZip": {"path": result_zip.name, "bytes": result_zip.stat().st_size, "sha256": sha256(result_zip)},
}, indent=2))
