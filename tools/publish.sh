#!/usr/bin/env sh
#
# Publish the library package to s&box from the shell, by driving the editor that is already open.
#
# WHY THIS IS NOT JUST A COMMAND. The upload is `geppetto_publish`, a ConCmd living inside the
# editor - it needs the editor's own ProjectPublisher, its login and the project it has open, none
# of which exist in a shell. What DOES reach out of the editor is its MCP bridge, an HTTP server on
# 127.0.0.1:7269 whose `console_command` tool runs anything the console accepts. So this does not
# reimplement publishing: it types the command into the editor's console over HTTP and reads the
# answer back out of the log, which is exactly what a person does by hand.
#
# THE EDITOR HAS TO BE OPEN, ON THIS PROJECT. Both are checked before anything is sent, because the
# failure they prevent is publishing the wrong package - the stray pooh.toolshed package got made
# by hand that way once already. No editor is not an error: ship.sh calls this at the end of a run
# that already succeeded, and a missing editor should leave you with a pushed commit and a note,
# not a red build.
#
#   tools/publish.sh              what would be published, uploads nothing
#   tools/publish.sh --commit     send it, notes taken from the last commit
#
set -eu

root=$( cd "$( dirname "$0" )/.." && pwd )
cd "$root"

bridge=${SBOX_MCP_URL:-http://127.0.0.1:7269/mcp}
project=geppetto
commit=0

while [ $# -gt 0 ]; do
	case "$1" in
		--commit) commit=1 ;;
		-h|--help) awk 'NR>2 && /^#/ { sub(/^# ?/, ""); print; next } NR>2 { exit }' "$0"; exit 0 ;;
		*) echo "unknown argument: $1" >&2; exit 1 ;;
	esac
	shift
done

# One call, one JSON-RPC id: the bridge is stateless over HTTP, so nothing has to be kept between
# calls and a failed curl is just a failed call rather than a broken session.
call() {
	curl -s -m 120 -X POST "$bridge" \
		-H 'Content-Type: application/json' \
		-H 'Accept: application/json, text/event-stream' \
		-d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"$1\",\"arguments\":$2}}"
}

# The bridge answers in MCP's envelope - result.content[0].text - and the text is itself JSON for
# some tools and plain lines for others. Pull the text out and let the caller deal with it.
text() {
	python -c 'import sys,json;d=json.load(sys.stdin);print(d.get("result",{}).get("content",[{}])[0].get("text",""))'
}

if ! status=$( call editor_status '{}' 2>/dev/null ) || [ -z "$status" ]; then
	echo ""
	echo "no editor on $bridge - the package was NOT published."
	echo ""
	echo "Open Geppetto in s&box and run this again, or publish from the editor console:"
	echo ""
	echo "    geppetto_publish            what would go, uploads nothing"
	echo "    geppetto_publish commit     send it, notes taken from the last commit"
	exit 0
fi

open=$( printf '%s' "$status" | text | python -c 'import sys,json;print(json.load(sys.stdin).get("Project",""))' )

if [ "$open" != "$project" ]; then
	echo "the editor has '$open' open, not '$project' - refusing to publish." >&2
	echo "Whatever is open is what gets published, so this would send the wrong package." >&2
	exit 1
fi

# WHERE THE LOG IS UP TO, BEFORE the command runs. Every read_console ends with a cursor, and
# reading from it afterwards is the difference between this run's output and this run's output
# mixed with the last one's - which, for two dry runs in a row, look identical and are the sort of
# thing you would read as success.
since=$( call read_console '{"limit":1}' | text | sed -n 's/^Cursor: //p' )
since=${since:-0}

if [ "$commit" -eq 1 ]; then
	echo "==> publishing (editor has $open open)"
	call console_command '{"command":"geppetto_publish commit"}' >/dev/null
else
	echo "==> publish dry run (editor has $open open)"
	call console_command '{"command":"geppetto_publish"}' >/dev/null
fi

# The ConCmd kicks off an async task and returns immediately, so the console is empty for a moment
# and an upload of any size takes longer than that. Poll for the line that ends the run rather than
# sleeping a guessed number of seconds and printing half a log.
i=0

while [ "$i" -lt 90 ]; do
	log=$( call read_console "{\"filter\":\"[publish]\",\"limit\":40,\"since\":$since}" | text )

	case "$log" in
		*"DRY RUN"*|*"] published "*|*"failed:"*) break ;;
	esac

	i=$(( i + 1 ))
	sleep 2
done

echo ""
printf '%s\n' "$log" | sed -n 's/^[0-9:]* \[Generic\] [A-Za-z]*: //p'
echo ""

case "$log" in
	*"failed:"*)
		echo "the publish did not go through - see above." >&2
		exit 1 ;;
	*"] published "*)
		echo "https://sbox.game/pooh/geppetto" ;;
esac
