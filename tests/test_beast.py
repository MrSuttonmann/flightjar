"""Tests for the BEAST wire-format parser."""

from app.beast import BEAST_ESC, parse_one

MSG_LONG = bytes.fromhex("8d406b902015a678d4d220aa4bda")  # 14 bytes
MSG_SHORT = bytes.fromhex("5d4ca2d158c901")  # 7 bytes
MSG_AC = bytes.fromhex("2000")  # 2 bytes


def _frame(type_byte: int, ts: bytes, sig: int, msg: bytes) -> bytes:
    """Build an unescaped frame (caller is responsible for any escaping)."""
    return bytes([BEAST_ESC, type_byte]) + ts + bytes([sig]) + msg


def test_parses_mode_s_long_frame():
    ts = bytes.fromhex("0102030405aa")
    frame = _frame(0x33, ts, 0x64, MSG_LONG)
    consumed, parsed = parse_one(bytearray(frame))
    assert consumed == len(frame)
    type_name, mlat, sig, msg = parsed
    assert type_name == "mode_s_long"
    assert mlat == int.from_bytes(ts, "big")
    assert sig == 0x64
    assert msg == MSG_LONG


def test_parses_mode_s_short_frame():
    frame = _frame(0x32, bytes(6), 0x50, MSG_SHORT)
    consumed, parsed = parse_one(bytearray(frame))
    assert consumed == len(frame)
    assert parsed[0] == "mode_s_short"
    assert parsed[3] == MSG_SHORT


def test_parses_mode_ac_frame():
    frame = _frame(0x31, bytes(6), 0x20, MSG_AC)
    _, parsed = parse_one(bytearray(frame))
    assert parsed[0] == "mode_ac"
    assert parsed[3] == MSG_AC


def test_empty_buffer_requests_more_data():
    consumed, parsed = parse_one(bytearray())
    assert (consumed, parsed) == (0, None)


def test_partial_frame_requests_more_data_without_consuming():
    frame = _frame(0x33, bytes(6), 0x00, MSG_LONG)
    consumed, parsed = parse_one(bytearray(frame[:-1]))
    assert consumed == 0
    assert parsed is None


def test_resyncs_to_next_escape_byte():
    garbage = b"\x00\x01\x02"
    buf = bytearray(garbage + b"\x1a\x33")
    consumed, parsed = parse_one(buf)
    assert consumed == len(garbage)
    assert parsed is None


def test_discards_entire_buffer_with_no_escape():
    buf = bytearray(b"\x00\x01\x02")
    consumed, parsed = parse_one(buf)
    assert consumed == len(buf)
    assert parsed is None


def test_bad_type_byte_drops_single_escape():
    buf = bytearray(b"\x1a\xff")
    consumed, parsed = parse_one(buf)
    assert consumed == 1
    assert parsed is None


def test_escaped_0x1a_in_body_is_unescaped():
    # Put an 0x1a in the message body; encode as 0x1a 0x1a on the wire.
    msg = bytearray(MSG_LONG)
    msg[3] = BEAST_ESC
    msg = bytes(msg)
    raw_body = bytes(6) + bytes([0x50]) + msg
    encoded_body = raw_body.replace(b"\x1a", b"\x1a\x1a")
    frame = bytes([BEAST_ESC, 0x33]) + encoded_body
    consumed, parsed = parse_one(bytearray(frame))
    assert consumed == len(frame)
    assert parsed[3] == msg


def test_unescaped_0x1a_inside_body_triggers_resync():
    # Frame header + a partial body that contains 0x1a followed by a non-0x1a
    # byte — that's an invalid escape, so the parser should resync from the
    # stray 0x1a rather than eat it as data.
    body = bytes(6) + bytes([0x50]) + b"\xaa\x1a\x00"
    buf = bytearray(bytes([BEAST_ESC, 0x33]) + body)
    consumed, parsed = parse_one(buf)
    assert parsed is None
    assert consumed > 0
    # The parser positions to the stray 0x1a so the next pass can re-evaluate.
    assert buf[consumed] == BEAST_ESC


def test_escape_at_buffer_boundary_requests_more_data():
    # Frame where the body ends mid-escape (0x1a with no next byte yet).
    # Build a frame that places an 0x1a as the last visible byte.
    msg = bytearray(MSG_LONG)
    msg[0] = BEAST_ESC
    # Encoded body up to the point where the first 0x1a's pair would be the
    # very next byte we haven't received yet.
    raw_body = bytes(6) + bytes([0x50]) + bytes(msg)
    encoded_body = raw_body.replace(b"\x1a", b"\x1a\x1a")
    frame = bytes([BEAST_ESC, 0x33]) + encoded_body
    # Truncate to right after the first 0x1a of the escape pair inside the body.
    # That's: 2 header + 7 ts/sig + 1 (first msg byte, which is 0x1a) = index 10.
    consumed, parsed = parse_one(bytearray(frame[:10]))
    assert (consumed, parsed) == (0, None)
