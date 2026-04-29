namespace FlightJar.Core.State;

/// <summary>
/// Origin of an aircraft's most recent position fix. Mapped from
/// <c>(DF, CF)</c> by the registry; serialised snake_case via the global
/// <c>JsonStringEnumConverter</c>.
///
/// <list type="bullet">
///   <item><c>Adsb</c> — direct broadcast from the aircraft (DF17) or from
///   a non-transponder ADS-B-out device with a real or anonymous ICAO
///   address (DF18 CF 0/1).</item>
///   <item><c>Mlat</c> — position computed by an mlat-server from
///   time-difference-of-arrival across multiple receivers and relayed back
///   as a synthetic DF18 CF 2 squitter. Updates more slowly than ADS-B
///   (typically every few seconds) and is less precise.</item>
///   <item><c>Tisb</c> — Traffic Information Service-Broadcast: ground
///   stations rebroadcasting non-ADS-B radar tracks to ADS-B-In receivers
///   (DF18 CF 3 coarse, plus the catch-all of CF 4/5/7 we lump here).
///   Mostly a US transition-period feature.</item>
///   <item><c>Adsr</c> — Automatic Dependent Surveillance-Rebroadcast:
///   ADS-B forwarded by a ground station onto a different frequency (DF18
///   CF 6). Used to bridge 1090 ES and UAT in the US.</item>
/// </list>
/// </summary>
public enum PositionSource
{
    Adsb,
    Mlat,
    Tisb,
    Adsr,
}
