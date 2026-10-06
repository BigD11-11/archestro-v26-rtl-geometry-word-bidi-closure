from pathlib import Path
import zipfile,hashlib,json,sys
root=Path(sys.argv[1]); target=Path(sys.argv[2])
with zipfile.ZipFile(target,'x',compression=zipfile.ZIP_STORED,allowZip64=True) as z:
    for p in sorted(x for x in root.rglob('*') if x.is_file()): z.write(p,p.relative_to(root).as_posix())
h=hashlib.sha256()
with target.open('rb') as f:
    for b in iter(lambda:f.read(8*1024*1024),b''): h.update(b)
print(json.dumps({'zip':str(target),'bytes':target.stat().st_size,'sha256':h.hexdigest(),'entries':len(zipfile.ZipFile(target).infolist())},indent=2))
