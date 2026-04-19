"""BEAST wire-format parser.

A BEAST frame is:
    0x1A <type> <6-byte MLAT timestamp> <1-byte signal> <message>

`type` is 0x31 (Mode A/C, 2-byte msg), 0x32 (Mode S short, 7-byte msg) or
0x33 (Mode S long, 14-byte msg). Any 0x1A byte inside the timestamp/signal/
message is escaped to 0x1A 0x1A on the wire.
"""

import asyncio

BEAST_ESC = 0x1A

FRAME_TYPES = {
    0x31: ("mode_ac", 2),
    0x32: ("mode_s_short", 7),
    0x33: ("mode_s_long", 14),
}


def parse_one(buf: bytearray):
    """Parse one frame from the start of `buf`.

    Returns (bytes_consumed, frame_or_None).
      - bytes_consumed == 0 means we need more data; do not drop anything.
      - frame_or_None is (type_name, mlat_ticks, signal, msg_bytes), or None
        when the consumed bytes were garbage (resync).
    """
    if not buf:
        return 0, None

    if buf[0] != BEAST_ESC:
        nxt = buf.find(b"\x1a", 1)
        if nxt < 0:
            return len(buf), None
        return nxt, None

    if len(buf) < 2:
        return 0, None

    tinfo = FRAME_TYPES.get(buf[1])
    if tinfo is None:
        return 1, None  # bad type byte, drop the 0x1A and resync

    type_name, msglen = tinfo
    body_needed = 6 + 1 + msglen

    i = 2
    body = bytearray()
    while len(body) < body_needed:
        if i >= len(buf):
            return 0, None
        b = buf[i]
        if b == BEAST_ESC:
            if i + 1 >= len(buf):
                return 0, None
            if buf[i + 1] == BEAST_ESC:
                body.append(BEAST_ESC)
                i += 2
            else:
                # Unescaped 0x1A inside a frame = truncated; resync from here.
                return i, None
        else:
            body.append(b)
            i += 1

    mlat_ticks = int.from_bytes(body[:6], "big")
    sig = body[6]
    msg = bytes(body[7 : 7 + msglen])
    return i, (type_name, mlat_ticks, sig, msg)


async def iter_frames(reader: asyncio.StreamReader):
    """Async generator yielding parsed BEAST frames from a socket reader."""
    buf = bytearray()
    while True:
        chunk = await reader.read(8192)
        if not chunk:
            return
        buf.extend(chunk)
        while True:
            consumed, frame = parse_one(buf)
            if consumed == 0:
                break
            del buf[:consumed]
            if frame is not None:
                yield frame


def iter_frames_sync(sock):
    """Sync generator yielding parsed BEAST frames from a blocking socket.

    Mirrors `iter_frames` but uses `sock.recv` instead of an asyncio reader;
    lets the standalone CLI share the same parse_one state machine.
    """
    buf = bytearray()
    while True:
        chunk = sock.recv(8192)
        if not chunk:
            return
        buf.extend(chunk)
        while True:
            consumed, frame = parse_one(buf)
            if consumed == 0:
                break
            del buf[:consumed]
            if frame is not None:
                yield frame
