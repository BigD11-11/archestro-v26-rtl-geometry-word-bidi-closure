import hashlib
import json
import sqlite3
import sys
import urllib.parse

source, output = sys.argv[1:3]
uri = "file:" + urllib.parse.quote(source.replace("\\", "/")) + "?mode=ro"
con = sqlite3.connect(uri, uri=True, timeout=15)
con.row_factory = sqlite3.Row
integrity = con.execute("PRAGMA integrity_check").fetchone()[0]
foreign_errors = len(con.execute("PRAGMA foreign_key_check").fetchall())
objects = con.execute("SELECT type,name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name").fetchall()
schema_hash = hashlib.sha256("\n".join(str(tuple(x)) for x in objects).encode()).hexdigest()
tables = []
shadow_suffixes = ("_data", "_idx", "_content", "_docsize", "_config", "_segments", "_segdir")
for obj in objects:
    if obj["type"] != "table":
        continue
    name = obj["name"]
    if name.endswith(shadow_suffixes):
        continue
    escaped = '"' + name.replace('"', '""') + '"'
    cols = con.execute("PRAGMA table_info(" + escaped + ")").fetchall()
    primary_key = [x["name"] for x in cols if x["pk"]]
    ordering = " ORDER BY " + ",".join('"' + x.replace('"', '""') + '"' for x in primary_key) if primary_key else " ORDER BY rowid"
    try:
        rows = con.execute("SELECT * FROM " + escaped + ordering)
    except sqlite3.OperationalError:
        rows = con.execute("SELECT * FROM " + escaped)
    digest = hashlib.sha256()
    count = 0
    for row in rows:
        def encode(value):
            if isinstance(value, bytes):
                return {"blobSha256": hashlib.sha256(value).hexdigest(), "length": len(value)}
            return value
        record = json.dumps(tuple(encode(v) for v in row), ensure_ascii=False, sort_keys=True, separators=(",", ":"), default=str).encode()
        digest.update(len(record).to_bytes(8, "big"))
        digest.update(record)
        count += 1
    tables.append({"table": name, "rows": count, "logicalSha256": digest.hexdigest()})
result = {
    "integrity": integrity,
    "foreignKeyErrors": foreign_errors,
    "schemaSha256": schema_hash,
    "logicalRows": sum(x["rows"] for x in tables),
    "tables": tables,
}
with open(output, "w", encoding="utf-8") as handle:
    json.dump(result, handle, ensure_ascii=False, indent=2)
con.close()
