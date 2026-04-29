using System.Globalization;
using FlightJar.Core.Configuration;

namespace FlightJar.Api.Configuration;

public static class AppOptionsBinder
{
    public static AppOptions FromEnvironment(IDictionary<string, string?>? env = null)
    {
        var get = MakeLookup(env);

        var rotateRaw = Str(get, "BEAST_ROTATE", "daily");
        if (!TryParseRotate(rotateRaw, out var rotate))
        {
            throw new ConfigException($"BEAST_ROTATE='{rotateRaw}': must be one of none, hourly, daily");
        }

        var port = Int(get, "BEAST_PORT", 30005);
        if (port < 1 || port > 65535)
        {
            throw new ConfigException($"BEAST_PORT={port}: must be in 1..65535");
        }

        var keep = Int(get, "BEAST_ROTATE_KEEP", 14);
        if (keep < 0)
        {
            throw new ConfigException($"BEAST_ROTATE_KEEP={keep}: must be >= 0");
        }

        var interval = FloatRequired(get, "SNAPSHOT_INTERVAL", 1.0);
        if (interval <= 0)
        {
            throw new ConfigException(
                $"SNAPSHOT_INTERVAL={interval.ToString(CultureInfo.InvariantCulture)}: must be > 0");
        }

        var refreshHours = FloatRequired(get, "AIRCRAFT_DB_REFRESH_HOURS", 0.0);
        if (refreshHours < 0)
        {
            throw new ConfigException(
                $"AIRCRAFT_DB_REFRESH_HOURS={refreshHours.ToString(CultureInfo.InvariantCulture)}: must be >= 0");
        }

        var host = Str(get, "BEAST_HOST", "readsb");
        if (string.IsNullOrEmpty(host))
        {
            host = "readsb";
        }

        var antennaAgl = FloatRequired(get, "BLACKSPOTS_ANTENNA_AGL_M", 5.0);
        if (antennaAgl < 0)
        {
            throw new ConfigException(
                $"BLACKSPOTS_ANTENNA_AGL_M={antennaAgl.ToString(CultureInfo.InvariantCulture)}: must be >= 0");
        }
        var antennaMsl = FloatOptional(get, "BLACKSPOTS_ANTENNA_MSL_M");
        if (antennaMsl is double mslVal && (mslVal < -500 || mslVal > 9000))
        {
            throw new ConfigException(
                $"BLACKSPOTS_ANTENNA_MSL_M={mslVal.ToString(CultureInfo.InvariantCulture)}: must be in [-500, 9000]");
        }
        var radiusKm = FloatRequired(get, "BLACKSPOTS_RADIUS_KM", 400.0);
        if (radiusKm <= 0 || radiusKm > 1000)
        {
            throw new ConfigException(
                $"BLACKSPOTS_RADIUS_KM={radiusKm.ToString(CultureInfo.InvariantCulture)}: must be in (0, 1000]");
        }
        var gridDeg = FloatRequired(get, "BLACKSPOTS_GRID_DEG", 0.05);
        if (gridDeg <= 0 || gridDeg > 1)
        {
            throw new ConfigException(
                $"BLACKSPOTS_GRID_DEG={gridDeg.ToString(CultureInfo.InvariantCulture)}: must be in (0, 1]");
        }
        var faceGridDeg = FloatRequired(get, "BLACKSPOTS_FACE_GRID_DEG", 0.005);
        if (faceGridDeg <= 0 || faceGridDeg > 0.05)
        {
            throw new ConfigException(
                $"BLACKSPOTS_FACE_GRID_DEG={faceGridDeg.ToString(CultureInfo.InvariantCulture)}: must be in (0, 0.05]");
        }
        var maxAgl = FloatRequired(get, "BLACKSPOTS_MAX_AGL_M", 100.0);
        if (maxAgl <= 0)
        {
            throw new ConfigException(
                $"BLACKSPOTS_MAX_AGL_M={maxAgl.ToString(CultureInfo.InvariantCulture)}: must be > 0");
        }
        var idleTimeoutMin = FloatRequired(get, "BLACKSPOTS_IDLE_TIMEOUT_MIN", 15.0);
        if (idleTimeoutMin < 0)
        {
            throw new ConfigException(
                $"BLACKSPOTS_IDLE_TIMEOUT_MIN={idleTimeoutMin.ToString(CultureInfo.InvariantCulture)}: must be >= 0");
        }

        return new AppOptions
        {
            BeastHost = host,
            BeastPort = port,
            LatRef = FloatOptional(get, "LAT_REF"),
            LonRef = FloatOptional(get, "LON_REF"),
            ReceiverAnonKm = FloatOptional(get, "RECEIVER_ANON_KM") ?? 0.0,
            SiteName = NullIfEmpty(Str(get, "SITE_NAME", "")),
            JsonlPath = Str(get, "BEAST_OUTFILE", ""),
            JsonlRotate = rotate,
            JsonlKeep = keep,
            JsonlStdout = Bool(get, "BEAST_STDOUT", false),
            JsonlDecode = !Bool(get, "BEAST_NO_DECODE", false),
            SnapshotInterval = interval,
            AircraftDbRefreshHours = refreshHours,
            FlightRoutesEnabled = Bool(get, "FLIGHT_ROUTES", true),
            MetarEnabled = Bool(get, "METAR_WEATHER", true),
            OpenAipApiKey = Str(get, "OPENAIP_API_KEY", ""),
            OpenAipPrefetchRadiusKm = FloatOptional(get, "OPENAIP_PREFETCH_RADIUS_KM") ?? 300.0,
            VfrmapChartDate = Str(get, "VFRMAP_CHART_DATE", ""),
            BlackspotsEnabled = Bool(get, "BLACKSPOTS_ENABLED", true),
            BlackspotsAntennaAglM = antennaAgl,
            BlackspotsAntennaMslM = antennaMsl,
            BlackspotsRadiusKm = radiusKm,
            BlackspotsGridDeg = gridDeg,
            BlackspotsFaceGridDeg = faceGridDeg,
            BlackspotsMaxAglM = maxAgl,
            BlackspotsIdleTimeoutMinutes = idleTimeoutMin,
            TerrainCacheDir = NullIfEmpty(Str(get, "TERRAIN_CACHE_DIR", "")) ?? "/data/terrain",
            TelemetryEnabled = Bool(get, "TELEMETRY_ENABLED", true),
            Password = Str(get, "FLIGHTJAR_PASSWORD", ""),
        };
    }

    private static Func<string, string?> MakeLookup(IDictionary<string, string?>? env)
    {
        if (env is null)
        {
            return static name => Environment.GetEnvironmentVariable(name);
        }
        return name => env.TryGetValue(name, out var v) ? v : null;
    }

    private static string Str(Func<string, string?> get, string name, string @default)
    {
        var raw = get(name);
        if (raw is null)
        {
            return @default;
        }
        return raw.Trim();
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    private static int Int(Func<string, string?> get, string name, int @default)
    {
        var raw = Str(get, name, "");
        if (raw.Length == 0)
        {
            return @default;
        }
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
        {
            throw new ConfigException($"{name}='{raw}': not an integer");
        }
        return v;
    }

    private static double FloatRequired(Func<string, string?> get, string name, double @default)
    {
        var raw = Str(get, name, "");
        if (raw.Length == 0)
        {
            return @default;
        }
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            throw new ConfigException($"{name}='{raw}': not a number");
        }
        return v;
    }

    private static double? FloatOptional(Func<string, string?> get, string name)
    {
        var raw = Str(get, name, "");
        if (raw.Length == 0)
        {
            return null;
        }
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            return null;
        }
        return v;
    }

    private static bool Bool(Func<string, string?> get, string name, bool @default)
    {
        var raw = Str(get, name, @default ? "1" : "0").ToLowerInvariant();
        return raw is "1" or "true" or "yes" or "on";
    }

    private static bool TryParseRotate(string raw, out JsonlRotateMode mode)
    {
        switch (raw.ToLowerInvariant())
        {
            case "none":
                mode = JsonlRotateMode.None;
                return true;
            case "hourly":
                mode = JsonlRotateMode.Hourly;
                return true;
            case "daily":
                mode = JsonlRotateMode.Daily;
                return true;
            default:
                mode = JsonlRotateMode.Daily;
                return false;
        }
    }
}
