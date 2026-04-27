using FlightJar.Terrain;
using FlightJar.Terrain.LineOfSight;

namespace FlightJar.Core.Stats;

/// <summary>
/// Knobs for a single blocker-face raster. <see cref="GridDeg"/> is the
/// pixel size of the raster (smaller = sharper, more bytes per row).
/// </summary>
public sealed record BlockerFaceParams(
    double ReceiverLat,
    double ReceiverLon,
    double AntennaMslM,
    double TargetAltitudeM,
    double RadiusKm,
    double GridDeg);

/// <summary>
/// Hillshaded terrain raster with the receiver-facing blocking faces
/// tinted red. Sea/no-data pixels are transparent; everywhere else
/// shows greyscale relief; pixels above the LOS line to the target
/// altitude AND visible from the antenna are blended toward red in
/// proportion to how steeply they rise above the LOS plane.
///
/// <para>
/// <see cref="Alpha"/> is the intermediate strict-viewshed mask
/// (intensity = how far above LOS, 0 = not blocking). It's the input to
/// the hillshade tinting pass and stays on the record so tests can
/// assert on it without re-running the compute.
/// <see cref="Rgba"/> is the final 8-bit RGBA pixel buffer the endpoint
/// PNG-encodes and ships.
/// </para>
///
/// <para>
/// Row-major: index <c>y * Width + x</c>. <b>y=0 is the NORTHERN row</b>
/// — matches PNG scanline-from-top convention so a Leaflet image overlay
/// with <c>[[minLat,minLon],[maxLat,maxLon]]</c> bounds renders upright.
/// </para>
/// </summary>
public sealed record BlockerFaceRaster(
    BlockerFaceParams Params,
    double MinLat, double MaxLat,
    double MinLon, double MaxLon,
    int Width, int Height,
    byte[] Alpha,
    byte[] Rgba);

/// <summary>
/// Two-stage compute:
///   1. Strict-viewshed ray walk from the receiver to fill the per-pixel
///      <see cref="BlockerFaceRaster.Alpha"/> mask — each visible
///      silhouette transition above the LOS line to the target altitude
///      paints with intensity proportional to "metres above LOS".
///   2. Hillshade pass over the same raster: per-pixel Lambertian
///      shading from the 4-neighbour gradient, blended toward red where
///      the viewshed mask says the pixel is an active blocker.
/// </summary>
/// <remarks>
/// <para>
/// Stage 1 keeps the geometry from before — sub-pixel azimuth fan,
/// 30 m radial step, multi-peak detection, refraction-corrected angular
/// elevations. Stage 2 turns the sparse strict-viewshed answer into a
/// proper terrain map: every land pixel gets greyscale relief so the
/// surrounding topography reads as topography, and the blocker pixels
/// stand out as red against that greyscale rather than as a few
/// disconnected dots floating on the basemap.
/// </para>
/// <para>
/// Hillshade math: standard cartographic Lambertian reflectance with
/// the sun in the NW at 45°. Pixel pitch is computed from the local
/// planar projection (<c>kmPerDegLat</c> / <c>cos(lat) * kmPerDegLat</c>)
/// so dz/dx and dz/dy are in metres-per-metre, giving a slope angle
/// independent of latitude.
/// </para>
/// </remarks>
public static class BlockerFaceCompute
{
    /// <summary>Azimuth step in degrees. 0.1° = 3600 rays.</summary>
    public const double DefaultAzimuthStepDeg = 0.1;

    /// <summary>Radial step in metres along each ray. 30 m matches
    /// SRTM-1 horizontal resolution.</summary>
    public const double DefaultRangeStepM = 30.0;

    /// <summary>Sun azimuth (degrees) for hillshade — NW (315°) is the
    /// cartographic convention; gives shadows on the SE faces of hills.</summary>
    public const double HillshadeSunAzimuthDeg = 315.0;

    /// <summary>Sun altitude (degrees) for hillshade — 30° (lower than
    /// the default 45°) gives longer shadows and more pronounced
    /// ridgelines, so subtle UK-class terrain reads as 3D instead of
    /// fading to a flat mid-grey.</summary>
    public const double HillshadeSunAltitudeDeg = 30.0;

    /// <summary>Vertical-exaggeration factor applied to slopes before
    /// the Lambertian shading. 1.5× gently amplifies low-relief terrain
    /// (Pennines, Wolds) without making genuinely steep mountains look
    /// cartoonish — they still saturate the slope curve.</summary>
    public const double HillshadeSlopeExaggeration = 1.5;

    /// <summary>
    /// Axis-aligned lat/lon bbox covering a circle of <paramref name="radiusKm"/>
    /// around the receiver, computed in the same local-planar projection
    /// the per-pixel pass uses below.
    /// </summary>
    public static (double MinLat, double MaxLat, double MinLon, double MaxLon) BboxFor(
        double receiverLat, double receiverLon, double radiusKm)
    {
        const double kmPerDegLat = 111.32;
        var latRad = receiverLat * Math.PI / 180.0;
        var dlat = radiusKm / kmPerDegLat;
        var dlon = radiusKm / (kmPerDegLat * Math.Max(0.01, Math.Cos(latRad)));
        return (receiverLat - dlat, receiverLat + dlat, receiverLon - dlon, receiverLon + dlon);
    }

    /// <summary>
    /// Web-Mercator forward (degrees → unit-radius mercator y). Used to
    /// sample the PNG in Mercator-linear y so Leaflet's
    /// <c>imageOverlay</c> stretching — which maps the image to layer
    /// coords in <c>EPSG:3857</c> linearly between bbox corners — lines
    /// up the rendered terrain with its true geographic position.
    /// Sampling linear-in-lat instead places the bbox's midpoint
    /// content at the Mercator midpoint of the rendered rectangle,
    /// which at UK latitudes shifts terrain ~11 km north of where it
    /// should be.
    /// </summary>
    private static double LatToMercY(double latDeg)
    {
        var lat = latDeg * Math.PI / 180.0;
        return Math.Log(Math.Tan(Math.PI / 4.0 + lat / 2.0));
    }

    /// <summary>Web-Mercator inverse (mercator y → degrees).</summary>
    private static double MercYToLat(double mercY)
        => (2.0 * Math.Atan(Math.Exp(mercY)) - Math.PI / 2.0) * 180.0 / Math.PI;

    /// <summary>
    /// Produce the hillshaded raster for the given receiver / altitude / sampler.
    /// </summary>
    public static BlockerFaceRaster Compute(BlockerFaceParams p, ITerrainSampler sampler)
    {
        var (minLat, maxLat, minLon, maxLon) = BboxFor(
            p.ReceiverLat, p.ReceiverLon, p.RadiusKm);

        // Mercator y range covering the bbox. The PNG's y-axis is
        // linear in this range so Leaflet's image-overlay stretching
        // (which is linear in EPSG:3857 layer coords) aligns rows to
        // their true latitudes.
        var mercTop = LatToMercY(maxLat);
        var mercBot = LatToMercY(minLat);
        var mercSpan = mercTop - mercBot;

        var step = p.GridDeg;
        var width = Math.Max(1, (int)Math.Ceiling((maxLon - minLon) / step));
        // Pixel pitch for the y-axis is in Mercator units, derived from the
        // requested step at the receiver's latitude. Converting `step` (a
        // lat-degree quantity) to Mercator gives a height that produces
        // ~step-degree pixels at the receiver's lat (slightly finer at
        // high latitudes, coarser at low latitudes — same property as
        // any Mercator raster).
        var rxMercStep = LatToMercY(p.ReceiverLat + step / 2.0)
                       - LatToMercY(p.ReceiverLat - step / 2.0);
        var height = Math.Max(1, (int)Math.Ceiling(mercSpan / rxMercStep));
        var alpha = new byte[width * height];

        var rxLatRad = p.ReceiverLat * Math.PI / 180.0;
        const double kmPerDegLat = 111.32;
        var kmPerDegLon = kmPerDegLat * Math.Max(0.01, Math.Cos(rxLatRad));
        var radiusM = p.RadiusKm * 1000.0;
        var rEff = GreatCircle.EffectiveRadiusMetres;

        var dAzDeg = DefaultAzimuthStepDeg;
        var numAz = (int)Math.Round(360.0 / dAzDeg);
        var dR = DefaultRangeStepM;
        var numR = Math.Max(1, (int)Math.Ceiling(radiusM / dR));

        var targetEffFar = p.TargetAltitudeM - radiusM * radiusM / (2.0 * rEff);
        var targetSlope = (targetEffFar - p.AntennaMslM) / radiusM;

        const double oneDegree = Math.PI / 180.0;
        var intensityScale = 255.0 / oneDegree;

        // ---- Stage 1: strict-viewshed ray walks ----
        Parallel.For(0, numAz, ai =>
        {
            var azRad = ai * dAzDeg * Math.PI / 180.0;
            var sinAz = Math.Sin(azRad);
            var cosAz = Math.Cos(azRad);
            var runMax = float.NegativeInfinity;
            for (var ri = 0; ri < numR; ri++)
            {
                var d = (ri + 1) * dR;
                var dxM = d * sinAz;
                var dyM = d * cosAz;
                var lat = p.ReceiverLat + dyM / (kmPerDegLat * 1000.0);
                var lon = p.ReceiverLon + dxM / (kmPerDegLon * 1000.0);
                if (lat < minLat || lat > maxLat || lon < minLon || lon > maxLon)
                {
                    continue;
                }
                var elev = sampler.ElevationMetres(lat, lon);
                if (elev < 0) elev = 0;
                var elevEff = elev - d * d / (2.0 * rEff);
                var ang = (float)((elevEff - p.AntennaMslM) / d);
                var isNewSilhouette = ang > runMax;
                if (isNewSilhouette)
                {
                    runMax = ang;
                }
                var aboveTarget = ang - (float)targetSlope;
                if (!(isNewSilhouette && elev > 0 && aboveTarget > 0))
                {
                    continue;
                }
                // y=0 = NORTH (PNG scanline convention). The y-axis is
                // linear in Web Mercator, so when Leaflet stretches the
                // PNG between bbox corners, every pixel lands at its true
                // latitude. (Linear-in-lat would shift terrain ~11 km
                // north at UK latitudes.)
                var yPix = (int)((mercTop - LatToMercY(lat)) / mercSpan * height);
                var xPix = (int)((lon - minLon) / step);
                if (yPix < 0 || yPix >= height || xPix < 0 || xPix >= width)
                {
                    continue;
                }
                var idx = yPix * width + xPix;
                var byteVal = (int)(aboveTarget * intensityScale);
                if (byteVal > 255) byteVal = 255;
                if ((byte)byteVal > alpha[idx])
                {
                    alpha[idx] = (byte)byteVal;
                }
            }
        });

        // 3×3 max-dilation widens single-pixel silhouette lines so the
        // red tint is visible at typical zoom levels. Without this the
        // near-field hill that dominates a Nottingham-class antenna's
        // SW horizon collapses to ~7 pixels and disappears.
        alpha = Dilate3x3(alpha, width, height);

        // ---- Stage 2: hillshade + blocker tint ----
        var rgba = RenderHillshade(p, width, height, step, minLon,
            mercTop, mercSpan, kmPerDegLon, alpha, sampler);

        return new BlockerFaceRaster(
            p, minLat, maxLat, minLon, maxLon, width, height, alpha, rgba);
    }

    /// <summary>
    /// Per-pixel Lambertian hillshade from a 4-neighbour gradient. Sea
    /// pixels emit alpha=0 (transparent — basemap shows through);
    /// everywhere else gets greyscale relief, blended toward red where
    /// <paramref name="alpha"/> says the pixel is an active blocker.
    /// </summary>
    private static byte[] RenderHillshade(
        BlockerFaceParams p, int width, int height, double step,
        double minLon,
        double mercTop, double mercSpan,
        double kmPerDegLon,
        byte[] alpha, ITerrainSampler sampler)
    {
        var rgba = new byte[width * height * 4];
        const double kmPerDegLat = 111.32;
        var pitchX = step * kmPerDegLon * 1000.0;
        var sunAz = HillshadeSunAzimuthDeg * Math.PI / 180.0;
        var sunAlt = HillshadeSunAltitudeDeg * Math.PI / 180.0;
        var sinSunAlt = Math.Sin(sunAlt);
        var cosSunAlt = Math.Cos(sunAlt);

        Parallel.For(0, height, y =>
        {
            // y-axis is Mercator-linear (see Compute() comment).
            var lat = MercYToLat(mercTop - (y + 0.5) * mercSpan / height);
            // Mercator pixels are non-uniform in lat-degree extent — at
            // higher latitudes a pixel covers fewer lat-degrees. Use the
            // actual lat-extent of THIS pixel for the dzdy denominator
            // so the slope angle is geometrically correct everywhere.
            var latNeighbourSpan = MercYToLat(mercTop - y * mercSpan / height)
                                  - MercYToLat(mercTop - (y + 1) * mercSpan / height);
            var pitchY = latNeighbourSpan * kmPerDegLat * 1000.0;
            for (var x = 0; x < width; x++)
            {
                var lon = minLon + (x + 0.5) * step;
                var z = sampler.ElevationMetres(lat, lon);
                var idx4 = (y * width + x) * 4;
                if (z <= 0)
                {
                    // Sea / no-data — transparent so the basemap shows through.
                    rgba[idx4 + 3] = 0;
                    continue;
                }
                // 4-neighbour gradient. Use lat-degree pixel extent for
                // the y-step so the gradient is consistent with how
                // pixels are spaced on the actual earth.
                var zW = Math.Max(0.0, sampler.ElevationMetres(lat, lon - step));
                var zE = Math.Max(0.0, sampler.ElevationMetres(lat, lon + step));
                var zN = Math.Max(0.0, sampler.ElevationMetres(lat + latNeighbourSpan, lon));
                var zS = Math.Max(0.0, sampler.ElevationMetres(lat - latNeighbourSpan, lon));
                // Apply z-exaggeration so subtle UK-class terrain shows
                // distinct relief instead of washing out to mid-grey.
                var dzdx = HillshadeSlopeExaggeration * (zE - zW) / (2.0 * pitchX);
                var dzdy = HillshadeSlopeExaggeration * (zN - zS) / (2.0 * pitchY);
                var slopeRad = Math.Atan(Math.Sqrt(dzdx * dzdx + dzdy * dzdy));
                var aspectRad = Math.Atan2(dzdy, -dzdx);
                var hs = Math.Cos(slopeRad) * sinSunAlt
                       + Math.Sin(slopeRad) * cosSunAlt * Math.Cos(sunAz - aspectRad);
                if (hs < 0) hs = 0;
                if (hs > 1) hs = 1;
                // Map [0, 1] to [25, 240] — wider than the conservative
                // [50, 230] to push lit faces brighter and shadowed
                // faces darker, so the relief reads through the
                // semi-transparent layer overlay.
                var grey = 25 + hs * 215;

                var blockerByte = alpha[(y * width) + x];
                if (blockerByte > 0)
                {
                    // Blend the greyscale toward red proportional to
                    // viewshed intensity. Saturate quickly (blockerByte
                    // ≈ 170 → fully red) so the visual signal pops.
                    var t = Math.Min(1.0, blockerByte * (1.5 / 255.0));
                    rgba[idx4] = (byte)(grey * (1 - t) + 220 * t);
                    rgba[idx4 + 1] = (byte)(grey * (1 - t) + 40 * t);
                    rgba[idx4 + 2] = (byte)(grey * (1 - t) + 40 * t);
                }
                else
                {
                    var g = (byte)grey;
                    rgba[idx4] = g;
                    rgba[idx4 + 1] = g;
                    rgba[idx4 + 2] = g;
                }
                rgba[idx4 + 3] = 255;
            }
        });
        return rgba;
    }

    /// <summary>
    /// 3×3 max-dilation over a single-channel byte buffer. Returns a
    /// fresh buffer; the input is unchanged.
    /// </summary>
    private static byte[] Dilate3x3(byte[] src, int width, int height)
    {
        var dst = new byte[src.Length];
        Parallel.For(0, height, y =>
        {
            var rowStart = y * width;
            for (var x = 0; x < width; x++)
            {
                byte best = 0;
                var y0 = y == 0 ? 0 : y - 1;
                var y1 = y == height - 1 ? height - 1 : y + 1;
                var x0 = x == 0 ? 0 : x - 1;
                var x1 = x == width - 1 ? width - 1 : x + 1;
                for (var yy = y0; yy <= y1; yy++)
                {
                    var rowOff = yy * width;
                    for (var xx = x0; xx <= x1; xx++)
                    {
                        var v = src[rowOff + xx];
                        if (v > best) best = v;
                    }
                }
                dst[rowStart + x] = best;
            }
        });
        return dst;
    }
}
