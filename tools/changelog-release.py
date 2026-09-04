"""Retire the Unreleased section of CHANGELOG.md as a published revision.

Called by tools/ship.sh once a publish has actually created a revision. Renames the Unreleased
heading to that version and opens a fresh empty Unreleased above it, so the next batch of notes
has somewhere to go and the released ones stop looking pending.

Exits 1 without touching the file when there is nothing to retire - a ship that changed only
tooling leaves Unreleased empty, and stamping an empty section onto a revision would put a heading
with no notes under it in the file forever.

    python tools/changelog-release.py CHANGELOG.md 367364
"""

import datetime
import re
import sys

path, revision = sys.argv[1], sys.argv[2]

text = open(path, encoding="utf-8").read()

match = re.search(r"^## Unreleased\s*\n(.*?)(?=^## |\Z)", text, re.S | re.M)

if not match:
    print("no Unreleased section in {} - not stamping".format(path))
    raise SystemExit(1)

body = match.group(1)

# "Nothing yet." is the placeholder the fresh section carries, so it counts as empty.
has_notes = bool(re.search(r"^- ", body, re.M))

if not has_notes:
    print("Unreleased is empty - v{} published no user-visible notes, so nothing to stamp"
          .format(revision))
    raise SystemExit(1)

today = datetime.date.today().isoformat()

replacement = (
    "## Unreleased\n"
    "\n"
    "Nothing yet.\n"
    "\n"
    "## v{} — {}\n\n".format(revision, today)
)

text = text[:match.start()] + replacement + body + text[match.end():]

open(path, "w", encoding="utf-8", newline="\n").write(text)

print("CHANGELOG: Unreleased is now v{} — {}".format(revision, today))
