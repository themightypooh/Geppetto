"""Turn the Unreleased section of CHANGELOG.md into changelist entries for sbox.game.

Called by tools/changelist.sh, which is the thing to run. The header there explains why the
changelist is the one part of shipping that is still typed by hand.
"""

import re
import sys

# The notes carry arrows and em dashes, and Windows hands Python a cp1252 stdout, which raises
# rather than mangles. Say UTF-8 out loud.
sys.stdout.reconfigure(encoding="utf-8")

text = open(sys.argv[1], encoding="utf-8").read()

# Everything between "## Unreleased" and the next release heading.
match = re.search(r"^## Unreleased\s*\n(.*?)(?=^## )", text, re.S | re.M)

if not match:
    print("no Unreleased section in CHANGELOG.md")
    raise SystemExit(1)

bullets = []

for chunk in match.group(1).split("\n- ")[1:]:
    # Section headings (### Effigy, ### Tooling) group the file for a reader; to the form they are
    # not entries. Cut each bullet where the next heading starts rather than letting one trail on.
    chunk = re.split(r"\n\s*#", chunk)[0]

    # One line per bullet: the form reads a newline as a new entry, so a wrapped sentence would
    # arrive as three entries that each say a third of a thing.
    line = re.sub(r"\s+", " ", chunk.strip()).strip()

    # Sub-bullets are detail on the line above, not entries of their own.
    line = re.sub(r"\s+- ", " - ", line)

    # A trailing (`Foo.cs`, `Bar`) is a note to whoever works on the repo. Nobody reading release
    # notes on a store page wants it.
    line = re.sub(r"\s*\((?:`[^`]+`(?:,\s*)?)+\)\s*$", "", line)

    if line:
        bullets.append(line)

buckets = {"Added": [], "Improved": [], "Fixed": [], "Removed": [], "Known Issues": []}

for line in bullets:
    low = line.lower()

    # How a line STARTS, not what it mentions: half these entries say "no longer" somewhere in the
    # middle while describing a new feature, and matching that anywhere filed them all as fixes.
    if low.startswith(("fix", "stop ", "no longer")):
        buckets["Fixed"].append(line)
    elif low.startswith(("remove", "drop", "delete")):
        buckets["Removed"].append(line)
    elif low.startswith(("known ", "still ")) or "not yet" in low:
        buckets["Known Issues"].append(line)
    elif low.startswith(("add", "new ", "select first", "double-click")):
        buckets["Added"].append(line)
    else:
        buckets["Improved"].append(line)

for name, lines in buckets.items():
    if not lines:
        continue

    print("--- {} {}".format(name, "-" * (60 - len(name))))

    for line in lines:
        print(line)

    print()
