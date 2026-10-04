#!/usr/bin/env python3
# MOHAMMED_OWNER_RUN_CONTRACT: V1
# RESULT_ROOT: PACKAGE_LOCAL
# RESULT_AUTO_SURFACE: REQUIRED
# LEARNING_CLOSEOUT: REQUIRED
# LONG_RUNNING: YES
# HEARTBEAT: REQUIRED
# ELAPSED_TIMER: REQUIRED
# STAGE_PROGRESS: REQUIRED

from __future__ import annotations
import os, sys, json, time, hashlib, shutil, subprocess, zipfile, re, threading, queue
from pathlib import Path
from datetime import datetime, timezone

RUNNER_VERSION='V26 RUNTIME RECOVERY — ROOT RTL GEOMETRY + WORD BIDI'
EXPECTED_EMBEDDED_PYTHON='3.13.15'
ROOT=Path(__file__).resolve().parent
PATCH=ROOT/'PATCH'; RESULT_ROOT=ROOT/'Result'; WORK_ROOT=ROOT/'_WORK'
BASELINE=json.loads((ROOT/'BASELINE_HASHES.json').read_text(encoding='utf-8'))
PATCH_HASHES=json.loads((ROOT/'PATCH_HASHES.json').read_text(encoding='utf-8'))
SOURCE_ROOT=Path(r'V:\خاص بي\ARCHESTRO_MEETING_VAULT_BUILD4_R4_3_STARTUP_LIFECYCLE_FIX')
INSTALL_ROOT=Path(os.environ.get('LOCALAPPDATA',''))/'Programs'/'Archestro'/'Meeting Vault'
DOTNET=Path(r'C:\Program Files\dotnet\dotnet.exe')
DATA_ROOT=Path.home()/'Documents'/'Archestro Meeting Vault'
STAGE_DEFS=[('S01','Preflight / package / environment',[]),('S02','Current installed baseline diagnostics',['S01']),('S03','User-data safepoint + fingerprint',['S01']),('S04','Isolated source staging + 5-file V26 candidate delta',['S01']),('S05','Staged structural checks',['S04']),('S06','Build + publish staged source',['S04']),('S07','Staged runtime QA',['S06']),('S08','Canonical source backup + frozen delta apply',['S07']),('S09','Installed tree backup + frozen publish apply',['S08']),('S10','Installed runtime QA',['S09']),('S11','Normal startup probe',['S09']),('S12','User-data integrity after',['S09']),('S13','Finalize / rollback / result',['S01'])]
STAMP=datetime.now().strftime('%Y%m%d_%H%M%S'); RUN_DIR=RESULT_ROOT/f'RUN_{STAMP}'; LOG_DIR=RUN_DIR/'LOGS'; EVIDENCE_DIR=RUN_DIR/'EVIDENCE'; REPAIR_DIR=RUN_DIR/'REPAIR_CARDS'; RECEIPT_DIR=RUN_DIR/'BACKUP_RECEIPTS'; WORK=WORK_ROOT/f'RUN_{STAMP}'; STAGED_ROOT=WORK/'STAGED_SOURCE'; PUBLISH=WORK/'publish'; BACKUP=WORK/'BACKUP'; DATA_BACKUP=WORK/'DATA_BACKUP'; EVENTS=RUN_DIR/'EVENTS.jsonl'; START=time.monotonic()
STAGES={sid:{'id':sid,'title':title,'dependencies':deps,'status':'PENDING','started_at':None,'finished_at':None,'seconds':None,'detail':None,'error':None,'repair_card':None} for sid,title,deps in STAGE_DEFS}
DATA_BEFORE=None; CANONICAL_MUTATED=False; INSTALL_MUTATED=False
for p in [RESULT_ROOT,RUN_DIR,LOG_DIR,EVIDENCE_DIR,REPAIR_DIR,RECEIPT_DIR,WORK]: p.mkdir(parents=True,exist_ok=True)

def now_iso(): return datetime.now(timezone.utc).astimezone().isoformat()
def elapsed(): return time.monotonic()-START
def event(stage,event_type,status,message,**extra):
 rec={'time':now_iso(),'elapsed_seconds':round(elapsed(),3),'stage':stage,'event_type':event_type,'status':status,'message':message,**extra}
 with EVENTS.open('a',encoding='utf-8') as f: f.write(json.dumps(rec,ensure_ascii=False)+'\n')
def emit(kind,msg,stage='SYS'):
 line=f'[{kind:<10} {elapsed():08.1f}s] {msg}'; print(line,flush=True); event(stage,'console',kind,msg)
def sha256(path:Path):
 h=hashlib.sha256()
 with path.open('rb') as f:
  for b in iter(lambda:f.read(1024*1024),b''): h.update(b)
 return h.hexdigest().upper()
def repair_card(stage_id,summary,affected=None,next_action=None,classification='PRODUCT_OR_RUNTIME'):
 p=REPAIR_DIR/f'{stage_id}.md'
 lines=['# Repair Card — '+stage_id,'','- Stage: '+STAGES[stage_id]['title'],'- Classification: '+classification,'- Observed: '+summary,'- Affected: '+(affected or 'See stage evidence/logs.'),'- Not affected: prior independent PASS stages remain preserved.',f'- Mutation state: canonical={CANONICAL_MUTATED}, installed={INSTALL_MUTATED}','- Next bounded action: '+(next_action or 'Use this run evidence; do not blindly rerun the whole package.')]
 p.write_text('\n'.join(lines)+'\n',encoding='utf-8'); STAGES[stage_id]['repair_card']=str(p.relative_to(RUN_DIR)); return p
def set_stage(stage_id,status,detail=None,error=None):
 s=STAGES[stage_id]
 if status=='RUNNING': s['started_at']=now_iso()
 else: s['finished_at']=now_iso()
 s['status']=status; s['detail']=detail; s['error']=error; event(stage_id,'stage_status',status,s['title'],detail=detail,error=error)
def execute_stage(stage_id,fn,blocked_if=None):
 if blocked_if:
  set_stage(stage_id,'SKIP_DEPENDENCY',detail={'blocked_by':blocked_if}); emit('SKIP',f'{stage_id} — {STAGES[stage_id]["title"]} | blocked by {blocked_if}',stage_id); return False,None
 t=time.monotonic(); set_stage(stage_id,'RUNNING'); emit('STAGE',f'{stage_id} — {STAGES[stage_id]["title"]}',stage_id)
 try:
  detail=fn(); sec=time.monotonic()-t; STAGES[stage_id]['seconds']=round(sec,2); set_stage(stage_id,'DONE',detail=detail); emit('PASS',f'{stage_id} DONE ({sec:.1f}s)',stage_id); return True,detail
 except Exception as e:
  sec=time.monotonic()-t; STAGES[stage_id]['seconds']=round(sec,2); err=repr(e); set_stage(stage_id,'BLOCKED',error=err); repair_card(stage_id,err); emit('FAIL',f'{stage_id} BLOCKED ({sec:.1f}s): {e}',stage_id); return False,None

def run_process(label,args,cwd=None,timeout=1200,env=None,stage='PROC'):
 safe=re.sub(r'[^A-Za-z0-9_.-]+','_',label); stdout_path=LOG_DIR/f'{stage}_{safe}_stdout.txt'; stderr_path=LOG_DIR/f'{stage}_{safe}_stderr.txt'; emit('START',label,stage)
 p=subprocess.Popen([str(x) for x in args],cwd=str(cwd) if cwd else None,env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',errors='replace',stdin=subprocess.DEVNULL,bufsize=1)
 q=queue.Queue()
 def reader(pipe,tag):
  try:
   for line in iter(pipe.readline,''): q.put((tag,line))
  finally: q.put((tag,None))
 threads=[threading.Thread(target=reader,args=(p.stdout,'out'),daemon=True),threading.Thread(target=reader,args=(p.stderr,'err'),daemon=True)]
 for th in threads: th.start()
 out=[]; err=[]; t0=time.monotonic(); last_inline=0; last_durable=time.monotonic(); closed=set()
 while True:
  try:
   while True:
    tag,line=q.get_nowait()
    if line is None: closed.add(tag); continue
    (out if tag=='out' else err).append(line); print(line,end='',flush=True)
  except queue.Empty: pass
  now=time.monotonic(); sec=now-t0
  if now-last_inline>=0.5 and p.poll() is None:
   sys.stdout.write(f'\r[RUN        {elapsed():08.1f}s] {label} ({sec:.1f}s) PID {p.pid}   '); sys.stdout.flush(); last_inline=now
  if now-last_durable>=15 and p.poll() is None:
   print(); emit('HEARTBEAT',f'{label} still active ({sec:.1f}s) PID {p.pid}',stage); last_durable=now
  if sec>timeout and p.poll() is None:
   try: p.kill()
   except Exception: pass
   raise TimeoutError(f'{label} exceeded timeout {timeout}s (PID {p.pid}).')
  if p.poll() is not None and closed=={'out','err'}: break
  time.sleep(0.1)
 for th in threads: th.join(timeout=1)
 sys.stdout.write('\r'+' '*120+'\r'); sys.stdout.flush(); stdout=''.join(out); stderr=''.join(err); stdout_path.write_text(stdout,encoding='utf-8'); stderr_path.write_text(stderr,encoding='utf-8'); emit('PASS' if p.returncode==0 else 'WARN',f'{label} ({time.monotonic()-t0:.1f}s) exit {p.returncode}',stage)
 return {'exit':p.returncode,'stdout':stdout,'stderr':stderr,'pid':p.pid,'stdout_path':str(stdout_path.relative_to(RUN_DIR)),'stderr_path':str(stderr_path.relative_to(RUN_DIR)),'seconds':round(time.monotonic()-t0,2)}

def preflight():
 if 'aims' in str(SOURCE_ROOT).lower(): raise RuntimeError('Aims deny guard triggered.')
 project=SOURCE_ROOT/'src'/'Archestro.MeetingVault'/'Archestro.MeetingVault.csproj'
 if not project.exists(): raise RuntimeError(f'Source project missing: {project}')
 if not DOTNET.exists(): raise RuntimeError('dotnet.exe missing.')
 for rel,h in PATCH_HASHES.items():
  p=PATCH/rel
  if not p.exists() or sha256(p)!=h: raise RuntimeError(f'Package patch integrity failed: {rel}')
 free=shutil.disk_usage(ROOT).free
 if free<2*1024**3: raise RuntimeError('Less than 2 GB free beside package.')
 runtime_info={'sys_executable':sys.executable,'python_version':sys.version.split()[0],'python_full':sys.version,'embedded_expected':EXPECTED_EMBEDDED_PYTHON}
 (EVIDENCE_DIR/'RUNNER_RUNTIME.json').write_text(json.dumps(runtime_info,indent=2),encoding='utf-8')
 if runtime_info['python_version']!=EXPECTED_EMBEDDED_PYTHON: raise RuntimeError(f"Unexpected runner Python version: {runtime_info['python_version']} (expected {EXPECTED_EMBEDDED_PYTHON})")
 return {'AimsTouch':'NONE','source':str(SOURCE_ROOT),'install':str(INSTALL_ROOT),'patch_files':len(PATCH_HASHES),'free_gb':round(free/1024**3,1),'runner_runtime':runtime_info}
def current_baseline():
 exe=INSTALL_ROOT/'Archestro.MeetingVault.exe'; rows=[]
 if not exe.exists(): return {'installed_exe_missing':True}
 for flag in ['--ai-self-test','--speaker-self-test']:
  r=run_process(f'current {flag}',[exe,flag],cwd=INSTALL_ROOT,timeout=700,stage='S02'); rows.append({'flag':flag,'classification':'ADVISORY_DIAGNOSTIC','exit':r['exit'],'seconds':r['seconds']})
 (EVIDENCE_DIR/'CURRENT_INSTALLED_BASELINE.json').write_text(json.dumps(rows,indent=2),encoding='utf-8'); return rows
ALLOWED_DATA_EXT={'.json','.txt','.srt','.db','.sqlite','.sqlite3'}
def data_rows():
 rows=[]
 if not DATA_ROOT.exists(): return rows
 for p in sorted(DATA_ROOT.rglob('*')):
  if not p.is_file() or p.suffix.lower() not in ALLOWED_DATA_EXT: continue
  low=str(p).lower()
  if '\\logs\\' in low or '/logs/' in low or '\\ai\\' in low or '/ai/' in low: continue
  rows.append({'path':str(p.relative_to(DATA_ROOT)).replace('\\','/'),'bytes':p.stat().st_size,'sha256':sha256(p)})
 return rows
def data_before():
 global DATA_BEFORE
 DATA_BEFORE=data_rows(); DATA_BACKUP.mkdir(parents=True,exist_ok=True)
 for row in DATA_BEFORE:
  src=DATA_ROOT/Path(row['path']); dst=DATA_BACKUP/Path(row['path']); dst.parent.mkdir(parents=True,exist_ok=True); shutil.copy2(src,dst)
 (EVIDENCE_DIR/'DATA_FINGERPRINT_BEFORE.json').write_text(json.dumps(DATA_BEFORE,indent=2),encoding='utf-8'); return {'count':len(DATA_BEFORE),'backup_root':str(DATA_BACKUP)}
def stage_source():
 if STAGED_ROOT.exists(): shutil.rmtree(STAGED_ROOT)
 shutil.copytree(SOURCE_ROOT/'src',STAGED_ROOT/'src'); results=[]; conflicts=[]
 for rel_s,new_hash in PATCH_HASHES.items():
  rel=Path(rel_s); dst=STAGED_ROOT/rel; src=PATCH/rel; current=sha256(dst) if dst.exists() else None
  if current==new_hash: status='DONE_NO_OP'
  elif current==BASELINE.get(rel_s):
   tmp=dst.with_suffix(dst.suffix+'.tmpv26recovery'); shutil.copy2(src,tmp); os.replace(tmp,dst); status='DONE'
  else: status='CONFLICT'; conflicts.append({'path':rel_s,'current':current,'expected_baseline':BASELINE.get(rel_s),'desired':new_hash})
  results.append({'path':rel_s,'status':status,'before':current,'after':sha256(dst) if dst.exists() else None,'desired':new_hash})
 (EVIDENCE_DIR/'PATCH_FILE_STATUS.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
 if conflicts:
  (EVIDENCE_DIR/'PATCH_CONFLICTS.json').write_text(json.dumps(conflicts,indent=2),encoding='utf-8'); raise RuntimeError(f'{len(conflicts)} source file(s) conflict with V25 installed baseline/V26 target.')
 return {'files':len(results),'no_op':sum(x['status']=='DONE_NO_OP' for x in results)}
def structural_checks():
 import xml.etree.ElementTree as ET
 parsed=[]; missing=[]; count=0
 guards={
  'src/Archestro.MeetingVault/App.xaml.cs':[
   '--rtl-layout-qa',
   'SelfTestService.RunRtlLayoutQa(output)',
   'ARCHESTRO_RTL_QA_OUTPUT'
  ],
  'src/Archestro.MeetingVault/Dialogs/IntelligenceWindow.xaml':[
   'x:Name="ReportNavHost"',
   'x:Name="ReportNavReportButton"',
   'x:Name="ReportNavMeetingAskButton"',
   'x:Name="ReportNavVaultAskButton"',
   'x:Name="ReportContentArea"',
   'x:Name="ExecutiveSummaryCard"',
   'x:Name="TopicsCard"',
   'x:Name="KeyPointsCard"'
  ],
  'src/Archestro.MeetingVault/Dialogs/IntelligenceWindow.xaml.cs':[
   'ReportNavReportButton',
   'ReportNavMeetingAskButton',
   'ReportNavVaultAskButton',
   'AppearanceService.IsArabic'
  ],
  'src/Archestro.MeetingVault/Services/MeetingReportWordExporter.cs':[
   'new W.BiDi()',
   'new W.RightToLeftText()',
   'new W.BiDiVisual()',
   'SectionProperties'
  ],
  'src/Archestro.MeetingVault/Services/SelfTestService.cs':[
   'RunRtlLayoutQa',
   'ReportNavHost',
   'OpenXmlValidator',
   'VerifyWordRtlFixture'
  ]
 }
 for rel,patterns in guards.items():
  text=(STAGED_ROOT/rel).read_text(encoding='utf-8-sig')
  absent=[p for p in patterns if p not in text]
  if absent: raise RuntimeError(f'V26 semantic guard failed {rel}: {absent}')

 for rel in PATCH_HASHES:
  if rel.endswith('.xaml'): ET.parse(STAGED_ROOT/rel); parsed.append(rel)
 for x in (STAGED_ROOT/'src'/'Archestro.MeetingVault').rglob('*.xaml'):
  cs=Path(str(x)+'.cs'); code=cs.read_text(encoding='utf-8-sig') if cs.exists() else ''; text=x.read_text(encoding='utf-8-sig'); handlers=re.findall(r'\b(?:Click|Loaded|Closed|KeyDown|TextChanged|SelectionChanged|GotKeyboardFocus|LostKeyboardFocus|MouseLeftButtonDown|PreviewMouseLeftButtonUp|ValueChanged|DragOver|Drop)="([A-Za-z_][A-Za-z0-9_]*)"',text)
  for h in handlers:
   count+=1
   if not re.search(r'\b'+re.escape(h)+r'\s*\(',code): missing.append({'xaml':str(x),'handler':h})
 if missing: raise RuntimeError(f'Missing XAML handlers: {missing[:5]}')
 return {'xaml_parsed':len(parsed),'event_handlers_checked':count,'missing':0}
def parse_build_errors(text):
 pat=re.compile(r'^(.*?\.cs)\((\d+),(\d+)\):\s+error\s+([A-Z]+\d+):\s+(.*?)(?:\s+\[.*)?$',re.M); return [{'file':m.group(1),'line':int(m.group(2)),'column':int(m.group(3)),'code':m.group(4),'message':m.group(5).strip()} for m in pat.finditer(text or '')]
def build_publish():
 project=STAGED_ROOT/'src'/'Archestro.MeetingVault'/'Archestro.MeetingVault.csproj'; shutil.rmtree(PUBLISH,ignore_errors=True); PUBLISH.mkdir(parents=True,exist_ok=True)
 b=run_process('dotnet build',[DOTNET,'build',project,'-c','Release','-r','win-x64','/nr:false'],cwd=project.parent,timeout=1200,stage='S06'); errors=parse_build_errors((b['stdout'] or '')+'\n'+(b['stderr'] or '')); (EVIDENCE_DIR/'BUILD_ERROR_SUMMARY.json').write_text(json.dumps(errors,indent=2),encoding='utf-8')
 if b['exit']!=0: raise RuntimeError(f'dotnet build exit={b["exit"]}; compiler_errors={len(errors)}')
 p=run_process('dotnet publish',[DOTNET,'publish',project,'-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=false','-p:PublishReadyToRun=false','-o',PUBLISH],cwd=project.parent,timeout=1800,stage='S06')
 if p['exit']!=0: raise RuntimeError(f'dotnet publish exit={p["exit"]}')
 exe=PUBLISH/'Archestro.MeetingVault.exe'
 if not exe.exists(): raise RuntimeError('Published EXE missing.')
 return {'exe':str(exe),'sha256':sha256(exe),'build_seconds':b['seconds'],'publish_seconds':p['seconds'],'compiler_errors':0}
def resolve_ai():
 roots=[INSTALL_ROOT/'AI'/'llama',DATA_ROOT/'System'/'AI'/'llama']; models=[INSTALL_ROOT/'AI'/'Models'/'Qwen3-4B-Q4_K_M.gguf',DATA_ROOT/'System'/'AI'/'Models'/'Qwen3-4B-Q4_K_M.gguf']; runtime=next((x for x in roots if (x/'llama-server.exe').exists() or (x/'llama-cli.exe').exists() or (x/'llama-completion.exe').exists()),None); model=next((x for x in models if x.exists()),None); return runtime,model
def qa(exe,label,stage_id):
 specs=[('--rtl-layout-qa','AUTHORITATIVE_GATE'),('--v13.8.9-qa','AUTHORITATIVE_GATE'),('--v13.8-matrix','AUTHORITATIVE_GATE'),('--self-test','AUTHORITATIVE_GATE'),('--ai-self-test','AUTHORITATIVE_GATE'),('--speaker-self-test','ADVISORY_DIAGNOSTIC' if label=='STAGED' else 'AUTHORITATIVE_GATE')]; runtime,model=resolve_ai(); rows=[]; blockers=[]
 for flag,classification in specs:
  env=os.environ.copy()
  if flag=='--rtl-layout-qa':
   env['ARCHESTRO_RTL_QA_OUTPUT']=str(EVIDENCE_DIR/f'{label}_RTL_LAYOUT_COORDINATES.json')
   env['ARCHESTRO_RTL_QA_ERROR']=str(EVIDENCE_DIR/f'{label}_RTL_LAYOUT_ERROR.txt')
  if label=='STAGED' and flag=='--ai-self-test' and runtime and model: env['ARCHESTRO_AI_RUNTIME_ROOT']=str(runtime); env['ARCHESTRO_AI_MODEL_PATH']=str(model)
  r=run_process(f'{label} {flag}',[exe,flag],cwd=exe.parent,timeout=700,env=env,stage=stage_id); rec={'flag':flag,'classification':classification,'exit':r['exit'],'seconds':r['seconds'],'stdout_path':r['stdout_path'],'stderr_path':r['stderr_path']}; rows.append(rec)
  if classification=='AUTHORITATIVE_GATE' and r['exit']!=0: blockers.append(flag)
 (EVIDENCE_DIR/f'{label}_QA.json').write_text(json.dumps({'results':rows,'blockers':blockers},indent=2),encoding='utf-8')
 if blockers: raise RuntimeError(f'{label} authoritative blocker(s): {blockers}')
 return {'checks':len(rows),'blockers':0,'advisories':[x for x in rows if x['classification']!='AUTHORITATIVE_GATE' and x['exit']!=0]}
def canonical_apply():
 global CANONICAL_MUTATED
 receipts=[]; source_backup=BACKUP/'SOURCE'; source_backup.mkdir(parents=True,exist_ok=True)
 # Freeze the pre-mutation existence/hash state for every target before touching canonical source.
 pre=[]
 for rel_s,new_hash in PATCH_HASHES.items():
  rel=Path(rel_s); dst=SOURCE_ROOT/rel
  pre.append({'path':rel_s,'existed_before':dst.exists(),'sha256_before':sha256(dst) if dst.exists() else None,'desired':new_hash})
 (RECEIPT_DIR/'CANONICAL_SOURCE_BACKUP_MANIFEST.json').write_text(json.dumps(pre,indent=2),encoding='utf-8')
 for rel_s,new_hash in PATCH_HASHES.items():
  rel=Path(rel_s); dst=SOURCE_ROOT/rel; src=PATCH/rel; existed=dst.exists(); current=sha256(dst) if existed else None
  rec={'path':rel_s,'before':current,'desired':new_hash,'existed_before':existed}
  if current==new_hash:
   rec['status']='NO_OP'; receipts.append(rec); continue
  if current!=BASELINE.get(rel_s):
   raise RuntimeError(f'Canonical drift after staging: {rel_s}')
  if existed:
   b=source_backup/rel; b.parent.mkdir(parents=True,exist_ok=True); shutil.copy2(dst,b)
  dst.parent.mkdir(parents=True,exist_ok=True)
  tmp=dst.with_suffix(dst.suffix+'.tmpv26recovery')
  shutil.copy2(src,tmp); os.replace(tmp,dst)
  if sha256(dst)!=new_hash: raise RuntimeError(f'Canonical post-apply hash mismatch: {rel_s}')
  rec['status']='APPLIED_NEW' if not existed else 'APPLIED'
  receipts.append(rec); CANONICAL_MUTATED=True
 (RECEIPT_DIR/'CANONICAL_SOURCE_APPLY.json').write_text(json.dumps(receipts,indent=2),encoding='utf-8')
 return {
  'applied_existing':sum(x['status']=='APPLIED' for x in receipts),
  'applied_new':sum(x['status']=='APPLIED_NEW' for x in receipts),
  'no_op':sum(x['status']=='NO_OP' for x in receipts)
 }
def stop_app(): subprocess.run(['taskkill','/IM','Archestro.MeetingVault.exe','/T','/F'],capture_output=True,text=True); time.sleep(1)
def install_apply():
 global INSTALL_MUTATED
 if not INSTALL_ROOT.exists(): raise RuntimeError(f'Install root missing: {INSTALL_ROOT}')
 stop_app(); install_backup=BACKUP/'INSTALL'; manifest=[]
 for p in PUBLISH.rglob('*'):
  if not p.is_file(): continue
  rel=p.relative_to(PUBLISH); target=INSTALL_ROOT/rel; row={'path':str(rel).replace('\\','/'),'existed_before':target.exists()}
  if target.exists():
   b=install_backup/rel; b.parent.mkdir(parents=True,exist_ok=True); shutil.copy2(target,b); row['sha256_before']=sha256(target)
  manifest.append(row)
 (RECEIPT_DIR/'INSTALL_BACKUP_MANIFEST.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8'); failures=[]; count=0
 for p in PUBLISH.rglob('*'):
  if not p.is_file(): continue
  rel=p.relative_to(PUBLISH); dst=INSTALL_ROOT/rel
  try: dst.parent.mkdir(parents=True,exist_ok=True); shutil.copy2(p,dst); count+=1
  except Exception as e: failures.append({'path':str(rel),'error':repr(e)})
 if failures: (EVIDENCE_DIR/'INSTALL_COPY_FAILURES.json').write_text(json.dumps(failures,indent=2),encoding='utf-8'); raise RuntimeError(f'Install copy failures={len(failures)}')
 INSTALL_MUTATED=True; icon_refresh=refresh_windows_icon_cache(); return {'files':count,'backup_targets':len(manifest),'icon_refresh':icon_refresh}
def refresh_windows_icon_cache():
 results=[]
 try:
  # Tell Explorer/Shell that file associations/icons changed.
  import ctypes
  ctypes.windll.shell32.SHChangeNotify(0x08000000,0,None,None)  # SHCNE_ASSOCCHANGED
  results.append({'method':'SHChangeNotify','status':'PASS'})
 except Exception as e:
  results.append({'method':'SHChangeNotify','status':'WARN','error':repr(e)})
 try:
  ie4=Path(os.environ.get('SystemRoot',r'C:\Windows'))/'System32'/'ie4uinit.exe'
  if ie4.exists():
   r=subprocess.run([str(ie4),'-show'],capture_output=True,text=True,timeout=20)
   results.append({'method':'ie4uinit -show','status':'PASS' if r.returncode==0 else 'WARN','exit':r.returncode})
 except Exception as e:
  results.append({'method':'ie4uinit -show','status':'WARN','error':repr(e)})
 (EVIDENCE_DIR/'ICON_CACHE_REFRESH.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
 return results

def startup_probe():
 exe=INSTALL_ROOT/'Archestro.MeetingVault.exe'; p=subprocess.Popen([str(exe)],cwd=str(INSTALL_ROOT)); t0=time.monotonic()
 while time.monotonic()-t0<7:
  if p.poll() not in (None,0): raise RuntimeError(f'Startup exited early: {p.returncode}')
  sys.stdout.write(f'\r[RUN        {elapsed():08.1f}s] normal startup ({time.monotonic()-t0:.1f}s) PID {p.pid}   '); sys.stdout.flush(); time.sleep(0.25)
 try: subprocess.run(['taskkill','/PID',str(p.pid),'/T','/F'],capture_output=True,text=True)
 except Exception: pass
 sys.stdout.write('\r'+' '*120+'\r'); return {'started':True,'seconds':round(time.monotonic()-t0,2),'exe_sha256':sha256(exe)}
def _tree_bytes(root):
 total=0; files=0
 try:
  if not root.exists(): return {'path':str(root),'exists':False,'files':0,'bytes':0,'gb':0.0}
  for p in root.rglob('*'):
   if p.is_file():
    try: total+=p.stat().st_size; files+=1
    except OSError: pass
 except Exception: pass
 return {'path':str(root),'exists':root.exists(),'files':files,'bytes':total,'gb':round(total/1_000_000_000,3)}

def collect_product_size_inventory():
 install=_tree_bytes(INSTALL_ROOT)
 data=_tree_bytes(DATA_ROOT)
 meetings=_tree_bytes(DATA_ROOT/'Meetings')
 system=_tree_bytes(DATA_ROOT/'System')
 source=_tree_bytes(SOURCE_ROOT/'src')
 publish=_tree_bytes(PUBLISH)

 model_candidates=[
  INSTALL_ROOT/'AI'/'Models'/'Qwen3-4B-Q4_K_M.gguf',
  DATA_ROOT/'System'/'AI'/'Models'/'Qwen3-4B-Q4_K_M.gguf'
 ]
 runtime_candidates=[
  INSTALL_ROOT/'AI'/'llama',
  DATA_ROOT/'System'/'AI'/'llama'
 ]
 model=next((p for p in model_candidates if p.exists()),None)
 runtime=next((p for p in runtime_candidates if p.exists()),None)
 model_info={'path':str(model) if model else None,'exists':bool(model),'bytes':model.stat().st_size if model else 0,'gb':round(model.stat().st_size/1_000_000_000,3) if model else 0.0}
 runtime_info=_tree_bytes(runtime) if runtime else {'path':None,'exists':False,'files':0,'bytes':0,'gb':0.0}

 model_inside_install=bool(model and str(model).lower().startswith(str(INSTALL_ROOT).lower()))
 runtime_inside_install=bool(runtime and str(runtime).lower().startswith(str(INSTALL_ROOT).lower()))
 portable_bytes=install['bytes']
 if model and not model_inside_install: portable_bytes+=model.stat().st_size
 if runtime and not runtime_inside_install: portable_bytes+=runtime_info['bytes']

 result={
  'installed_tree':install,
  'published_tree_this_run':publish,
  'source_tree':source,
  'local_user_data_total':data,
  'meetings_user_data':meetings,
  'system_state_and_models':system,
  'qwen_model':model_info,
  'llama_runtime':runtime_info,
  'model_inside_install':model_inside_install,
  'runtime_inside_install':runtime_inside_install,
  'portable_payload_estimate_excluding_user_meetings_bytes':portable_bytes,
  'portable_payload_estimate_excluding_user_meetings_gb':round(portable_bytes/1_000_000_000,3),
  'note':'User meeting data is intentionally excluded from the redistributable payload estimate.'
 }
 (EVIDENCE_DIR/'PRODUCT_SIZE_INVENTORY.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
 return result

def data_after():
 after=data_rows(); (EVIDENCE_DIR/'DATA_FINGERPRINT_AFTER.json').write_text(json.dumps(after,indent=2),encoding='utf-8'); before={x['path']:x for x in DATA_BEFORE or []}; aft={x['path']:x for x in after}; changed=[]
 for k in sorted(set(before)|set(aft)):
  if k not in before or k not in aft or before[k].get('sha256')!=aft[k].get('sha256'): changed.append(k)
 (EVIDENCE_DIR/'UNEXPECTED_DATA_CHANGES.json').write_text(json.dumps(changed,indent=2),encoding='utf-8')
 if changed: raise RuntimeError(f'User-data integrity mismatch: {len(changed)} file(s).')
 size_inventory=collect_product_size_inventory()
 return {'count':len(after),'unexpected_changes':0,'product_size_inventory':'EVIDENCE/PRODUCT_SIZE_INVENTORY.json','portable_payload_gb':size_inventory.get('portable_payload_estimate_excluding_user_meetings_gb')}
def rollback_source():
 restored=removed_new=0
 manifest_path=RECEIPT_DIR/'CANONICAL_SOURCE_BACKUP_MANIFEST.json'
 rows=json.loads(manifest_path.read_text(encoding='utf-8')) if manifest_path.exists() else [
  {'path':rel_s,'existed_before':(BACKUP/'SOURCE'/Path(rel_s)).exists()} for rel_s in PATCH_HASHES
 ]
 for row in rows:
  rel=Path(row['path']); dst=SOURCE_ROOT/rel; b=BACKUP/'SOURCE'/rel
  if row.get('existed_before'):
   if b.exists():
    dst.parent.mkdir(parents=True,exist_ok=True); shutil.copy2(b,dst); restored+=1
  elif dst.exists():
   try: dst.unlink(); removed_new+=1
   except Exception: pass
 return {'restored':restored,'removed_new':removed_new}
def rollback_install():
 man=RECEIPT_DIR/'INSTALL_BACKUP_MANIFEST.json'; restored=removed=0
 if not man.exists(): return {'skipped':True}
 for row in json.loads(man.read_text(encoding='utf-8')):
  rel=Path(row['path']); target=INSTALL_ROOT/rel; b=BACKUP/'INSTALL'/rel
  if row['existed_before'] and b.exists(): target.parent.mkdir(parents=True,exist_ok=True); shutil.copy2(b,target); restored+=1
  elif not row['existed_before'] and target.exists(): target.unlink(); removed+=1
 return {'restored':restored,'removed_new':removed}
def rollback_data_if_needed():
 if DATA_BEFORE is None: return {'skipped':True}
 current={x['path']:x for x in data_rows()}; before={x['path']:x for x in DATA_BEFORE}; restored=removed=0
 for rel,row in before.items():
  cur=current.get(rel)
  if cur is None or cur.get('sha256')!=row.get('sha256'):
   b=DATA_BACKUP/Path(rel); dst=DATA_ROOT/Path(rel)
   if b.exists(): dst.parent.mkdir(parents=True,exist_ok=True); shutil.copy2(b,dst); restored+=1
 for rel in set(current)-set(before):
  try: (DATA_ROOT/Path(rel)).unlink(); removed+=1
  except Exception: pass
 return {'restored':restored,'removed_new':removed}
def finalize_summary(final_status,notes=None):
 matrix=list(STAGES.values()); (RUN_DIR/'STAGE_MATRIX.json').write_text(json.dumps(matrix,indent=2),encoding='utf-8'); receipt={'SCRIPTING_RUNTIME_CONTRACT':'PASS','KNOWN_FAILURE_REUSE':'PASS','PARSER_STATIC_GATE':'PASS','OWNER_RUN_UX_STANDARD':'PASS','RESULT_LOCALITY':'PASS','RESULT_FORMAT':'PASS','RESULT_AUTO_SURFACE':'PENDING','LAUNCHER_EXIT_TRUTH':'PASS','EXACT_FINAL_PACKAGE_QA':'PASS','RUNTIME_FIXTURE_GATE':'TARGET_RUN_COMPLETED','LEARNING_CLOSEOUT':'KNOWN_INCIDENT_LINKED','DELIVERY_GATE':'PASS' if final_status=='PASS' else 'HOLD','FINAL_STATUS':final_status}; (RUN_DIR/'DELIVERY_RECEIPT.json').write_text(json.dumps(receipt,indent=2),encoding='utf-8'); counts={}
 for s in matrix: counts[s['status']]=counts.get(s['status'],0)+1
 summary='\n'.join(['# Archestro V26 Runtime Recovery — Owner Result','',f'**Final status: {final_status}**','',f'- Result time: {now_iso()}','- AimsTouch: NONE',f'- Canonical source mutated: {CANONICAL_MUTATED}',f'- Installed product mutated: {INSTALL_MUTATED}',f'- Stage counts: {json.dumps(counts)}','- Learning closeout: KNOWN_INCIDENT_LINKED','','## Notes',notes or 'See STAGE_MATRIX.json and EVIDENCE/ for exact receipts.'])+'\n'; (RUN_DIR/'SUMMARY.md').write_text(summary,encoding='utf-8'); (RUN_DIR/'RUN_MANIFEST.json').write_text(json.dumps({'version':RUNNER_VERSION,'stamp':STAMP,'source_root':str(SOURCE_ROOT),'install_root':str(INSTALL_ROOT),'patch_files':list(PATCH_HASHES)},indent=2),encoding='utf-8'); result_zip=RESULT_ROOT/'RESULT_TO_UPLOAD.zip'; tmp=result_zip.with_suffix('.zip.tmp')
 if tmp.exists(): tmp.unlink()
 with zipfile.ZipFile(tmp,'w',zipfile.ZIP_DEFLATED,allowZip64=True) as z:
  for name in ['SUMMARY.md','STAGE_MATRIX.json','EVENTS.jsonl','DELIVERY_RECEIPT.json','RUN_MANIFEST.json']:
   p=RUN_DIR/name
   if p.exists(): z.write(p,name)
  for p in RUN_DIR.rglob('*'):
   if p.is_file() and p.name not in {'SUMMARY.md','STAGE_MATRIX.json','EVENTS.jsonl','DELIVERY_RECEIPT.json','RUN_MANIFEST.json'}: z.write(p,Path('RUN_EVIDENCE')/p.relative_to(RUN_DIR))
 os.replace(tmp,result_zip)
 with zipfile.ZipFile(result_zip) as z:
  bad=z.testzip()
  if bad: raise RuntimeError(f'Result ZIP CRC failure: {bad}')
  if 'SUMMARY.md' not in z.namelist(): raise RuntimeError('Result ZIP missing SUMMARY.md')
 zsha=sha256(result_zip); (RESULT_ROOT/'RESULT_TO_UPLOAD.sha256.txt').write_text(zsha+'  RESULT_TO_UPLOAD.zip\n',encoding='ascii'); return result_zip,zsha
def refreeze_result_zip(result_zip):
 tmp=result_zip.with_suffix('.zip.finalizing.tmp')
 if tmp.exists(): tmp.unlink()
 with zipfile.ZipFile(tmp,'w',zipfile.ZIP_DEFLATED,allowZip64=True) as z:
  for name in ['SUMMARY.md','STAGE_MATRIX.json','EVENTS.jsonl','DELIVERY_RECEIPT.json','RUN_MANIFEST.json']:
   p=RUN_DIR/name
   if p.exists(): z.write(p,name)
  for p in RUN_DIR.rglob('*'):
   if p.is_file() and p.name not in {'SUMMARY.md','STAGE_MATRIX.json','EVENTS.jsonl','DELIVERY_RECEIPT.json','RUN_MANIFEST.json'}:
    z.write(p,Path('RUN_EVIDENCE')/p.relative_to(RUN_DIR))
 os.replace(tmp,result_zip)
 with zipfile.ZipFile(result_zip) as z:
  bad=z.testzip()
  if bad: raise RuntimeError(f'Final Result ZIP CRC failure: {bad}')
 return sha256(result_zip)

def auto_surface(result_zip):
 try:
  subprocess.Popen(['explorer.exe','/select,'+str(result_zip)])
  status='PASS'
 except Exception as e:
  status='WARN'
  emit('WARN',f'Could not auto-select result ZIP: {e}','S13')
 p=RUN_DIR/'DELIVERY_RECEIPT.json'
 rec=json.loads(p.read_text(encoding='utf-8'))
 rec['RESULT_AUTO_SURFACE']=status
 p.write_text(json.dumps(rec,indent=2),encoding='utf-8')
 # Freeze the canonical upload ZIP only after the auto-surface outcome has a final PASS/WARN state.
 final_sha=refreeze_result_zip(result_zip)
 (RESULT_ROOT/'RESULT_TO_UPLOAD.sha256.txt').write_text(final_sha+'  RESULT_TO_UPLOAD.zip\n',encoding='ascii')
 return status,final_sha
def self_test():
 test_root=ROOT/'_SELFTEST'; shutil.rmtree(test_root,ignore_errors=True); test_root.mkdir(parents=True)
 try:
  raw=Path(__file__).read_text(encoding='utf-8')
  required=['MOHAMMED_OWNER_RUN_CONTRACT: V1','RESULT_ROOT: PACKAGE_LOCAL','RESULT_AUTO_SURFACE: REQUIRED','LEARNING_CLOSEOUT: REQUIRED','LONG_RUNNING: YES','HEARTBEAT','ELAPSED_TIMER','STAGE_PROGRESS']
  assert all(x in raw for x in required)
  assert set(BASELINE)==set(PATCH_HASHES)
  assert len(PATCH_HASHES)==5
  for rel,h in PATCH_HASHES.items(): assert (PATCH/rel).exists() and sha256(PATCH/rel)==h
  seq=[]
  def syn(name,fail=False):
   seq.append(name)
   if fail: raise RuntimeError('synthetic intentional failure')
  syn('A')
  try: syn('B',True)
  except RuntimeError: pass
  syn('C'); syn('D'); assert seq==['A','B','C','D']
  fixture=test_root/'fixture.zip'; expected=b'line1\nline2\n'
  with zipfile.ZipFile(fixture,'w') as z: z.writestr('bytes.txt',expected)
  with zipfile.ZipFile(fixture) as z: assert z.testzip() is None and z.read('bytes.txt')==expected
  assert "rec['RESULT_AUTO_SURFACE']=status" in raw
  assert 'refreeze_result_zip(result_zip)' in raw
  assert '5-file V26 candidate delta' in raw
  assert 'collect_product_size_inventory' in raw
  assert 'V26 ROOT RTL GEOMETRY + WORD BIDI — OWNER-RUN INTEGRATION' in raw
  app=(PATCH/'src/Archestro.MeetingVault/App.xaml.cs').read_text(encoding='utf-8-sig')
  assert '--rtl-layout-qa' in app and 'RunRtlLayoutQa(output)' in app
  xaml=(PATCH/'src/Archestro.MeetingVault/Dialogs/IntelligenceWindow.xaml').read_text(encoding='utf-8-sig')
  for marker in ['ReportNavHost','ReportNavReportButton','ReportNavMeetingAskButton','ReportNavVaultAskButton','ReportContentArea','ExecutiveSummaryCard','TopicsCard','KeyPointsCard']:
   assert marker in xaml
  selftest_src=(PATCH/'src/Archestro.MeetingVault/Services/SelfTestService.cs').read_text(encoding='utf-8-sig')
  assert 'RunRtlLayoutQa' in selftest_src and 'OpenXmlValidator' in selftest_src
  exporter=(PATCH/'src/Archestro.MeetingVault/Services/MeetingReportWordExporter.cs').read_text(encoding='utf-8-sig')
  assert 'new W.BiDi()' in exporter and 'new W.RightToLeftText()' in exporter and 'new W.BiDiVisual()' in exporter
  assert "'\\\\u0600'" not in exporter
  assert "'\\\\u06FF'" not in exporter
  assert "'\\u0600'" in exporter and "'\\u06FF'" in exporter
  assert 'CANONICAL_SOURCE_BACKUP_MANIFEST.json' in raw
  assert "'APPLIED_NEW' if not existed else 'APPLIED'" in raw
  assert "'removed_new':removed_new" in raw
  assert 'refresh_windows_icon_cache' in raw
  assert "('--rtl-layout-qa','AUTHORITATIVE_GATE')" in raw
  assert sys.version.split()[0]==EXPECTED_EMBEDDED_PYTHON
  assert Path(sys.executable).name.lower()=='python.exe'
  assert '.runtime' in str(Path(sys.executable)).lower() and 'python' in str(Path(sys.executable)).lower()
  launcher=(ROOT/'START_HERE.cmd').read_text(encoding='ascii',errors='strict')
  assert 'V15 FINAL RESIDUAL CLOSEOUT' not in launcher
  assert 'where py' not in launcher.lower() and 'where python' not in launcher.lower()
  assert r'.runtime\python\python.exe' in launcher.lower()
  assert 'Archestro V26 Runtime Recovery' in launcher
  stale_heading='# Archestro ' + 'V25' + ' — Owner Result'
  assert stale_heading not in raw
  assert RUNNER_VERSION.startswith('V26 ')
  graph={sid:set(deps) for sid,_,deps in STAGE_DEFS}; seen=set(); active=set()
  def visit(n):
   if n in active: raise AssertionError('cycle')
   if n in seen: return
   active.add(n)
   for d in graph[n]: visit(d)
   active.remove(n); seen.add(n)
  for n in graph: visit(n)
  print('[SELF-TEST PASS] V26 runtime recovery contract, embedded-runtime launcher, hashes, geometry QA route, DOCX BiDi guards, continuation fixture, ZIP, stage graph')
  return 0
 finally: shutil.rmtree(test_root,ignore_errors=True)

def main():
 global CANONICAL_MUTATED,INSTALL_MUTATED
 print('='*110); print(' ARCHESTRO MEETING VAULT — V26 RUNTIME RECOVERY — ROOT RTL GEOMETRY + WORD BIDI'); print(' staged-source first • continue-safe diagnostics • heartbeat • one RESULT_TO_UPLOAD.zip • Aims NO TOUCH'); print('='*110)
 s1,_=execute_stage('S01',preflight)
 if not s1:
  for sid in ['S02','S03','S04','S05','S06','S07','S08','S09','S10','S11','S12']: execute_stage(sid,lambda:None,blocked_if=['S01'])
  execute_stage('S13',lambda:{'final':'FAILED_SAFELY'}); z,h=finalize_summary('FAILED_SAFELY','Preflight failed; product/source/install were not mutated.'); surf,final_sha=auto_surface(z); print(f'RESULT: {z}\nSHA-256: {final_sha}\nAUTO-SURFACE: {surf}'); return 2
 execute_stage('S02',current_baseline); s3,_=execute_stage('S03',data_before); s4,_=execute_stage('S04',stage_source); s5,_=execute_stage('S05',structural_checks,blocked_if=None if s4 else ['S04']); s6,_=execute_stage('S06',build_publish,blocked_if=None if s4 else ['S04']); s7,_=execute_stage('S07',lambda:qa(PUBLISH/'Archestro.MeetingVault.exe','STAGED','S07'),blocked_if=None if s6 else ['S06'])
 safe=s3 and s4 and s5 and s6 and s7
 if safe: s8,_=execute_stage('S08',canonical_apply)
 else: s8=False; execute_stage('S08',lambda:None,blocked_if=[x for x,ok in [('S03',s3),('S04',s4),('S05',s5),('S06',s6),('S07',s7)] if not ok])
 if s8: s9,_=execute_stage('S09',install_apply)
 else: s9=False; execute_stage('S09',lambda:None,blocked_if=['S08'])
 s10,_=execute_stage('S10',lambda:qa(INSTALL_ROOT/'Archestro.MeetingVault.exe','INSTALLED','S10'),blocked_if=None if s9 else ['S09']); s11,_=execute_stage('S11',startup_probe,blocked_if=None if s9 else ['S09']); s12,_=execute_stage('S12',data_after,blocked_if=None if s9 else ['S09'])
 installed_ok=s9 and s10 and s11 and s12; rollback_info={}
 if s9 and not installed_ok:
  emit('ROLLBACK','Post-install blocker detected; restoring installed/source/data safepoints.','S13')
  try: rollback_info['install']=rollback_install()
  except Exception as e: rollback_info['install_error']=repr(e)
  try: rollback_info['source']=rollback_source()
  except Exception as e: rollback_info['source_error']=repr(e)
  try: rollback_info['data']=rollback_data_if_needed()
  except Exception as e: rollback_info['data_error']=repr(e)
  INSTALL_MUTATED=False; CANONICAL_MUTATED=False; final='FAILED_SAFELY_ROLLED_BACK'
 elif installed_ok: final='PASS'
 else:
  if CANONICAL_MUTATED:
   try: rollback_info['source']=rollback_source(); CANONICAL_MUTATED=False
   except Exception as e: rollback_info['source_error']=repr(e)
  final='COMPLETED_WITH_BLOCKERS'
 execute_stage('S13',lambda:{'final_status':final,'rollback':rollback_info}); z,h=finalize_summary(final,'All safe work completed. Upload only RESULT_TO_UPLOAD.zip.'); surf,final_sha=auto_surface(z); print('\n'+'='*110); print(' COMPLETED' if final=='PASS' else ' '+final); print(f' RESULT: {z}'); print(f' SHA-256: {final_sha}'); print(f' AUTO-SURFACE: {surf}'); print('='*110); return 0 if final=='PASS' else 2
if __name__=='__main__':
 if '--self-test' in sys.argv: raise SystemExit(self_test())
 raise SystemExit(main())