#!/usr/bin/env python3
"""Fake MCP stdio server — JSON-RPC newline-delimited (SPEC-07 tests).

Responde initialize / tools/list / tools/call (echo) / ping.
"""
import json
import sys

TOOLS = [{
    "name": "echo",
    "description": "echo back the text argument",
    "inputSchema": {
        "type": "object",
        "properties": {"text": {"type": "string"}},
        "required": ["text"],
    },
}]


def reply(rid, result):
    sys.stdout.write(json.dumps(
        {"jsonrpc": "2.0", "id": rid, "result": result}) + "\n")
    sys.stdout.flush()


def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError:
            continue
        method, rid = req.get("method"), req.get("id")
        params = req.get("params") or {}
        if method == "initialize":
            reply(rid, {
                "protocolVersion": params.get(
                    "protocolVersion", "2025-03-26"),
                "capabilities": {"tools": {}},
                "serverInfo": {"name": "fake-mcp", "version": "0.1"},
            })
        elif method in ("notifications/initialized", "initialized"):
            continue
        elif method == "tools/list":
            reply(rid, {"tools": TOOLS})
        elif method == "tools/call":
            text = (params.get("arguments") or {}).get("text", "")
            reply(rid, {
                "content": [{"type": "text", "text": text}],
                "isError": False,
            })
        elif method == "ping":
            reply(rid, {})
        else:
            sys.stdout.write(json.dumps({
                "jsonrpc": "2.0", "id": rid,
                "error": {"code": -32601, "message": "method not found"},
            }) + "\n")
            sys.stdout.flush()


if __name__ == "__main__":
    main()
