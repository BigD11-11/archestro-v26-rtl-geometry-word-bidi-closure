from pathlib import Path
import hashlib,json,sys
root=Path(sys.argv[1]); source=sys.argv[2]; branch=sys.argv[3]; stamp=sys.argv[4]
files=[]
for p in sorted(x for x in root.rglob('*') if x.is_file() and x.name!='PACKAGE_MANIFEST.json'):
    rel=p.relative_to(root).as_posix()
    h=hashlib.sha256()
    with p.open('rb') as f:
        for block in iter(lambda:f.read(8*1024*1024),b''): h.update(block)
    files.append({'path':rel,'sizeBytes':p.stat().st_size,'sha256':h.hexdigest()})
exe=next(x for x in files if x['path']=='App/Archestro.MeetingVault.exe')
manifest={'product':'Archestro Meeting Vault','customerVersion':'1.1.0','internalBuild':'V29','sourceCommit':source,'branch':branch,'buildTimestampUtc':stamp,'architecture':'win-x64','framework':'net10.0-windows self-contained','defaultProvider':'Local','cloudEnabledByDefault':False,'releaseExeSha256':exe['sha256'],'releaseExeBytes':exe['sizeBytes'],'payloadFiles':files,'totalPayloadBytes':sum(x['sizeBytes'] for x in files),'omittedDemoAssets':['App/SpeakerModels/0-four-speakers-zh.wav']}
(root/'PACKAGE_MANIFEST.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
print(json.dumps({k:manifest[k] for k in ('customerVersion','internalBuild','sourceCommit','buildTimestampUtc','releaseExeSha256','releaseExeBytes','totalPayloadBytes')},indent=2)); print('payloadFiles',len(files))
