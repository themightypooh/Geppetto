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

# The editor assembly itself, compiled headlessly against s&box's own libraries. The lint above
# catches one kind of mistake; this catches all of them - a broken editor build used to surface
# only as a red line in s&box's log, and only once the editor was focused and tried to hot-reload.
# The project file is the one s&box generates for IDEs (gitignored, machine-specific paths), so it
# exists only where s&box has opened this project. Without it - a fresh cloud container - the step
# is skipped and says so, rather than failing a suite that has no editor to build.
editor_proj="$root/Editor/geppetto.editor.csproj"
engine_ref=$( sed -n 's|.*Reference Include="\([^"]*Sandbox\.Engine\.dll\)".*|\1|p' "$editor_proj" 2>/dev/null | head -n 1 )

if [ -f "$editor_proj" ] && [ -n "$engine_ref" ] && [ -f "$root/Editor/$engine_ref" ]; then
	echo "building the editor assembly headlessly"
	editor_out=$( mktemp -d )
	if ! dotnet build "$editor_proj" -c Release -o "$editor_out" -nologo -clp:ErrorsOnly; then
		rm -rf "$editor_out"
		echo "the editor assembly does not compile - fix the errors above" >&2
		exit 1
	fi
	rm -rf "$editor_out"
else
	echo "skipping the editor build: no s&box-generated Editor/geppetto.editor.csproj (open the project in s&box once to make it)"
fi

cd "$root/Effigy.Tests"
exec dotnet run --  "${1:-out}"
