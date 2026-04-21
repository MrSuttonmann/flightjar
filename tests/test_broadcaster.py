"""Tests for the WebSocket broadcaster.

The snapshot pusher fans payloads out to every connected client on every
tick. Historically the loop was serial, so one wedged client stalled the
1 Hz cadence for everyone else — these tests pin the parallel fan-out
and dead-client reaping behaviour that the fix introduced.
"""

import asyncio

from app.main import Broadcaster


class _FakeWs:
    def __init__(self, *, raises: BaseException | None = None, delay: float = 0.0):
        self.calls: list[str] = []
        self._raises = raises
        self._delay = delay

    async def send_text(self, payload: str) -> None:
        if self._delay:
            await asyncio.sleep(self._delay)
        self.calls.append(payload)
        if self._raises is not None:
            raise self._raises


def test_broadcast_noop_with_no_clients():
    b = Broadcaster()
    # Should not raise or hang.
    asyncio.run(b.broadcast("{}"))


def test_broadcast_delivers_to_every_client():
    b = Broadcaster()
    a, c = _FakeWs(), _FakeWs()
    b.add(a)  # type: ignore[arg-type]
    b.add(c)  # type: ignore[arg-type]
    asyncio.run(b.broadcast('{"x":1}'))
    assert a.calls == ['{"x":1}']
    assert c.calls == ['{"x":1}']


def test_broadcast_reaps_clients_whose_send_raised():
    b = Broadcaster()
    good = _FakeWs()
    dead = _FakeWs(raises=RuntimeError("bad pipe"))
    b.add(good)  # type: ignore[arg-type]
    b.add(dead)  # type: ignore[arg-type]
    asyncio.run(b.broadcast("{}"))
    assert good in b.clients
    assert dead not in b.clients


def test_broadcast_runs_clients_in_parallel():
    """A slow client must not delay its peers — they're awaited together."""
    b = Broadcaster()
    order: list[str] = []

    class _Tagged(_FakeWs):
        def __init__(self, tag: str, delay: float = 0.0):
            super().__init__(delay=delay)
            self.tag = tag

        async def send_text(self, payload: str) -> None:
            if self._delay:
                await asyncio.sleep(self._delay)
            order.append(self.tag)

    slow = _Tagged("slow", delay=0.05)
    fast = _Tagged("fast")
    b.add(slow)  # type: ignore[arg-type]
    b.add(fast)  # type: ignore[arg-type]
    asyncio.run(b.broadcast("{}"))
    # Parallel fan-out: the fast client finishes before the slow one.
    # A sequential implementation (iterating in set-insertion order)
    # could not guarantee this ordering.
    assert order == ["fast", "slow"]


def test_broadcast_tolerates_all_clients_failing():
    b = Broadcaster()
    bad1 = _FakeWs(raises=RuntimeError("x"))
    bad2 = _FakeWs(raises=RuntimeError("y"))
    b.add(bad1)  # type: ignore[arg-type]
    b.add(bad2)  # type: ignore[arg-type]
    # Doesn't raise; both reaped.
    asyncio.run(b.broadcast("{}"))
    assert b.clients == set()
