#!/usr/bin/env python3
import pathlib
import re
import sys
text = pathlib.Path(sys.argv[1]).read_text()
summary = re.findall(r'Total:\s*(\d+),\s*Errors:\s*(\d+),\s*Failed:\s*(\d+),\s*Skipped:\s*(\d+)', text)
not_run = re.findall(r'Not Run:\s*(\d+)', text)
if len(summary) != 1 or int(summary[0][0]) == 0 or any(int(value) for value in summary[0][1:]) or any(int(value) for value in not_run):
    raise SystemExit('Require a nonzero xUnit Total, zero errors/failures and no skipped selected QA cases.')
print('Verified xUnit Total:', summary[0][0])
