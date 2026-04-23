using System.Buffers.Binary;
using FlightJar.Terrain.Srtm;

namespace FlightJar.Terrain.Tests.Srtm;

public class SrtmTileTests
{
    /// <summary>
    /// Build a synthetic tile where each sample's elevation equals its row
    /// index (0 at the north edge, 3600 at the south edge). That gives us a
    /// purely latitude-dependent ramp which is trivial to reason about.
    /// </summary>
    private static SrtmTile RampTile(SrtmTileKey key)
    {
        var buf = new byte[SrtmTile.Size * SrtmTile.Size * 2];
        for (var row = 0; row < SrtmTile.Size; row++)
        {
            for (var col = 0; col < SrtmTile.Size; col++)
            {
                var offset = (row * SrtmTile.Size + col) * 2;
                BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(offset, 2), (short)row);
            }
        }
        return SrtmTile.FromBytes(key, buf);
    }

    [Fact]
    public void Sample_north_edge_is_zero_south_edge_is_3600()
    {
        var tile = RampTile(new SrtmTileKey(52, -2));
        // North edge = lat 53, south edge = lat 52
        Assert.Equal(0, tile.Sample(53.0, -1.5), 3);
        Assert.Equal(3600, tile.Sample(52.0, -1.5), 3);
    }

    [Fact]
    public void Sample_midpoint_is_halfway()
    {
        var tile = RampTile(new SrtmTileKey(52, -2));
        Assert.Equal(1800, tile.Sample(52.5, -1.5), 3);
    }

    [Fact]
    public void Sample_bilinear_interp_between_rows()
    {
        var tile = RampTile(new SrtmTileKey(52, -2));
        // One arc-second south of the north edge = row 1 = elev 1.
        var oneArcSec = 1.0 / 3600.0;
        Assert.Equal(1.0, tile.Sample(53.0 - oneArcSec, -1.5), 3);
        // Half an arc-second south of the north edge = halfway between rows 0 and 1 = 0.5.
        Assert.Equal(0.5, tile.Sample(53.0 - oneArcSec / 2, -1.5), 3);
    }

    [Fact]
    public void Empty_tile_samples_to_zero_everywhere()
    {
        var tile = SrtmTile.Empty(new SrtmTileKey(52, -2));
        Assert.Equal(0.0, tile.Sample(52.5, -1.5), 6);
    }

    [Fact]
    public void NoData_raw_value_becomes_zero()
    {
        var buf = new byte[SrtmTile.Size * SrtmTile.Size * 2];
        // Flood with the void marker 0x8000 = -32768.
        for (var i = 0; i < buf.Length; i += 2)
        {
            buf[i] = 0x80;
            buf[i + 1] = 0x00;
        }
        var tile = SrtmTile.FromBytes(new SrtmTileKey(52, -2), buf);
        Assert.Equal(0.0, tile.Sample(52.5, -1.5), 6);
    }

    [Fact]
    public void FromBytes_rejects_wrong_length()
    {
        Assert.Throws<InvalidDataException>(() =>
            SrtmTile.FromBytes(new SrtmTileKey(0, 0), new byte[100]));
    }
}
