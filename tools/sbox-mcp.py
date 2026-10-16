#!/usr/bin/env python3
#
# The s&box editor's MCP server, as a stdio MCP server - the thing .mcp.json and publish.sh talk to.
#
# WHY THIS EXISTS. Since 26.10 the editor no longer serves MCP over HTTP on 127.0.0.1:7269. It
# listens on a same-user local pipe instead, which on Linux is a unix socket named
# /tmp/CoreFxPipe_sbox-mcp-<pid>-<hash>, speaking line-delimited JSON-RPC. The official client,
# `dotnet <engine>/bin/managed/sbox-cli.dll --connect`, is the obvious thing to register - but on
# Linux its `--list` finds no editors and `--connect <pid>` attaches and then never answers, while
# a plain socket connection to the same pipe works. So this is that plain connection: stdin lines
# go to the socket, socket lines come back on stdout, nothing is reinterpreted.
#
# WHICH EDITOR. Every editor that is open has its own socket. SBOX_PID picks one; otherwise the
# editor whose -project is geppetto wins, then the only editor running, then the newest. Sockets
# left behind by editors that have exited are skipped - /tmp keeps them after a crash.
#
# THE EDITOR GOING AWAY. Restarting the editor (or it crashing) drops the socket. The next message
# reconnects to whichever editor is up and replays the handshake the client sent the first time,
# so a long Claude session does not lose its tools to an editor restart. Calls that were in flight
# when it dropped are answered with an error rather than left hanging.
#
#   tools/sbox-mcp.py                          stdio MCP server (what .mcp.json runs)
#   tools/sbox-mcp.py --call <tool> [json]     one tool call, prints the raw JSON-RPC reply
#   tools/sbox-mcp.py --which                  print the socket it would use
#
import glob
import json
import os
import socket
import sys
import threading

PROJECT = "geppetto"
REPLAY = "sbox-mcp-replay"


def alive(pid):
	try:
		os.kill(int(pid), 0)
		return True
	except (OSError, ValueError):
		return False


def project_of(pid):
	try:
		args = open("/proc/%s/cmdline" % pid, "rb").read().split(b"\0")
	except OSError:
		return ""
	for i, a in enumerate(args[:-1]):
		if a == b"-project":
			p = args[i + 1].decode(errors="replace")
			return os.path.splitext(os.path.basename(p))[0] if p.endswith(".sbproj") else os.path.basename(p.rstrip("/"))
	return ""


def find_socket():
	want = os.environ.get("SBOX_PID")
	found = []
	for path in glob.glob("/tmp/CoreFxPipe_sbox-mcp-*"):
		pid = os.path.basename(path).split("-")[2]
		if alive(pid) and (not want or pid == want):
			found.append((path, pid))
	if not found:
		return None
	mine = [f for f in found if project_of(f[1]).lower() == PROJECT]
	pool = mine or found
	return max(pool, key=lambda f: os.path.getmtime(f[0]))[0]


def connect():
	path = find_socket()
	if not path:
		return None
	s = socket.socket(socket.AF_UNIX)
	try:
		s.connect(path)
	except OSError:
		return None
	return s


def no_editor(msg_id):
	return {"jsonrpc": "2.0", "id": msg_id, "error": {"code": -32000, "message":
		"no s&box editor is listening - open Geppetto in sbox-dev (Project Settings > AI > Enable MCP Communication must be on)"}}


def one_call(tool, args):
	s = connect()
	if not s:
		print("no s&box editor is listening", file=sys.stderr)
		return 1
	s.settimeout(int(os.environ.get("SBOX_MCP_TIMEOUT", "300")))
	f = s.makefile("rwb")

	def send(o):
		f.write((json.dumps(o) + "\n").encode())
		f.flush()

	def recv(i):
		while True:
			line = f.readline()
			if not line:
				raise EOFError("editor closed the pipe")
			m = json.loads(line)
			if m.get("id") == i:
				return m

	send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-03-26",
		"capabilities": {}, "clientInfo": {"name": "geppetto-sbox-mcp", "version": "1"}}})
	recv(1)
	send({"jsonrpc": "2.0", "method": "notifications/initialized"})
	send({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": tool, "arguments": json.loads(args)}})
	print(json.dumps(recv(2)))
	return 0


class Bridge:
	def __init__(self):
		self.out = threading.Lock()
		self.sock = None
		self.handshake = []   # the client's initialize + initialized, replayed on reconnect
		self.pending = set()  # request ids sent to the editor and not answered yet
		self.lock = threading.Lock()

	def write(self, obj):
		with self.out:
			sys.stdout.write(json.dumps(obj) + "\n")
			sys.stdout.flush()

	def pump(self, s):
		f = s.makefile("rb")
		for line in f:
			try:
				m = json.loads(line)
			except ValueError:
				continue
			if m.get("id") == REPLAY:
				continue
			with self.lock:
				self.pending.discard(m.get("id"))
			self.write(m)
		with self.lock:
			if self.sock is s:
				self.sock = None
			lost, self.pending = self.pending, set()
		for i in lost:
			self.write({"jsonrpc": "2.0", "id": i, "error": {"code": -32000, "message": "the s&box editor closed the connection"}})

	def ensure(self, replay):
		with self.lock:
			if self.sock:
				return self.sock
		s = connect()
		if not s:
			return None
		threading.Thread(target=self.pump, args=(s,), daemon=True).start()
		if replay:
			for m in self.handshake:
				m = dict(m, id=REPLAY) if "id" in m else m
				s.sendall((json.dumps(m) + "\n").encode())
		with self.lock:
			self.sock = s
		return s

	def run(self):
		for line in sys.stdin:
			line = line.strip()
			if not line:
				continue
			try:
				m = json.loads(line)
			except ValueError:
				continue
			method = m.get("method")
			first = method == "initialize"
			if first:
				self.handshake = [m]
			elif method == "notifications/initialized":
				self.handshake.append(m)
			s = self.ensure(replay=not first)
			if not s:
				if "id" in m:
					self.write(no_editor(m["id"]))
				continue
			if "id" in m and method:
				with self.lock:
					self.pending.add(m["id"])
			try:
				s.sendall((line + "\n").encode())
			except OSError:
				with self.lock:
					self.sock = None
					self.pending.discard(m.get("id"))
				if "id" in m:
					self.write(no_editor(m["id"]))


def main():
	a = sys.argv[1:]
	if a[:1] == ["--which"]:
		print(find_socket() or "")
		return 0 if find_socket() else 1
	if a[:1] == ["--call"] and len(a) >= 2:
		return one_call(a[1], a[2] if len(a) > 2 else "{}")
	if a:
		print(__doc__ or "usage: sbox-mcp.py [--call <tool> [json] | --which]", file=sys.stderr)
		return 1
	Bridge().run()
	return 0


if __name__ == "__main__":
	sys.exit(main())
