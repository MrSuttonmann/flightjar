namespace FlightJar.Decoder.ModeS;

/// <summary>
/// Mode S Comm-B (DF 20 / 21) payload decoding for the meteorological and
/// intention registers: BDS 4,0 (selected vertical intention + QNH), BDS 4,4
/// (meteorological routine — wind, static air temperature), BDS 5,0 (track and
/// turn — true airspeed, true track, ground speed, roll), BDS 6,0 (heading and
/// speed — magnetic heading, indicated airspeed, Mach).
///
/// Ported from pyModeS 3.x (<c>pyModeS/decoder/bds/</c>), which is MIT-licensed.
/// Unlike BDS 1,0 / 2,0 / 3,0 these registers carry no explicit format
/// identifier in the payload; callers have to infer the register using the
/// <c>Is-</c> validators, which apply reserved-bit / status-bit / range checks
/// that reject implausible interpretations.
///
/// All methods operate on the 56-bit MB (message B) payload of a DF 20/21
/// long-format Mode S reply as an unsigned 64-bit int (top 8 bits zero, bits
/// 0-55 MSB-first mirror the pyModeS bit indexing: <c>(payload &gt;&gt; (55 - i)) &amp; 1</c>).
/// </summary>
public static class CommB
{
    /// <summary>Extract the 56-bit MB payload from a DF 20/21 long-format message.</summary>
    public static ulong Payload(HexMessage msg)
    {
        if (msg.TotalBits != 112)
        {
            return 0;
        }
        // MB field is 56 bits starting at bit 32 of the 112-bit reply.
        return ModeSBits.ExtractUnsigned(msg.Bits, 32, 56, 112);
    }

    // ----- BDS 4,0 — Selected Vertical Intention + QNH -----------------------

    /// <summary>
    /// Plausibility check for BDS 4,0. Enforces status/value consistency on the
    /// five status-gated fields plus the two reserved blocks (bits 39-46 and
    /// 51-52) that must be zero.
    /// </summary>
    public static bool IsBds40(ulong payload)
    {
        if (payload == 0)
        {
            return false;
        }
        if (WrongStatus(payload, 0, 1, 12)) return false;   // MCP alt
        if (WrongStatus(payload, 13, 14, 12)) return false; // FMS alt
        if (WrongStatus(payload, 26, 27, 12)) return false; // QNH
        if (WrongStatus(payload, 47, 48, 3)) return false;  // MCP mode bits
        if (WrongStatus(payload, 53, 54, 2)) return false;  // target alt source
        // Reserved bits 39-46 (8 bits).
        if (((payload >> (55 - 46)) & 0xFF) != 0) return false;
        // Reserved bits 51-52 (2 bits).
        return ((payload >> (55 - 52)) & 0x3) == 0;
    }

    public readonly record struct Bds40Data(
        int? SelectedAltitudeMcpFt,
        int? SelectedAltitudeFmsFt,
        double? BaroPressureSettingHpa);

    public static Bds40Data DecodeBds40(ulong payload)
    {
        int? mcp = null;
        if (((payload >> (55 - 0)) & 0x1) != 0)
        {
            mcp = (int)((payload >> (55 - 12)) & 0xFFF) * 16;
        }
        int? fms = null;
        if (((payload >> (55 - 13)) & 0x1) != 0)
        {
            fms = (int)((payload >> (55 - 25)) & 0xFFF) * 16;
        }
        double? qnh = null;
        if (((payload >> (55 - 26)) & 0x1) != 0)
        {
            qnh = ((payload >> (55 - 38)) & 0xFFF) * 0.1 + 800.0;
        }
        return new Bds40Data(mcp, fms, qnh);
    }

    // ----- BDS 4,4 — Meteorological Routine Air Report -----------------------

    /// <summary>
    /// Plausibility check for BDS 4,4 (MRAR). Enforces FOM ≤ 4, wind-status set,
    /// status/value consistency on pressure/turbulence/humidity, wind speed in
    /// [0, 250] kt, temperature in [-80, +60] °C, and rejects the all-zero
    /// meteorological payload (wind + direction + temperature all zero).
    /// </summary>
    public static bool IsBds44(ulong payload)
    {
        if (payload == 0)
        {
            return false;
        }
        // FOM (bits 0-3) must be 0..4.
        var fom = (int)((payload >> (55 - 3)) & 0xF);
        if (fom > 4) return false;

        // Wind must be present per pyModeS heuristic.
        if (((payload >> (55 - 4)) & 0x1) == 0) return false;

        if (WrongStatus(payload, 34, 35, 11)) return false; // static pressure
        if (WrongStatus(payload, 46, 47, 2)) return false;  // turbulence
        if (WrongStatus(payload, 49, 50, 6)) return false;  // humidity

        var windSpeed = (int)((payload >> (55 - 13)) & 0x1FF);
        var windDirRaw = (int)((payload >> (55 - 22)) & 0x1FF);
        var tempSign = (int)((payload >> (55 - 23)) & 0x1);
        var tempRaw = (int)((payload >> (55 - 33)) & 0x3FF);

        if (windSpeed > 250) return false;

        var tempSigned = tempSign != 0 ? tempRaw - 1024 : tempRaw;
        var tempC = tempSigned * 0.25;
        if (tempC < -80.0 || tempC > 60.0) return false;

        // Reject all-zero met data (wind speed 0, wind dir 0, temp 0).
        return !(windSpeed == 0 && windDirRaw == 0 && tempRaw == 0);
    }

    public readonly record struct Bds44Data(
        int FigureOfMerit,
        int? WindSpeedKt,
        double? WindDirectionDeg,
        double StaticAirTemperatureC,
        int? StaticPressureHpa,
        int? Turbulence,
        double? HumidityPct);

    public static Bds44Data DecodeBds44(ulong payload)
    {
        var fom = (int)((payload >> (55 - 3)) & 0xF);
        int? windSpeed = null;
        double? windDir = null;
        if (((payload >> (55 - 4)) & 0x1) != 0)
        {
            windSpeed = (int)((payload >> (55 - 13)) & 0x1FF);
            windDir = ((payload >> (55 - 22)) & 0x1FF) * (180.0 / 256.0);
        }

        var tempSign = (int)((payload >> (55 - 23)) & 0x1);
        var tempRaw = (int)((payload >> (55 - 33)) & 0x3FF);
        var sat = Signed(tempRaw, 10, tempSign) * 0.25;

        int? staticPressure = null;
        if (((payload >> (55 - 34)) & 0x1) != 0)
        {
            staticPressure = (int)((payload >> (55 - 45)) & 0x7FF);
        }
        int? turbulence = null;
        if (((payload >> (55 - 46)) & 0x1) != 0)
        {
            turbulence = (int)((payload >> (55 - 48)) & 0x3);
        }
        double? humidity = null;
        if (((payload >> (55 - 49)) & 0x1) != 0)
        {
            humidity = ((payload >> (55 - 55)) & 0x3F) * (100.0 / 64.0);
        }
        return new Bds44Data(fom, windSpeed, windDir, sat, staticPressure, turbulence, humidity);
    }

    // ----- BDS 5,0 — Track and Turn Report -----------------------------------

    public static bool IsBds50(ulong payload)
    {
        if (payload == 0) return false;

        if (WrongStatus(payload, 0, 1, 10)) return false;   // roll
        if (WrongStatus(payload, 11, 12, 11)) return false; // track
        if (WrongStatus(payload, 23, 24, 10)) return false; // groundspeed
        if (WrongStatus(payload, 34, 35, 10)) return false; // track rate
        if (WrongStatus(payload, 45, 46, 10)) return false; // true airspeed

        var rollStatus = ((payload >> (55 - 0)) & 0x1) != 0;
        var rollSign = (int)((payload >> (55 - 1)) & 0x1);
        var rollMag = (int)((payload >> (55 - 10)) & 0x1FF);

        var gsStatus = ((payload >> (55 - 23)) & 0x1) != 0;
        var gsRaw = (int)((payload >> (55 - 33)) & 0x3FF);

        var tasStatus = ((payload >> (55 - 45)) & 0x1) != 0;
        var tasRaw = (int)((payload >> (55 - 55)) & 0x3FF);

        if (rollStatus)
        {
            var rollDeg = Signed(rollMag, 9, rollSign) * 45.0 / 256.0;
            if (Math.Abs(rollDeg) > 35.0) return false;
        }
        if (gsStatus && gsRaw * 2 > 600) return false;
        if (tasStatus && tasRaw * 2 > 600) return false;

        return !(gsStatus && tasStatus && Math.Abs(tasRaw * 2 - gsRaw * 2) > 200);
    }

    public readonly record struct Bds50Data(
        double? RollDeg,
        double? TrueTrackDeg,
        int? GroundspeedKt,
        double? TrackRateDegPerS,
        int? TrueAirspeedKt);

    public static Bds50Data DecodeBds50(ulong payload)
    {
        double? roll = null;
        if (((payload >> (55 - 0)) & 0x1) != 0)
        {
            var sign = (int)((payload >> (55 - 1)) & 0x1);
            var mag = (int)((payload >> (55 - 10)) & 0x1FF);
            roll = Signed(mag, 9, sign) * 45.0 / 256.0;
        }
        double? trueTrack = null;
        if (((payload >> (55 - 11)) & 0x1) != 0)
        {
            var sign = (int)((payload >> (55 - 12)) & 0x1);
            var raw = (int)((payload >> (55 - 22)) & 0x3FF);
            var deg = Signed(raw, 10, sign) * 90.0 / 512.0;
            trueTrack = NormaliseAngle(deg);
        }
        int? gs = null;
        if (((payload >> (55 - 23)) & 0x1) != 0)
        {
            gs = (int)((payload >> (55 - 33)) & 0x3FF) * 2;
        }
        double? trackRate = null;
        if (((payload >> (55 - 34)) & 0x1) != 0)
        {
            var sign = (int)((payload >> (55 - 35)) & 0x1);
            var mag = (int)((payload >> (55 - 44)) & 0x1FF);
            trackRate = Signed(mag, 9, sign) * 8.0 / 256.0;
        }
        int? tas = null;
        if (((payload >> (55 - 45)) & 0x1) != 0)
        {
            tas = (int)((payload >> (55 - 55)) & 0x3FF) * 2;
        }
        return new Bds50Data(roll, trueTrack, gs, trackRate, tas);
    }

    // ----- BDS 6,0 — Heading and Speed Report --------------------------------

    public static bool IsBds60(ulong payload)
    {
        if (payload == 0) return false;

        if (WrongStatus(payload, 0, 1, 11)) return false;   // heading
        if (WrongStatus(payload, 12, 13, 10)) return false; // IAS
        if (WrongStatus(payload, 23, 24, 10)) return false; // Mach
        if (WrongStatus(payload, 34, 35, 10)) return false; // baro vr
        if (WrongStatus(payload, 45, 46, 10)) return false; // inertial vr

        var iasStatus = ((payload >> (55 - 12)) & 0x1) != 0;
        var iasRaw = (int)((payload >> (55 - 22)) & 0x3FF);

        var machStatus = ((payload >> (55 - 23)) & 0x1) != 0;
        var machRaw = (int)((payload >> (55 - 33)) & 0x3FF);

        var vrbStatus = ((payload >> (55 - 34)) & 0x1) != 0;
        var vrbSign = (int)((payload >> (55 - 35)) & 0x1);
        var vrbMag = (int)((payload >> (55 - 44)) & 0x1FF);

        var vriStatus = ((payload >> (55 - 45)) & 0x1) != 0;
        var vriSign = (int)((payload >> (55 - 46)) & 0x1);
        var vriMag = (int)((payload >> (55 - 55)) & 0x1FF);

        if (iasStatus && iasRaw > 500) return false;
        if (machStatus && machRaw * 2.048 / 512.0 > 1.0) return false;
        if (vrbStatus && Math.Abs(Signed(vrbMag, 9, vrbSign) * 32) > 6000) return false;

        return !(vriStatus && Math.Abs(Signed(vriMag, 9, vriSign) * 32) > 6000);
    }

    public readonly record struct Bds60Data(
        double? MagneticHeadingDeg,
        int? IndicatedAirspeedKt,
        double? Mach,
        int? BaroVerticalRateFpm,
        int? InertialVerticalRateFpm);

    public static Bds60Data DecodeBds60(ulong payload)
    {
        double? heading = null;
        if (((payload >> (55 - 0)) & 0x1) != 0)
        {
            var sign = (int)((payload >> (55 - 1)) & 0x1);
            var raw = (int)((payload >> (55 - 11)) & 0x3FF);
            var deg = Signed(raw, 10, sign) * 90.0 / 512.0;
            heading = NormaliseAngle(deg);
        }
        int? ias = null;
        if (((payload >> (55 - 12)) & 0x1) != 0)
        {
            ias = (int)((payload >> (55 - 22)) & 0x3FF);
        }
        double? mach = null;
        if (((payload >> (55 - 23)) & 0x1) != 0)
        {
            mach = ((payload >> (55 - 33)) & 0x3FF) * 2.048 / 512.0;
        }
        int? vrb = null;
        if (((payload >> (55 - 34)) & 0x1) != 0)
        {
            var sign = (int)((payload >> (55 - 35)) & 0x1);
            var mag = (int)((payload >> (55 - 44)) & 0x1FF);
            vrb = Signed(mag, 9, sign) * 32;
        }
        int? vri = null;
        if (((payload >> (55 - 45)) & 0x1) != 0)
        {
            var sign = (int)((payload >> (55 - 46)) & 0x1);
            var mag = (int)((payload >> (55 - 55)) & 0x1FF);
            vri = Signed(mag, 9, sign) * 32;
        }
        return new Bds60Data(heading, ias, mach, vrb, vri);
    }

    // ----- Inference --------------------------------------------------------

    /// <summary>
    /// Which heuristic BDS registers a DF 20/21 payload could plausibly be.
    /// Set fields to true when the corresponding <c>Is-</c> validator accepts
    /// the payload; consumers decide how to resolve multi-match ambiguity.
    /// </summary>
    public readonly record struct Candidates(bool Bds40, bool Bds44, bool Bds50, bool Bds60)
    {
        public int Count => (Bds40 ? 1 : 0) + (Bds44 ? 1 : 0) + (Bds50 ? 1 : 0) + (Bds60 ? 1 : 0);
    }

    public static Candidates Infer(ulong payload)
    {
        return new Candidates(
            Bds40: IsBds40(payload),
            Bds44: IsBds44(payload),
            Bds50: IsBds50(payload),
            Bds60: IsBds60(payload));
    }

    // ----- Helpers -----------------------------------------------------------

    /// <summary>
    /// Sign-magnitude to signed int. Mode S uses sign-magnitude in separate
    /// bit fields (NOT two's complement), so <c>sign=1, magnitude=0</c>
    /// represents -2^width, not -0.
    /// </summary>
    private static int Signed(int value, int width, int sign)
    {
        return sign != 0 ? value - (1 << width) : value;
    }

    private static double NormaliseAngle(double deg)
    {
        var r = deg % 360.0;
        return r < 0 ? r + 360.0 : r;
    }

    /// <summary>
    /// Status-gated value consistency. When a status bit is 0, the entire
    /// gated value field (including any sign bit) must also be 0 — a nonzero
    /// value with status=0 is a strong signal the payload isn't this register.
    /// </summary>
    private static bool WrongStatus(ulong payload, int statusBit, int valueStart, int valueWidth)
    {
        var status = (payload >> (55 - statusBit)) & 0x1;
        if (status != 0)
        {
            return false;
        }
        var shift = 55 - (valueStart + valueWidth - 1);
        var mask = (1UL << valueWidth) - 1;
        return ((payload >> shift) & mask) != 0;
    }
}
