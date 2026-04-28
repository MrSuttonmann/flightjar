using FlightJar.Core.Stats;
using FlightJar.Terrain;
using FlightJar.Terrain.LineOfSight;

namespace FlightJar.Core.Tests.Stats;

public class BlockerFaceRasterTests
{
    private sealed class FlatSampler : ITerrainSampler
    {
        public double ElevationMetres(double lat, double lon) => 0.0;
    }

    /// <summary>One ridge running E–W just north of the receiver. Anything inside
    /// the ridge band gets a fixed elevation; everywhere else is sea level.</summary>
    private sealed class RidgeSampler : ITerrainSampler
    {
        private readonly double _ridgeLat;
        private readonly double _bandDeg;
        private readonly double _elevM;

        public RidgeSampler(double ridgeLat, double bandDeg, double elevM)
        {
            _ridgeLat = ridgeLat;
            _bandDeg = bandDeg;
            _elevM = elevM;
        }

        public double ElevationMetres(double lat, double lon) =>
            Math.Abs(lat - _ridgeLat) <= _bandDeg ? _elevM : 0.0;
    }

    /// <summary>Two parallel E–W ridges to the north of the receiver.
    /// Whichever band the lookup falls into wins (else sea level).</summary>
    private sealed class TwoRidgeSampler : ITerrainSampler
    {
        private readonly double _nearLat, _nearBand, _nearElev;
        private readonly double _farLat, _farBand, _farElev;

        public TwoRidgeSampler(
            double nearLat, double nearBand, double nearElev,
            double farLat, double farBand, double farElev)
        {
            _nearLat = nearLat; _nearBand = nearBand; _nearElev = nearElev;
            _farLat = farLat; _farBand = farBand; _farElev = farElev;
        }

        public double ElevationMetres(double lat, double lon)
        {
            if (Math.Abs(lat - _nearLat) <= _nearBand) return _nearElev;
            if (Math.Abs(lat - _farLat) <= _farBand) return _farElev;
            return 0.0;
        }
    }

    private static BlockerFaceParams DefaultParams(
        double radiusKm = 50,
        double gridDeg = 0.005,
        double targetAltMslM = 3000,
        double antennaMslM = 5) =>
        new(
            ReceiverLat: 51.5,
            ReceiverLon: -0.1,
            AntennaMslM: antennaMslM,
            TargetAltitudeM: targetAltMslM,
            RadiusKm: radiusKm,
            GridDeg: gridDeg);

    [Fact]
    public void Flat_terrain_high_target_produces_no_shaded_pixels()
    {
        var raster = BlockerFaceCompute.Compute(DefaultParams(), new FlatSampler());
        Assert.True(raster.Width > 0);
        Assert.True(raster.Height > 0);
        Assert.Equal(raster.Width * raster.Height, raster.Alpha.Length);
        Assert.All(raster.Alpha, b => Assert.Equal((byte)0, b));
    }

    [Fact]
    public void Ridge_inside_radius_shades_only_its_receiver_facing_silhouette()
    {
        // 1 km ridge band 20 km north of the receiver, target at 1 km MSL
        // 50 km out. Ridge clearly above LOS, so something must shade —
        // but with proper viewshed only the *near* side of the band can
        // be on the silhouette. The far side has lower angular elevation
        // (same elev / longer range) and is occluded by its own near edge.
        var ridgeLat = 51.5 + 20.0 / 111.32;
        var sampler = new RidgeSampler(ridgeLat, bandDeg: 0.01, elevM: 1000);
        var p = DefaultParams(radiusKm: 50, gridDeg: 0.005, targetAltMslM: 1000);

        var raster = BlockerFaceCompute.Compute(p, sampler);

        // Classify by pixel position relative to the ridge centre. The
        // silhouette ray-samples land just inside the band's southern
        // edge; the host pixel's centre may still sit outside the strict
        // ±0.01° band tolerance because of sub-pixel sampling, so we
        // don't try to bucket "in-band vs out-of-band" — just south
        // (receiver-facing) vs north (lee-side) of the ridge centre.
        // Raster is row-major with y=0 = NORTH (matches PNG scanline +
        // Leaflet image-overlay top edge).
        var shadedSouth = 0;
        var shadedNorth = 0;
        for (var y = 0; y < raster.Height; y++)
        {
            var lat = raster.MaxLat - (y + 0.5) * p.GridDeg;
            for (var x = 0; x < raster.Width; x++)
            {
                if (raster.Alpha[y * raster.Width + x] == 0) continue;
                if (lat < ridgeLat) shadedSouth++;
                else shadedNorth++;
            }
        }
        Assert.True(shadedSouth > 0, "expected near-side (south of ridge) pixels to shade");
        // Lee-side pixels (back-slope of the ridge) must stay clear
        // — they're occluded by the ridge's own near edge.
        Assert.Equal(0, shadedNorth);
    }

    [Fact]
    public void Hillshade_rgba_is_transparent_over_sea_and_opaque_over_land()
    {
        // Sea (FlatSampler returns 0): hillshade pass should emit
        // alpha=0 so the basemap shows through. Land pixels (anywhere
        // the sampler returns >0) get full alpha and a greyscale RGB.
        var ridgeLat = 51.5 + 20.0 / 111.32;
        var sampler = new RidgeSampler(ridgeLat, bandDeg: 0.01, elevM: 1000);
        var p = DefaultParams(radiusKm: 50, gridDeg: 0.005, targetAltMslM: 1000);

        var raster = BlockerFaceCompute.Compute(p, sampler);

        Assert.Equal(raster.Width * raster.Height * 4, raster.Rgba.Length);
        var seaTransparent = 0;
        var landOpaque = 0;
        for (var y = 0; y < raster.Height; y++)
        {
            var lat = raster.MaxLat - (y + 0.5) * p.GridDeg;
            var inBand = Math.Abs(lat - ridgeLat) <= 0.01;
            for (var x = 0; x < raster.Width; x++)
            {
                var a = raster.Rgba[(y * raster.Width + x) * 4 + 3];
                if (inBand && a == 255) landOpaque++;
                if (!inBand && a == 0) seaTransparent++;
            }
        }
        Assert.True(landOpaque > 0, "ridge band pixels must be opaque (alpha=255) in the hillshade RGBA");
        Assert.True(seaTransparent > 0, "outside the ridge band the sampler returns 0 → alpha must be 0");
    }

    [Fact]
    public void Far_taller_ridge_behind_a_closer_dominant_one_is_occluded()
    {
        // Closer ridge: 10 km north, 200 m elev → angular elev ~ 19.5 mrad.
        // Farther ridge: 30 km north, 300 m elev → angular elev ~ 9.8 mrad.
        // Both clear the LOS to a 200 m target at the radius edge
        // (target slope ≈ 3.9 mrad), so the naive "above LOS" test would
        // shade both. Viewshed must hide the far ridge entirely —
        // the closer one's silhouette dominates the bearing.
        var nearLat = 51.5 + 10.0 / 111.32;
        var farLat = 51.5 + 30.0 / 111.32;
        var sampler = new TwoRidgeSampler(
            nearLat: nearLat, nearBand: 0.01, nearElev: 200,
            farLat: farLat, farBand: 0.01, farElev: 300);
        var p = DefaultParams(radiusKm: 50, gridDeg: 0.005, targetAltMslM: 200);

        var raster = BlockerFaceCompute.Compute(p, sampler);

        // Bucket shaded pixels by which half of the radius (closer to
        // near or far ridge) they land in. Sub-pixel sampling means the
        // exact "in-band" classifier is too strict — split at the
        // halfway point between the two ridges instead.
        var midLat = (nearLat + farLat) / 2.0;
        var shadedNearHalf = 0;
        var shadedFarHalf = 0;
        for (var y = 0; y < raster.Height; y++)
        {
            var lat = raster.MaxLat - (y + 0.5) * p.GridDeg;
            for (var x = 0; x < raster.Width; x++)
            {
                if (raster.Alpha[y * raster.Width + x] == 0) continue;
                if (lat <= midLat) shadedNearHalf++;
                else shadedFarHalf++;
            }
        }
        Assert.True(shadedNearHalf > 0, "near ridge should shade — it's the dominant silhouette");
        Assert.Equal(0, shadedFarHalf);
    }

    [Fact]
    public void Multi_peak_ridges_mark_each_visible_summit_along_a_ray()
    {
        // Closer ridge: 10 km north, 200 m → angular elev ~ 19.5 mrad.
        // Farther ridge: 30 km north, 800 m → angular elev ~ 26.5 mrad.
        // The far ridge is *taller in angular terms* than the close one
        // — i.e. it pokes above the closer ridge and is visible from the
        // receiver. Multi-peak detection must mark BOTH ridges, not
        // just one. Single-max-per-ray would miss the closer one.
        var nearLat = 51.5 + 10.0 / 111.32;
        var farLat = 51.5 + 30.0 / 111.32;
        var sampler = new TwoRidgeSampler(
            nearLat: nearLat, nearBand: 0.01, nearElev: 200,
            farLat: farLat, farBand: 0.01, farElev: 800);
        var p = DefaultParams(radiusKm: 50, gridDeg: 0.005, targetAltMslM: 200);

        var raster = BlockerFaceCompute.Compute(p, sampler);

        // Both ridges must contribute to the strict-viewshed mask
        // (raster.Alpha): the near one as the first silhouette, the
        // far one because it pokes above the closer angular max.
        var midLat = (nearLat + farLat) / 2.0;
        var nearHalf = 0;
        var farHalf = 0;
        for (var y = 0; y < raster.Height; y++)
        {
            var lat = raster.MaxLat - (y + 0.5) * p.GridDeg;
            for (var x = 0; x < raster.Width; x++)
            {
                if (raster.Alpha[y * raster.Width + x] == 0) continue;
                if (lat <= midLat) nearHalf++;
                else farHalf++;
            }
        }
        Assert.True(nearHalf > 0,
            "near ridge should mark — it's the first visible silhouette per ray");
        Assert.True(farHalf > 0,
            "far ridge should mark — it pokes above the closer one (angular elev " +
            "26.5 mrad vs 19.5 mrad), so it's a visible secondary peak per ray");
    }

    [Fact]
    public void Pixels_outside_radius_are_never_shaded()
    {
        var sampler = new RidgeSampler(60.0, bandDeg: 0.5, elevM: 5000);
        var p = DefaultParams(radiusKm: 50, gridDeg: 0.01, targetAltMslM: 3000);
        var raster = BlockerFaceCompute.Compute(p, sampler);
        Assert.All(raster.Alpha, b => Assert.Equal((byte)0, b));
    }

    [Fact]
    public void Hillshade_rgba_is_transparent_outside_the_radius_circle()
    {
        // The bbox is square but the rendered raster should be circular —
        // pixels in the bbox corners (well outside the receiver's radius)
        // must come back fully transparent so the disc matches the
        // outermost ring of blackspot cells.
        var sampler = new RidgeSampler(51.5, bandDeg: 90, elevM: 200); // land everywhere
        var p = DefaultParams(radiusKm: 50, gridDeg: 0.01, targetAltMslM: 5000);
        var raster = BlockerFaceCompute.Compute(p, sampler);

        var radiusM = p.RadiusKm * 1000.0;
        // Test reconstructs lat with the lat-linear bbox spacing while the
        // raster spaces rows in Mercator-y, so allow a small boundary band.
        // Pixels well outside the radius (≥ 5% margin) must be transparent;
        // pixels well inside (≤ 95%) must be opaque.
        var transparentOutside = 0;
        var opaqueOutside = 0;
        var opaqueInside = 0;
        for (var y = 0; y < raster.Height; y++)
        {
            var lat = raster.MaxLat - (y + 0.5) * p.GridDeg;
            for (var x = 0; x < raster.Width; x++)
            {
                var lon = raster.MinLon + (x + 0.5) * p.GridDeg;
                var d = GreatCircle.DistanceMetres(p.ReceiverLat, p.ReceiverLon, lat, lon);
                var a = raster.Rgba[(y * raster.Width + x) * 4 + 3];
                if (d > radiusM * 1.05)
                {
                    if (a == 0) transparentOutside++;
                    else opaqueOutside++;
                }
                else if (d < radiusM * 0.95 && a == 255)
                {
                    opaqueInside++;
                }
            }
        }
        Assert.True(transparentOutside > 0, "bbox corners outside the radius should exist and be transparent");
        Assert.Equal(0, opaqueOutside);
        Assert.True(opaqueInside > 0, "interior land pixels must still render opaque hillshade");
    }

    /// <summary>Hill at <see cref="HillLat"/>,<see cref="HillLon"/>;
    /// flat sea elsewhere. Each test instance wires up exactly one
    /// hill so we can reason about which quadrant should shade.</summary>
    private sealed class PointHillSampler : ITerrainSampler
    {
        public required double HillLat { get; init; }
        public required double HillLon { get; init; }
        public required double HillElev { get; init; }
        public required double Tolerance { get; init; }

        public double ElevationMetres(double lat, double lon) =>
            Math.Abs(lat - HillLat) <= Tolerance && Math.Abs(lon - HillLon) <= Tolerance
                ? HillElev : 0.0;
    }

    [Theory]
    [InlineData("north", 0.18, 0.0)]
    [InlineData("south", -0.18, 0.0)]
    [InlineData("east", 0.0, 0.3)]
    [InlineData("west", 0.0, -0.3)]
    [InlineData("ne", 0.13, 0.2)]
    [InlineData("sw", -0.13, -0.2)]
    public void Hill_in_each_direction_shades_the_right_quadrant(
        string label, double dLat, double dLon)
    {
        // Place a single 800 m hill at offset (dLat, dLon) from the
        // receiver — roughly 20 km away in the named direction at lat 51.5.
        // The face raster + ridge mask must put their painted pixels
        // on the corresponding side of the receiver.
        var p = DefaultParams(radiusKm: 50, gridDeg: 0.005, targetAltMslM: 800);
        var sampler = new PointHillSampler
        {
            HillLat = p.ReceiverLat + dLat,
            HillLon = p.ReceiverLon + dLon,
            HillElev = 800,
            Tolerance = 0.015,
        };

        var raster = BlockerFaceCompute.Compute(p, sampler);

        // Sum painted alpha by which side of the receiver each pixel
        // sits on. Use the raster's own coordinate convention
        // (y=0=north, x=0=west) to translate pixel index → relative
        // direction.
        var (sN, sS, sE, sW) = (0, 0, 0, 0);
        for (var y = 0; y < raster.Height; y++)
        {
            var lat = raster.MaxLat - (y + 0.5) * p.GridDeg;
            for (var x = 0; x < raster.Width; x++)
            {
                if (raster.Alpha[y * raster.Width + x] == 0) continue;
                var lon = raster.MinLon + (x + 0.5) * p.GridDeg;
                if (lat > p.ReceiverLat) sN++;
                if (lat < p.ReceiverLat) sS++;
                if (lon > p.ReceiverLon) sE++;
                if (lon < p.ReceiverLon) sW++;
            }
        }
        // The hill should show up on the named side of the receiver.
        var (expectedSide, count) = label switch
        {
            "north" => ("north", sN),
            "south" => ("south", sS),
            "east" => ("east", sE),
            "west" => ("west", sW),
            "ne" => ("northeast", Math.Min(sN, sE)),
            "sw" => ("southwest", Math.Min(sS, sW)),
            _ => throw new InvalidOperationException(label),
        };
        Assert.True(count > 0,
            $"hill in the {expectedSide} should shade the {expectedSide} of the raster — " +
            $"got N={sN} S={sS} E={sE} W={sW}");
    }

    [Fact]
    public void Bbox_matches_BlackspotsGrid_BboxFor()
    {
        var p = DefaultParams(radiusKm: 100);
        var raster = BlockerFaceCompute.Compute(p, new FlatSampler());
        var (minLat, maxLat, minLon, maxLon) = BlockerFaceCompute.BboxFor(
            p.ReceiverLat, p.ReceiverLon, p.RadiusKm);
        Assert.Equal(minLat, raster.MinLat, precision: 6);
        Assert.Equal(maxLat, raster.MaxLat, precision: 6);
        Assert.Equal(minLon, raster.MinLon, precision: 6);
        Assert.Equal(maxLon, raster.MaxLon, precision: 6);
    }
}
