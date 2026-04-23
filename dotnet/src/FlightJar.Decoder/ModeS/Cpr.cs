namespace FlightJar.Decoder.ModeS;

/// <summary>
/// CPR (Compact Position Reporting) decoding. Mirrors pyModeS 3.2.0
/// <c>position/_cpr.py</c>.
/// </summary>
public static class Cpr
{
    private const double Denom = 131072.0; // 2^17

    // Latitude boundaries where NL steps down (ascending). Entry i is the
    // latitude at which NL transitions from (59 - i) to (59 - i - 1).
    // Derived from the DO-260B §A.1.7.2 formula with nz=15; stable across releases.
    private static readonly double[] NlBoundaries =
    {
        10.47047129996848,
        14.828174368686794,
        18.186263570713354,
        21.029394926028463,
        23.545044865570706,
        25.829247070587755,
        27.938987101219045,
        29.911356857318083,
        31.77209707681077,
        33.53993436298484,
        35.22899597796385,
        36.85025107593526,
        38.41241892412256,
        39.922566843338615,
        41.38651832260239,
        42.80914012243555,
        44.194549514192744,
        45.546267226602346,
        46.867332524987454,
        48.160391280966216,
        49.42776439255687,
        50.67150165553835,
        51.893424691687684,
        53.09516152796003,
        54.278174722729,
        55.44378444495043,
        56.59318756205918,
        57.72747353866114,
        58.84763776148457,
        59.954592766940294,
        61.04917774246351,
        62.13216659210329,
        63.20427479381928,
        64.2661652256744,
        65.31845309682089,
        66.36171008382617,
        67.39646774084667,
        68.4232202208333,
        69.44242631144024,
        70.454510749876,
        71.45986473028982,
        72.45884544728945,
        73.45177441667865,
        74.43893415725137,
        75.42056256653356,
        76.39684390794469,
        77.36789461328188,
        78.33374082922747,
        79.29428225456925,
        80.24923213280512,
        81.19801349271948,
        82.13956980510606,
        83.07199444719814,
        83.99173562980565,
        84.89166190702085,
        85.75541620944418,
        86.535369975121,
        87.0,
    };

    /// <summary>CPR longitude-zone count for latitude <paramref name="lat"/>.</summary>
    public static int Nl(double lat)
    {
        var absLat = Math.Abs(lat);
        if (absLat > 87.0)
        {
            return 1;
        }
        if (absLat == 87.0)
        {
            return 2;
        }
        // Equivalent to Python's bisect_right: first index where absLat < boundary.
        var idx = BisectRight(NlBoundaries, absLat);
        return 59 - idx;
    }

    private static int BisectRight(double[] sorted, double value)
    {
        var lo = 0;
        var hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (value < sorted[mid])
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return lo;
    }

    /// <summary>
    /// Resolve an airborne CPR frame against a nearby reference (within 180 NM).
    /// </summary>
    public static (double Lat, double Lon) AirbornePositionWithRef(
        int cprFormat, int cprLatRaw, int cprLonRaw, double latRef, double lonRef)
    {
        var cprLat = cprLatRaw / Denom;
        var cprLon = cprLonRaw / Denom;
        var dLat = cprFormat == 1 ? 360.0 / 59 : 360.0 / 60;

        var j = Math.Floor(0.5 + latRef / dLat - cprLat);
        var lat = dLat * (j + cprLat);

        var ni = Nl(lat) - cprFormat;
        var dLon = ni > 0 ? 360.0 / ni : 360.0;

        var m = Math.Floor(0.5 + lonRef / dLon - cprLon);
        var lon = dLon * (m + cprLon);

        return (lat, lon);
    }

    /// <summary>
    /// Resolve absolute lat/lon from an even/odd airborne CPR pair. Returns null
    /// when the two frames are in different latitude zones or when the result
    /// is physically impossible.
    /// </summary>
    public static (double Lat, double Lon)? AirbornePositionPair(
        int cprLatEvenRaw, int cprLonEvenRaw,
        int cprLatOddRaw, int cprLonOddRaw,
        bool evenIsNewer)
    {
        var cprLatEven = cprLatEvenRaw / Denom;
        var cprLonEven = cprLonEvenRaw / Denom;
        var cprLatOdd = cprLatOddRaw / Denom;
        var cprLonOdd = cprLonOddRaw / Denom;

        var j = Math.Floor(59 * cprLatEven - 60 * cprLatOdd + 0.5);

        var latEven = (360.0 / 60) * (Mod(j, 60) + cprLatEven);
        var latOdd = (360.0 / 59) * (Mod(j, 59) + cprLatOdd);

        if (latEven >= 270)
        {
            latEven -= 360;
        }
        if (latOdd >= 270)
        {
            latOdd -= 360;
        }

        if (Nl(latEven) != Nl(latOdd))
        {
            return null;
        }

        double lat, lon;
        if (evenIsNewer)
        {
            lat = latEven;
            var nl = Nl(lat);
            var ni = Math.Max(nl, 1);
            var m = Math.Floor(cprLonEven * (nl - 1) - cprLonOdd * nl + 0.5);
            lon = (360.0 / ni) * (Mod(m, ni) + cprLonEven);
        }
        else
        {
            lat = latOdd;
            var nl = Nl(lat);
            var ni = Math.Max(nl - 1, 1);
            var m = Math.Floor(cprLonEven * (nl - 1) - cprLonOdd * nl + 0.5);
            lon = (360.0 / ni) * (Mod(m, ni) + cprLonOdd);
        }

        if (lon > 180)
        {
            lon -= 360;
        }

        if (Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
        {
            return null;
        }

        return (lat, lon);
    }

    /// <summary>
    /// Resolve a surface CPR frame against a nearby reference (within 45 NM).
    /// </summary>
    public static (double Lat, double Lon) SurfacePositionWithRef(
        int cprFormat, int cprLatRaw, int cprLonRaw, double latRef, double lonRef)
    {
        var cprLat = cprLatRaw / Denom;
        var cprLon = cprLonRaw / Denom;
        var dLat = cprFormat == 1 ? 90.0 / 59 : 90.0 / 60;

        var j = Math.Floor(0.5 + latRef / dLat - cprLat);
        var lat = dLat * (j + cprLat);

        var ni = Nl(lat) - cprFormat;
        var dLon = ni > 0 ? 90.0 / ni : 90.0;

        var m = Math.Floor(0.5 + lonRef / dLon - cprLon);
        var lon = dLon * (m + cprLon);

        return (lat, lon);
    }

    /// <summary>
    /// Resolve an even/odd surface CPR pair against a receiver location.
    /// </summary>
    public static (double Lat, double Lon)? SurfacePositionPair(
        int cprLatEvenRaw, int cprLonEvenRaw,
        int cprLatOddRaw, int cprLonOddRaw,
        double latRef, double lonRef, bool evenIsNewer)
    {
        var cprLatEven = cprLatEvenRaw / Denom;
        var cprLonEven = cprLonEvenRaw / Denom;
        var cprLatOdd = cprLatOddRaw / Denom;
        var cprLonOdd = cprLonOddRaw / Denom;

        var j = Math.Floor(59 * cprLatEven - 60 * cprLatOdd + 0.5);

        var latEvenN = (90.0 / 60) * (Mod(j, 60) + cprLatEven);
        var latOddN = (90.0 / 59) * (Mod(j, 59) + cprLatOdd);
        var latEvenS = latEvenN - 90;
        var latOddS = latOddN - 90;

        var latEven = latRef > 0 ? latEvenN : latEvenS;
        var latOdd = latRef > 0 ? latOddN : latOddS;

        if (Nl(latEven) != Nl(latOdd))
        {
            return null;
        }

        double lat, lonBase;
        if (evenIsNewer)
        {
            lat = latEven;
            var nl = Nl(lat);
            var ni = Math.Max(nl, 1);
            var m = Math.Floor(cprLonEven * (nl - 1) - cprLonOdd * nl + 0.5);
            lonBase = (90.0 / ni) * (Mod(m, ni) + cprLonEven);
        }
        else
        {
            lat = latOdd;
            var nl = Nl(lat);
            var ni = Math.Max(nl - 1, 1);
            var m = Math.Floor(cprLonEven * (nl - 1) - cprLonOdd * nl + 0.5);
            lonBase = (90.0 / ni) * (Mod(m, ni) + cprLonOdd);
        }

        // Four 90° quadrant candidates; pick the one closest to the receiver.
        var best = 0.0;
        var bestDist = double.MaxValue;
        for (var q = 0; q < 4; q++)
        {
            var candidate = ((lonBase + q * 90 + 180) % 360) - 180;
            if (candidate < -180)
            {
                candidate += 360;
            }
            var dist = Math.Abs(lonRef - candidate);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = candidate;
            }
        }

        return (lat, best);
    }

    /// <summary>
    /// Python's floor-mod semantics: always returns a value in [0, modulus).
    /// C#'s <c>%</c> follows the sign of the dividend for negative inputs.
    /// </summary>
    private static double Mod(double value, double modulus)
    {
        var r = value % modulus;
        return r < 0 ? r + modulus : r;
    }
}
