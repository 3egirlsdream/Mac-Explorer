import json,subprocess,time
from pathlib import Path
out=Path('outputs/finder-comparison-20261025')
p=out/'data/interactive-cached-trials.jsonl'
while True:
 rows=[json.loads(s) for s in p.read_text().splitlines()] if p.exists() else []
 if len(rows)==30:
  if not all(r['valid'] for r in rows):raise SystemExit('Invalid interactive trial; memory not started')
  # Let the final native recording finish its tail and teardown.
  time.sleep(3)
  break
 time.sleep(2)
with (out/'source/memory-run.log').open('w') as log:
 result=subprocess.run(['python3','Tools/Performance/FinderComparison/memory_trials.py'],stdout=log,stderr=subprocess.STDOUT)
raise SystemExit(result.returncode)
