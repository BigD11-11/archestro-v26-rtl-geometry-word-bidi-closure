from pathlib import Path
from shutil import copy2
import shutil
src=Path(r'V:\ARCHSTRO_CUSTOMER_RELEASES\ARCHSTRO_MEETING_VAULT_1_1_0_20261006')
dst=Path(r'V:\ARCHSTRO_CUSTOMER_RELEASES\V29_CANDIDATE_V1_1_0_20261006')
if dst.exists(): raise SystemExit(f'Candidate already exists: {dst}')
dst.mkdir(parents=True)
count=0; total=0; omitted=[]
for p in src.rglob('*'):
    rel=p.relative_to(src)
    if p.is_symlink(): raise SystemExit(f'Symlink refused: {rel}')
    if p.is_dir(): continue
    if rel.as_posix().lower()=='app/speakermodels/0-four-speakers-zh.wav':
        omitted.append(rel.as_posix()); continue
    out=dst/rel; out.parent.mkdir(parents=True,exist_ok=True); copy2(p,out); count+=1; total+=p.stat().st_size
print({'copiedFiles':count,'bytes':total,'omittedDemoAssets':omitted,'candidate':str(dst)})
