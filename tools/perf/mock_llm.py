#!/usr/bin/env python3
"""Mock OpenAI-compatible LLM server for OpenHandsNET load/perf tests.

SPEC-93 RF-001/RF-002/RF-006 — deterministic stdlib+aiohttp server that
speaks the subset consumed by OpenAiCompatLlmProvider:

  GET  /v1/models            -> {"object":"list","data":[mock-model]}
  POST /v1/chat/completions  -> SSE stream (stream:true) or single JSON
  POST /v1/embeddings        -> {"object":"list","data":[{embedding}]}

Behavior triggers (looked up in the LAST role=="user" message):
  __task__N    -> N tool_calls of the `task` builtin (background=true —
                subagent fan-out analog to wait=false)
  __words__N   -> N delta.content chunks in the stream
  __sleep__MS  -> MS milliseconds of latency before the 1st token
  __finish__   -> single tool_call of the `finish` builtin (ends the run)

Tool-result turn: when the last message has role=="tool" the mock answers
plain text so the agent loop terminates. Same input -> same output (seed
fixed per request id).

Bind: 127.0.0.1 only (RF-006). Requires: aiohttp.
"""
import asyncio
import json
import re
import time
import uuid

from aiohttp import web

MODEL = "mock-fast"
EMB = [0.01 * i for i in range(64)]

TASK_RE = re.compile(r"__task__(\d+)")
WORDS_RE = re.compile(r"__words__(\d+)")
SLEEP_RE = re.compile(r"__sleep__(\d+)")
FINISH_RE = re.compile(r"__finish__")


def _chunk(delta: dict, finish: str | None = None,
           usage: dict | None = None, rid: str = "") -> dict:
    chunk = {
        "id": rid,
        "object": "chat.completion.chunk",
        "created": int(time.time()),
        "model": MODEL,
        "choices": [{
            "index": 0,
            "delta": delta,
            "finish_reason": finish,
        }] if usage is None else [],
    }
    if usage is not None:
        chunk["usage"] = usage
    return chunk


def _tool_call(idx: int, name: str, args: dict, rid: str) -> dict:
    return {
        "index": idx,
        "id": f"call-{rid}-{idx}",
        "type": "function",
        # OpenAiCompatLlmProvider lê arguments como *string* JSON
        "function": {"name": name, "arguments": json.dumps(args)},
    }


def _plan(messages: list, rid: str):
    """Deterministic plan -> (kind, payload, delay_ms)."""
    last_user = ""
    for m in reversed(messages):
        if m.get("role") == "user":
            last_user = m.get("content") or ""
            break
    if messages and messages[-1].get("role") == "tool":
        return ("text", "Tool result processed. Done.", 0)
    sleep = SLEEP_RE.search(last_user)
    delay = int(sleep.group(1)) if sleep else 0
    task = TASK_RE.search(last_user)
    if task:
        n = min(int(task.group(1)), 8)
        return ("tool_calls", [
            _tool_call(i, "task", {
                "description": f"loadtest subtask {i}",
                "prompt": "Reply with a short confirmation.",
                "subagent_type": "explore",
                "background": True,
            }, rid) for i in range(n)], delay)
    if FINISH_RE.search(last_user):
        return ("tool_calls", [
            _tool_call(0, "finish",
                       {"message": "loadtest done"}, rid)], delay)
    words = WORDS_RE.search(last_user)
    n = int(words.group(1)) if words else 6
    return ("text", " ".join(f"w{i}" for i in range(n)), delay)


async def models(  # NOSONAR — aiohttp exige coroutine handler
        request: web.Request) -> web.Response:
    return web.json_response({"object": "list", "data": [{
        "id": MODEL, "object": "model",
        "created": int(time.time()), "owned_by": "mock"}]})


async def embeddings(request: web.Request) -> web.Response:
    body = await request.json()
    inp = body.get("input", "")
    n = len(inp) if isinstance(inp, list) else 1
    return web.json_response({
        "object": "list",
        "data": [{"object": "embedding", "index": i, "embedding": EMB}
                 for i in range(max(n, 1))],
        "model": MODEL,
        "usage": {"prompt_tokens": n, "total_tokens": n},
    })


async def completions(request: web.Request) -> web.Response:
    body = await request.json()
    messages = body.get("messages", [])
    stream = body.get("stream", True)
    rid = "chatcmpl-" + uuid.uuid4().hex[:12]
    kind, payload, delay = _plan(messages, rid)
    if delay:
        await asyncio.sleep(delay / 1000.0)
    usage = {"prompt_tokens": 24, "completion_tokens": 12,
             "total_tokens": 36}
    print(f"[mock] {request.remote} tools={'y' if kind == 'tool_calls' else 'n'} "
          f"stream={stream} delay={delay}ms", flush=True)

    if not stream:
        msg: dict = {"role": "assistant",
                     "content": payload if kind == "text" else ""}
        if kind == "tool_calls":
            msg["tool_calls"] = payload
        return web.json_response({
            "id": rid, "object": "chat.completion",
            "created": int(time.time()), "model": MODEL,
            "choices": [{"index": 0, "message": msg,
                         "finish_reason": ("tool_calls"
                                           if kind == "tool_calls"
                                           else "stop")}],
            "usage": usage})

    resp = web.StreamResponse(status=200, headers={
        "Content-Type": "text/event-stream",
        "Cache-Control": "no-cache"})
    await resp.prepare(request)

    async def send(obj: dict) -> None:
        await resp.write(f"data: {json.dumps(obj)}\n\n".encode())

    if kind == "tool_calls":
        await send(_chunk({"role": "assistant", "content": ""}, rid=rid))
        for call in payload:
            await send(_chunk({"tool_calls": [call]}, rid=rid))
        await send(_chunk({}, finish="tool_calls", rid=rid))
    else:
        for piece in payload.split(" "):
            await send(_chunk({"content": piece + " "}, rid=rid))
            await asyncio.sleep(0.002)
        await send(_chunk({}, finish="stop", rid=rid))
    await send(_chunk({}, usage=usage, rid=rid))
    await resp.write(b"data: [DONE]\n\n")
    await resp.write_eof()
    return resp


def main() -> None:
    import sys
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 11439
    app = web.Application()
    app.router.add_get("/v1/models", models)
    app.router.add_post("/v1/chat/completions", completions)
    app.router.add_post("/v1/embeddings", embeddings)
    print(f"mock-llm (openai-compat) listening on 127.0.0.1:{port}",
          flush=True)
    web.run_app(app, host="127.0.0.1", port=port, print=None)


if __name__ == "__main__":
    main()
