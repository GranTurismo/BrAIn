import sys, json
m = {v: k for k, v in json.load(open("keymap.json")).items()}
print("".join(m.get(c, c) for c in sys.stdin.read()), end="")
