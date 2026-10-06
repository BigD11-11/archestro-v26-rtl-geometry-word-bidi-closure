import sqlite3, hashlib, json, sys
from pathlib import Path
before, after = map(Path, sys.argv[1:3])
def digest(db, table):
    c=sqlite3.connect(f'file:{db.as_posix()}?mode=ro', uri=True)
    c.row_factory=sqlite3.Row
    names=[r[0] for r in c.execute(f'pragma table_info("{table}")')]
    rows=[dict(r) for r in c.execute(f'select * from "{table}"')]
    rows.sort(key=lambda x: json.dumps(x, ensure_ascii=False, sort_keys=True, default=str))
    blob=json.dumps(rows,ensure_ascii=False,sort_keys=True,separators=(',',':'),default=str).encode()
    c.close(); return len(rows),hashlib.sha256(blob).hexdigest()
result={'migration':'additive AI usage/pricing/FX schema','tables':{}}
for table in ('meetings','categories','important_marks'):
    n0,h0=digest(before,table); n1,h1=digest(after,table)
    result['tables'][table]={'beforeRows':n0,'afterRows':n1,'beforeSemanticSha256':h0,'afterSemanticSha256':h1,'preserved':n0==n1 and h0==h1}
c=sqlite3.connect(after); result['integrityCheck']=c.execute('pragma integrity_check').fetchone()[0]; result['foreignKeyViolations']=len(c.execute('pragma foreign_key_check').fetchall()); result['ledgerRows']=c.execute('select count(*) from ai_usage_ledger').fetchone()[0]; result['pricingSnapshots']=c.execute('select count(*) from ai_pricing_snapshots').fetchone()[0]; result['fxSnapshots']=c.execute('select count(*) from ai_fx_snapshots').fetchone()[0]; result['migrationRows']=c.execute('select count(*) from app_schema_migrations').fetchone()[0]; c.close(); result['allPriorSemanticDataPreserved']=all(v['preserved'] for v in result['tables'].values()) and result['integrityCheck']=='ok' and result['foreignKeyViolations']==0
print(json.dumps(result,indent=2))
