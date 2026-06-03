import sys
import re
import subprocess
from pathlib import Path

root = Path(sys.argv[1])
pattern = re.compile(sys.argv[2]) if len(sys.argv) > 2 else None
out = Path(__file__).parent / "source.txt"

tracked = set(
    subprocess.check_output(["git", "ls-files"], cwd=root, text=True).splitlines()
)

with out.open("w") as f:
    for rel in sorted(tracked):
        if pattern and not pattern.search(rel):
            continue
        p = root / rel
        if p.is_file():
            f.write(f"***** {p} *****\n{p.read_text(errors='replace')}\n")
