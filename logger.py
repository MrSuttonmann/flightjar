#!/usr/bin/env python3
"""Log BEAST messages from a readsb/dump1090 source (as used by tar1090).

Connects to a BEAST TCP port, parses frames, optionally decodes them with
pyModeS, and writes one JSON object per line to stdout and/or a rotating file.
"""

import argparse
import json
import logging
import os
import signal
import socket
import sys
import time
from datetime import datetime, timezone
from logging.handlers import TimedRotatingFileHandler

try:
    import pyModeS as pms
    HAVE_PMS = True
except ImportError:
    HAVE_PMS = False


BEAST_ESC = 0x1A

# Mode AC is 2 bytes, Mode S short is 7 bytes, Mode S long is 14 bytes.
# Each frame is: 0x1A <type> <6-byte MLAT timestamp> <1-byte signal> <message>
FRAME_TYPES = {
    0x31: ("mode_ac", 2),
    0x32: ("mode_s_short", 7),
    0x33: ("mode_s_long", 14),
}

log = logging.getLogger("beast")


def parse_one(buf: bytearray):
    """Parse one frame from the start of buf.

    Returns (bytes_consumed, frame_or_None).
      - bytes_consumed == 0 means we need more data and should not drop anything.
      - frame_or_None is (type_name, mlat_ticks, signal, msg_bytes) or None if
        the consumed bytes were garbage/resync.
    """
    if not buf:
        return 0, None

    # If we're not sitting on a frame start, discard up to the next 0x1A.
    if buf[0] != BEAST_ESC:
        nxt = buf.find(b"\x1a", 1)
        if nxt < 0:
            return len(buf), None
        return nxt, None

    if len(buf) < 2:
        return 0, None

    tinfo = FRAME_TYPES.get(buf[1])
    if tinfo is None:
        # Bad type byte; drop the 0x1A and try to resync on the next one.
        return 1, None

    type_name, msglen = tinfo
    body_needed = 6 + 1 + msglen  # timestamp + signal + message

    # Walk the bytes after the type byte, unescaping 0x1A 0x1A -> 0x1A.
    i = 2
    body = bytearray()
    while len(body) < body_needed:
        if i >= len(buf):
            return 0, None  # need more bytes
        b = buf[i]
        if b == BEAST_ESC:
            if i + 1 >= len(buf):
                return 0, None
            if buf[i + 1] == BEAST_ESC:
                body.append(BEAST_ESC)
                i += 2
            else:
                # Unescaped 0x1A inside a frame = truncated frame; resync here.
                return i, None
        else:
            body.append(b)
            i += 1

    mlat_ticks = int.from_bytes(body[:6], "big")
    sig = body[6]
    msg = bytes(body[7 : 7 + msglen])
    return i, (type_name, mlat_ticks, sig, msg)


def iter_frames(sock):
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


def decode(hex_msg: str, type_name: str) -> dict:
    """Best-effort decode with pyModeS. Position needs CPR pairs so we skip it."""
    if not HAVE_PMS or type_name not in ("mode_s_short", "mode_s_long"):
        return {}
    out: dict = {}
    try:
        df = pms.df(hex_msg)
    except Exception:
        return {}
    out["df"] = df
    try:
        if df in (17, 18):
            if pms.crc(hex_msg) != 0:
                out["crc_ok"] = False
                return out
            out["crc_ok"] = True
            out["icao"] = pms.adsb.icao(hex_msg)
            tc = pms.adsb.typecode(hex_msg)
            out["tc"] = tc
            if 1 <= tc <= 4:
                try:
                    out["callsign"] = pms.adsb.callsign(hex_msg).rstrip("_ ").strip()
                except Exception:
                    pass
            elif 9 <= tc <= 18:
                try:
                    out["altitude_ft"] = pms.adsb.altitude(hex_msg)
                except Exception:
                    pass
            elif tc == 19:
                try:
                    v = pms.adsb.velocity(hex_msg)
                    if v:
                        out["velocity"] = {
                            "speed": v[0],
                            "heading": v[1],
                            "vrate": v[2],
                            "type": v[3],
                        }
                except Exception:
                    pass
        elif df in (4, 20):
            try:
                out["icao"] = pms.common.icao(hex_msg)
                out["altitude_ft"] = pms.common.altcode(hex_msg)
            except Exception:
                pass
        elif df in (5, 21):
            try:
                out["icao"] = pms.common.icao(hex_msg)
                out["squawk"] = pms.common.idcode(hex_msg)
            except Exception:
                pass
        elif df == 11:
            try:
                out["icao"] = pms.common.icao(hex_msg)
            except Exception:
                pass
    except Exception as e:
        out["decode_error"] = str(e)
    return out


def connect_forever(host: str, port: int, on_frame, backoff_max: int = 30):
    backoff = 1
    while True:
        try:
            log.info("connecting to %s:%d", host, port)
            with socket.create_connection((host, port), timeout=15) as sock:
                sock.settimeout(60)
                log.info("connected")
                backoff = 1
                for frame in iter_frames(sock):
                    on_frame(frame)
                log.warning("remote closed the stream")
        except (OSError, socket.timeout) as e:
            log.warning("connection error: %s", e)
        time.sleep(backoff)
        backoff = min(backoff * 2, backoff_max)


def env_bool(name: str, default: str) -> bool:
    return os.environ.get(name, default).lower() in ("1", "true", "yes", "on")


def main():
    p = argparse.ArgumentParser(description="Log BEAST messages as JSONL.")
    p.add_argument("--host", default=os.environ.get("BEAST_HOST", "readsb"))
    p.add_argument("--port", type=int,
                   default=int(os.environ.get("BEAST_PORT", "30005")))
    p.add_argument("--outfile",
                   default=os.environ.get("BEAST_OUTFILE", "/data/beast.jsonl"))
    p.add_argument("--no-stdout", action="store_true",
                   default=not env_bool("BEAST_STDOUT", "1"),
                   help="do not mirror JSONL to stdout")
    p.add_argument("--no-decode", action="store_true",
                   default=env_bool("BEAST_NO_DECODE", "0"),
                   help="skip pyModeS decoding, raw hex only")
    p.add_argument("--rotate",
                   default=os.environ.get("BEAST_ROTATE", "daily"),
                   choices=["none", "hourly", "daily"])
    p.add_argument("--rotate-keep", type=int,
                   default=int(os.environ.get("BEAST_ROTATE_KEEP", "14")))
    args = p.parse_args()

    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s",
        stream=sys.stderr,
    )

    writers = []
    if args.outfile:
        d = os.path.dirname(args.outfile)
        if d:
            os.makedirs(d, exist_ok=True)
        if args.rotate == "none":
            fh = open(args.outfile, "a", buffering=1)
            writers.append(lambda s, fh=fh: fh.write(s + "\n"))
        else:
            when = "H" if args.rotate == "hourly" else "midnight"
            rot = TimedRotatingFileHandler(
                args.outfile, when=when, backupCount=args.rotate_keep, utc=True,
            )
            rot.setFormatter(logging.Formatter("%(message)s"))
            rot_logger = logging.getLogger("beast.jsonl")
            rot_logger.setLevel(logging.INFO)
            rot_logger.propagate = False
            rot_logger.addHandler(rot)
            writers.append(rot_logger.info)

    if not args.no_stdout:
        writers.append(lambda s: print(s, flush=True))

    if not writers:
        log.error("no writers configured (both file and stdout disabled)")
        sys.exit(2)

    decode_enabled = not args.no_decode and HAVE_PMS
    if not decode_enabled:
        log.info("decoding disabled (pyModeS=%s, no_decode=%s)",
                 HAVE_PMS, args.no_decode)

    counter = {"n": 0}
    last_report = time.monotonic()

    def on_frame(frame):
        nonlocal last_report
        type_name, mlat_ticks, sig, msg = frame
        hex_msg = msg.hex()
        record = {
            "ts_rx": datetime.now(timezone.utc).isoformat(),
            "mlat_ticks": mlat_ticks,
            "type": type_name,
            "signal": sig,
            "hex": hex_msg,
        }
        if decode_enabled:
            d = decode(hex_msg, type_name)
            if d:
                record["decoded"] = d
        line = json.dumps(record, separators=(",", ":"))
        for w in writers:
            try:
                w(line)
            except Exception as e:
                log.error("writer failure: %s", e)
        counter["n"] += 1
        now = time.monotonic()
        if now - last_report > 30:
            log.info("%d messages in last %.0fs", counter["n"], now - last_report)
            counter["n"] = 0
            last_report = now

    def handle_sig(signum, _frame):
        log.info("signal %d received, exiting", signum)
        sys.exit(0)

    signal.signal(signal.SIGINT, handle_sig)
    signal.signal(signal.SIGTERM, handle_sig)

    connect_forever(args.host, args.port, on_frame)


if __name__ == "__main__":
    main()
