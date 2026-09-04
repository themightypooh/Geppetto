"""Turn the Unreleased section of CHANGELOG.md into changelist entries for sbox.game.

Called by tools/changelist.sh, which is the thing to run. The header there explains why the
changelist is the one part of shipping that is still typed by hand.
"""

import re
import sys

# The notes carry arrows and em dashes, and Windows hands Python a cp1252 stdout, which raises
# rather than mangles. Say UTF-8 out loud.
sys.stdout.reconfigure(encoding="utf-8")

# The five boxes the form asks for, in the order it asks for them. CHANGELOG.md uses the same five
# as its ### headings, which is the whole point: the file says which box a line belongs in, so
# nothing here has to infer it from wording. An earlier version guessed from how a line started and
# filed a new feature under Fixed because the sentence happened to contain "no longer".
BOXES = ["Added", "Improved", "Fixed", "Removed", "Known Issues"]

text = open(sys.argv[1], encoding="utf-8").read()

# HTML comments are notes to whoever maintains the file. Left in, the block at the end of the last
# section gets swept up as part of its final bullet and pasted onto the store page.
text = re.sub(r"<!--.*?-->", "", text, flags=re.S)

# Which "## " section to print. No argument means Unreleased, which is the common case. A version
# is for going back and filling in a changelist for a revision already published, which the site
# allows from its "Assign to a revision" list.
wanted = sys.argv[2] if len(sys.argv) > 2 else "Unreleased"

headings = re.findall(r"^## (.+?)\s*$", text, re.M)

# Match on the leading token, so "367356" finds "v367356 — 2026-09-04" without retyping the date.
matches = [h for h in headings
           if h == wanted or h.split()[0].lstrip("v") == wanted.lstrip("v")]

if not matches:
    print("no section '{}' in CHANGELOG.md. It has:".format(wanted))

    for h in headings:
        print("    " + h)

    raise SystemExit(1)

heading = matches[0]

# To the next "## ", or the end of the file for the last section.
unreleased = re.search(
    r"^## " + re.escape(heading) + r"\s*\n(.*?)(?=^## |\Z)", text, re.S | re.M).group(1)

# Say which section this is whenever it is not the one asked for verbatim, so a paste into the
# wrong revision's boxes is caught before it is pasted.
if heading != wanted:
    print("# {}".format(heading))
    print()

buckets = {name: [] for name in BOXES}
unknown = {}

# Split on the ### headings, keeping which heading each run of bullets sat under.
sections = re.split(r"^###\s+(.+?)\s*$", unreleased, flags=re.M)

# sections[0] is anything before the first heading - bullets written without one.
if sections[0].strip().startswith("-"):
    unknown["(no heading)"] = sections[0]

for name, body in zip(sections[1::2], sections[2::2]):
    if name in buckets:
        buckets[name].append(body)
    else:
        unknown.setdefault(name, "")
        unknown[name] += body


def entries(body):
    """The bullets in one section, each flattened to the single line the form wants."""

    out = []

    for chunk in body.split("\n- ")[1:]:
        # One line per bullet: the form reads a newline as a new entry, so a wrapped sentence
        # would arrive as three entries that each say a third of a thing.
        line = re.sub(r"\s+", " ", chunk.strip()).strip()

        # Sub-bullets are detail on the line above, not entries of their own.
        line = re.sub(r"\s+- ", " - ", line)

        # A trailing (`Foo.cs`, `Bar`) is a note to whoever works on the repo. Nobody reading
        # release notes on a store page wants it.
        line = re.sub(r"\s*\((?:`[^`]+`(?:,\s*)?)+\)\s*$", "", line)

        # The form's boxes are plain text, so markdown emphasis arrives as literal asterisks and
        # backticks. Keep the words, drop the markup - it reads correctly in the file either way.
        line = re.sub(r"\*\*(.+?)\*\*", r"\1", line)
        line = line.replace("`", "")

        if line:
            out.append(line)

    return out


for name in BOXES:
    lines = [line for body in buckets[name] for line in entries(body)]

    if not lines:
        continue

    print("--- {} {}".format(name, "-" * (60 - len(name))))

    for line in lines:
        print(line)

    print()

# A heading that is not one of the five is a line that will not reach the form, so say so rather
# than dropping it silently - the failure mode this replaces is notes that never got published.
for name, body in unknown.items():
    lines = entries(body)

    if not lines:
        continue

    print("!!! {} is not one of the five boxes - these will not be published:".format(name))

    for line in lines:
        print("    " + line)

    print()
    print("    Move them under {} in CHANGELOG.md.".format(", ".join(BOXES)))
    print()
