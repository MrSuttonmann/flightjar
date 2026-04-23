using System.Buffers.Binary;

namespace FlightJar.Terrain.Srtm;

/// <summary>
/// A single SRTM1 tile in memory: 3601×3601 samples of signed-16 big-endian
/// elevation data covering the tile's 1°×1° cell. Row 0 is the northern edge
/// (southLat+1), row 3600 the southern edge; column 0 is the western edge,
/// column 3600 the eastern edge. Sample spacing is 1 arc-second ≈ 30 m.
///
/// A "void" value of <c>-32768</c> is mapped to sea-level 0 m on parse.
/// </summary>
public sealed class SrtmTile
{
    public const int Size = 3601;
    private const short NoDataRaw = -32768;

    public SrtmTileKey Key { get; }

    /// <summary>Row-major, north-to-south, west-to-east. Always <see cref="Size"/>² long.</summary>
    private readonly short[] _data;

    private SrtmTile(SrtmTileKey key, short[] data)
    {
        Key = key;
        _data = data;
    }

    /// <summary>A sentinel "tile is ocean / not available" tile that samples to 0 everywhere.</summary>
    public static SrtmTile Empty(SrtmTileKey key) => new(key, []);

    /// <summary>True for the <see cref="Empty"/> sentinel (no elevation data).</summary>
    public bool IsEmpty => _data.Length == 0;

    /// <summary>
    /// Parse raw <c>.hgt</c> bytes (<see cref="Size"/>² big-endian int16 samples) into a tile.
    /// </summary>
    public static SrtmTile FromBytes(SrtmTileKey key, ReadOnlySpan<byte> bytes)
    {
        const int expected = Size * Size * 2;
        if (bytes.Length != expected)
        {
            throw new InvalidDataException(
                $"SRTM tile {key} has {bytes.Length} bytes, expected {expected}");
        }
        var data = new short[Size * Size];
        for (var i = 0; i < data.Length; i++)
        {
            var raw = BinaryPrimitives.ReadInt16BigEndian(bytes.Slice(i * 2, 2));
            data[i] = raw == NoDataRaw ? (short)0 : raw;
        }
        return new SrtmTile(key, data);
    }

    /// <summary>
    /// Bilinear-interpolated elevation in metres at the given point, which must
    /// lie within this tile's 1°×1° cell (edges inclusive). Returns 0 for empty
    /// (not-downloadable / ocean) tiles.
    /// </summary>
    public double Sample(double lat, double lon)
    {
        if (_data.Length == 0)
        {
            return 0.0;
        }
        // Sample grid coordinates: (0,0) = NW corner, (Size-1, Size-1) = SE corner.
        var y = (Key.SouthLat + 1 - lat) * (Size - 1);
        var x = (lon - Key.WestLon) * (Size - 1);

        // Clamp into the grid — callers shouldn't depend on this, but it keeps
        // floating-point slop at the exact edges from indexing out of bounds.
        if (y < 0) y = 0;
        if (y > Size - 1) y = Size - 1;
        if (x < 0) x = 0;
        if (x > Size - 1) x = Size - 1;

        var y0 = (int)Math.Floor(y);
        var x0 = (int)Math.Floor(x);
        var y1 = Math.Min(y0 + 1, Size - 1);
        var x1 = Math.Min(x0 + 1, Size - 1);
        var fy = y - y0;
        var fx = x - x0;

        var h00 = _data[y0 * Size + x0];
        var h01 = _data[y0 * Size + x1];
        var h10 = _data[y1 * Size + x0];
        var h11 = _data[y1 * Size + x1];

        var top = h00 * (1.0 - fx) + h01 * fx;
        var bot = h10 * (1.0 - fx) + h11 * fx;
        return top * (1.0 - fy) + bot * fy;
    }
}
