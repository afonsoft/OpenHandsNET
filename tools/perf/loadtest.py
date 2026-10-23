#!/usr/bin/env python3
"""Async load-test client for the OpenHandsNET API (SPEC-93).

Scenarios:
  rps           weighted mix of hot endpoints at --workers concurrency
                for --duration seconds
  conversations --users x --convs-per-user x --rounds, each conv with an
                active run (POST start -> SignalR attach -> terminal
                state) measuring TTFT of the first streamed event
  subagents     --fanout conversations fire `__task__N` prompts, counting
                spawned vs completed child runs; orphans = never terminal

Report: effective rps, p50/p90/p95/p99 latency per route, TTFT of the
stream, tokens/s, error counts by class (timeout/5xx/4xx). `--out file`
dumps the same data as JSON for run-to-run comparison.

Requires: aiohttp. SignalR attach uses aiohttp's WebSocket client with
the JSON hub protocol (0x1e record separator) — no extra dependency.
"""
import argparse
import asyncio
import json
import statistics
import time
from collections import Counter, defaultdict

import aiohttp

SEP = "\x1e"  # SignalR JSON record separator


# ---------- metrics ----------

class Stats:
    def __init__(self) -> None:
        self.lat = defaultdict(list)          # route -> [ms]
        self.errors = Counter()               # class -> count
        self.ttft = []                        # ms until 1st hub event
        self.tokens = 0
        self.requests = 0
        self.started = time.monotonic()

    def rec(self, route: str, ms: float) -> None:
        self.lat[route].append(ms)
        self.requests += 1

    def err(self, cls: str) -> None:
        self.errors[cls] += 1

    @staticmethod
    def _pct(values: list[float], p: float) -> float:
        if not values:
            return 0.0
        k = max(0, min(len(values) - 1,
                       round((p / 100) * (len(values) - 1))))
        return sorted(values)[k]

    def report(self) -> dict:
        elapsed = time.monotonic() - self.started
        routes = {
            r: {"n": len(v), "p50": round(self._pct(v, 50), 1),
                "p90": round(self._pct(v, 90), 1),
                "p95": round(self._pct(v, 95), 1),
                "p99": round(self._pct(v, 99), 1)}
            for r, v in sorted(self.lat.items())}
        return {
            "elapsed_s": round(elapsed, 1),
            "requests": self.requests,
            "rps": round(self.requests / max(elapsed, 1e-6), 1),
            "routes": routes,
            "ttft_ms": {"n": len(self.ttft),
                        "p50": round(self._pct(self.ttft, 50), 1),
                        "p95": round(self._pct(self.ttft, 95), 1)},
            "tokens_per_s": round(self.tokens / max(elapsed, 1e-6), 1),
            "errors": dict(self.errors),
        }


def err_class(exc: BaseException | None, status: int = 0) -> str:
    if exc is not None:
        if isinstance(exc, (asyncio.TimeoutError, TimeoutError)):
            return "timeout"
        return type(exc).__name__
    return "5xx" if status >= 500 else "4xx"


# ---------- SignalR (minimal JSON protocol client) ----------

async def hub_connect(base: str, token: str, session):
    """POST negotiate -> ws connect -> protocol handshake.
    Returns (ws, t_connect_seconds) or raises."""
    t0 = time.monotonic()
    async with session.post(
            f"{base}/hubs/conversations/negotiate?negotiateVersion=1",
            headers={"Authorization": f"Bearer {token}"}
            if token else {}) as r:
        r.raise_for_status()
        neg = await r.json()
    ws_url = neg["url"].replace("https://", "wss://")
    if ws_url.startswith("http://"):
        ws_url = "ws" + ws_url[4:]  # NOSONAR — loopback test client only
    ws_url += ("&" if "?" in ws_url else "?") + "id=" + neg["connectionToken"]
    ws = await session.ws_connect(ws_url)
    await ws.send_str(json.dumps(
        {"protocol": "json", "version": 1}) + SEP)
    return ws, time.monotonic() - t0


async def hub_first_event(ws, timeout_s: float) -> tuple[float, int]:
    """Wait for 1st InvocationMessage. Returns (ttft_s, n_events_seen)."""
    t0 = time.monotonic()
    n = 0
    while True:
        msg = await asyncio.wait_for(ws.receive(), timeout_s)
        if msg.type in (aiohttp.WSMsgType.CLOSED,
                        aiohttp.WSMsgType.ERROR):
            raise RuntimeError("hub closed")
        if msg.type != aiohttp.WSMsgType.TEXT:
            continue
        for frame in msg.data.split(SEP):
            if not frame:
                continue
            try:
                env = json.loads(frame)
            except json.JSONDecodeError:
                continue
            if env.get("type") == 1:          # Invocation
                n += 1
                return time.monotonic() - t0, n
            if env.get("type") == 6:          # Ping — keep waiting
                continue


# ---------- HTTP helpers ----------

def hdrs(token: str) -> dict:
    return {"Authorization": f"Bearer {token}"} if token else {}


async def timed_get(session, base, path, token, stats):
    t0 = time.monotonic()
    try:
        async with session.get(base + path,
                               headers=hdrs(token),
                               timeout=aiohttp.ClientTimeout(total=15)) as r:
            await r.read()
            ms = (time.monotonic() - t0) * 1000
            if r.status >= 400:
                stats.err(err_class(None, r.status))
            stats.rec(f"GET {path}", ms)
            return r.status
    except Exception as e:                     # noqa: BLE001
        stats.err(err_class(e))
        stats.rec(f"GET {path}", (time.monotonic() - t0) * 1000)
        return 0


# ---------- scenario: rps ----------

RPS_MIX = [
    ("/api/v1/readiness", 3),
    ("/api/v1/config", 2),
    ("/api/v1/models", 2),
    ("/api/v1/app-conversations", 2),
    ("/api/v1/app-conversations/count", 1),
    ("/api/v1/app-conversations/search?q=load", 1),
]


async def _w(session, base, pool, token, stats, i, deadline):
    j = i
    while time.monotonic() < deadline:
        await timed_get(session, base, pool[j % len(pool)], token, stats)
        j += 1


# ---------- scenario: conversations ----------

async def _start_conv(session, base, token, prompt):
    async with session.post(
            base + "/api/v1/app-conversations",
            json={"prompt": prompt, "selected_repository": "loadtest"},
            headers=hdrs(token)) as r:
        if r.status >= 400:
            raise RuntimeError(f"start {r.status}")
        return await r.json()


async def _conv_run(session, base, token, args, stats, sem):
    async with sem:
        t0 = time.monotonic()
        try:
            await _start_conv(
                session, base, token, f"__words__{args.words}")
            ws, _ = await hub_connect(base, token, session)
            try:
                ttft, _ = await hub_first_event(ws, args.timeout)
                stats.ttft.append(ttft * 1000)
            finally:
                await ws.close()
            stats.tokens += args.words
            stats.rec("conversation-run",
                      (time.monotonic() - t0) * 1000)
        except Exception as e:                       # noqa: BLE001
            stats.err(err_class(e))


async def scenario_conversations(args, base, token, stats):
    total = args.users * args.convs_per_user * args.rounds
    sem = asyncio.Semaphore(args.workers)
    async with aiohttp.ClientSession() as session:
        await asyncio.gather(*(
            _conv_run(session, base, token, args, stats, sem)
            for _ in range(total)))


# ---------- scenario: subagents ----------

async def _count_done(session, base, token, cid, want, timeout_s):
    """Poll /delegations até `want` filhos terminais ou timeout."""
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        async with session.get(
                f"{base}/api/v1/app-conversations/{cid}/delegations",
                headers=hdrs(token)) as r:
            if r.status == 200:
                items = await r.json()
                if isinstance(items, dict):
                    items = items.get("items", [])
                done = sum(1 for d in items
                           if d.get("status") in
                           ("completed", "failed", "done"))
                if done >= want:
                    return done
        await asyncio.sleep(0.5)
    return 0


async def _sub_run(session, base, token, args, stats, sem, counts):
    async with sem:
        try:
            conv = await _start_conv(
                session, base, token, f"__task__{args.fanout} fan out")
            cid = conv.get("conversation_id") or conv.get("id")
            counts["spawned"] += args.fanout
            got = await _count_done(session, base, token, cid,
                                    args.fanout, args.timeout)
            counts["completed"] += got
            counts["orphans"] += max(0, args.fanout - got)
            stats.rec("subagent-fanout", args.fanout * 1000.0)
        except Exception as e:                       # noqa: BLE001
            stats.err(err_class(e))


async def scenario_subagents(args, base, token, stats):
    counts = {"spawned": 0, "completed": 0, "orphans": 0}
    sem = asyncio.Semaphore(args.workers)
    async with aiohttp.ClientSession() as session:
        await asyncio.gather(*(
            _sub_run(session, base, token, args, stats, sem, counts)
            for _ in range(args.fanout_conv)))
    print(f"subagents: spawned={counts['spawned']} "
          f"completed={counts['completed']} orphans={counts['orphans']}")


# ---------- main ----------

def main() -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("scenario", choices=["rps", "conversations", "subagents"])
    p.add_argument("--base", default="http://127.0.0.1:8080")
    p.add_argument("--token", default="")
    p.add_argument("--workers", type=int, default=8)
    p.add_argument("--duration", type=float, default=30)
    p.add_argument("--users", type=int, default=2)
    p.add_argument("--convs-per-user", type=int, default=2)
    p.add_argument("--rounds", type=int, default=1)
    p.add_argument("--fanout", type=int, default=3)
    p.add_argument("--fanout-conv", type=int, default=4)
    p.add_argument("--words", type=int, default=20)
    p.add_argument("--timeout", type=float, default=30)
    p.add_argument("--out", default="")
    args = p.parse_args()

    stats = Stats()
    fn = {"rps": _run_rps, "conversations": scenario_conversations,
          "subagents": scenario_subagents}[args.scenario]
    asyncio.run(_dispatch(fn, args, stats))
    rep = stats.report()
    print(json.dumps(rep, indent=2))
    if args.out:
        with open(args.out, "w") as f:
            json.dump(rep, f, indent=2)
        print(f"report -> {args.out}")
    return 0 if not stats.errors else 1


async def _dispatch(fn, args, stats):
    if fn is _run_rps:
        pool = [p for p, w in RPS_MIX for _ in range(w)]
        deadline = time.monotonic() + args.duration
        async with aiohttp.ClientSession() as session:
            await asyncio.gather(*(
                _w(session, args.base, pool, args.token, stats, i, deadline)
                for i in range(args.workers)))
        return
    await fn(args, args.base, args.token, stats)


_run_rps = object()  # sentinel — handled inline in _dispatch

if __name__ == "__main__":
    raise SystemExit(main())
