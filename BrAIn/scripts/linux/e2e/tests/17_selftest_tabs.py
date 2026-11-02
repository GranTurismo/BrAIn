"""The app's built-in tab self-test (the same one the Windows CI runs): tabs keep their page, scroll and typed text."""
from mcp import *

report = os.path.expanduser('~/.local/share/BrAIn/selftest-tabs.txt')
r = subprocess.run('cd /app && timeout 300 ./BrAIn --selftest-tabs --selftest-quiet </dev/null >/tmp/o.txt 2>/tmp/e.txt', shell=True)
text = open(report).read() if os.path.exists(report) else ''
print(text)
check('--selftest-tabs exits with code 0', r.returncode == 0, r.returncode)
check('every self-test step passed', 'RESULT: ALL PASSED' in text and 'FAIL' not in text)
finish()
