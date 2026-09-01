#!/usr/bin/env sh
#
# Run the kernel suite. Installs the .NET SDK first if the machine has none, which is the case on
# a fresh cloud container and is one apt away.
#
# WHY THIS IS WORTH ONE COMMAND: the kernel is engine-free by design, so all of it - plus every
# editor workflow that is "kernel calls in a particular order", plus the sketch snapping and the
# expression evaluator - compiles and runs with no s&box anywhere. A session that does not do this
# ends up verifying changes by reading them, and reading is how a bug that made every parameter
# edit a silent no-op survived long enough to look like three unrelated UI faults.
set -eu

root=$( cd "$( dirname "$0" )/.." && pwd )

if ! command -v dotnet >/dev/null 2>&1; then
	echo "no dotnet - installing the SDK"
	apt-get update -qq
	DEBIAN_FRONTEND=noninteractive apt-get install -y -qq dotnet-sdk-8.0
fi

# Keep the editor's copy of the kernel in step before testing the canonical one, so a run can
# never pass against source the editor is not actually compiling.
"$root/tools/sync-kernel.sh" >/dev/null

# The editor assembly is not compiled by anything in this container, so the one class of editor
# mistake that can be caught without s&box is caught here instead. See the script's own header:
# a missing `using Editor;` is indistinguishable from a missing assembly when the engine is absent,
# so it hides inside hundreds of identical CS0246s and survives a "compiles clean" claim.
python3 "$root/tools/lint-editor-usings.py"

cd "$root/Effigy.Tests"
exec dotnet run --  "${1:-out}"
