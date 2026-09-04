#!/usr/bin/env sh
#
# Run the kernel suite. Installs the .NET SDK first if the machine has none, which is the case on
# a fresh cloud container and is one apt away.
#
# The kernel is engine-free, so the suite compiles and runs without s&box.
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
# python3 on a Linux container, python on a Windows checkout where only the py launcher's
# `python` alias exists - the `python3` there is the Store stub, which prints an ad and exits 9009.
# Asks each candidate to run rather than just looking it up on PATH: Windows ships a `python3`
# that exists, prints a Store advert and exits 9009.
py=
for candidate in python3 python py; do
	if "$candidate" -c "" >/dev/null 2>&1; then py=$candidate; break; fi
done
[ -n "$py" ] || { echo "no working python for the editor lint" >&2; exit 1; }
"$py" "$root/tools/lint-editor-usings.py"

cd "$root/Effigy.Tests"
exec dotnet run --  "${1:-out}"
