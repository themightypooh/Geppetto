#!/usr/bin/env sh
#
# Print the Unreleased section of CHANGELOG.md as lines to paste into the changelist form on
# sbox.game, one bullet per line, sorted into the boxes it asks for.
#
# WHY THIS IS NOT AUTOMATIC, unlike the publish. The changelist is a website thing and the engine
# only reads them - Sandbox.Services.ServiceApi.IPackageApi carries [Get("/package/changelists/2/
# {ident}")] and nothing that writes one, so the editor cannot post it and neither can a script
# driving the editor. The form is the only door there is. What was avoidable is retyping the notes
# into it, and that is what this removes.
#
# CATEGORIES COME FROM THE FILE. CHANGELOG.md groups Unreleased under the same five headings the
# form asks for, so this reads them rather than inferring anything. It used to guess from how a
# line started and put a new feature under Fixed because the sentence contained "no longer" - the
# file knows which box a change belongs in, and nothing here is better placed to decide.
#
# A bullet under any other heading is reported as unpublishable rather than dropped, because the
# failure this whole thing exists to prevent is a note that never reached anybody.
#
#   tools/changelist.sh              the Unreleased section
#   tools/changelist.sh 367356    a revision already published, for the site's
#                                 "Assign to a revision" list
#
set -eu

root=$( cd "$( dirname "$0" )/.." && pwd )
cd "$root"

python "$root/tools/changelist.py" "$root/CHANGELOG.md" "$@"

echo "Paste these into https://sbox.game/pooh/geppetto - Edit changelist."
