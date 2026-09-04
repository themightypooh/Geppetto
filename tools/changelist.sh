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
# CATEGORIES COME FROM THE WORDING. A bullet lands in Added / Improved / Fixed / Removed / Known
# Issues depending on how it starts, and anything unclassified goes to Improved because that is
# the box a reader forgives being wrong. Move a line by hand if you disagree - this reads the
# file, it does not know better than you.
#
#   tools/changelist.sh
#
set -eu

root=$( cd "$( dirname "$0" )/.." && pwd )
cd "$root"

python "$root/tools/changelist.py" "$root/CHANGELOG.md"

echo "Paste these into https://sbox.game/pooh/geppetto - Edit changelist."
