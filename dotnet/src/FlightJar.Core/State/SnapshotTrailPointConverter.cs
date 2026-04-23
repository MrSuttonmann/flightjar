using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlightJar.Core.State;

/// <summary>
/// JSON converter that serialises a <see cref="SnapshotTrailPoint"/> as a
/// 5-element positional array <c>[lat, lon, altitude, speed, gap]</c> so
/// the frontend can index trail points by position.
/// </summary>
public sealed class SnapshotTrailPointConverter : JsonConverter<SnapshotTrailPoint>
{
    public override SnapshotTrailPoint Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("expected start of array");
        }
        reader.Read();
        var lat = reader.GetDouble();
        reader.Read();
        var lon = reader.GetDouble();
        reader.Read();
        int? altitude = reader.TokenType == JsonTokenType.Null ? null : reader.GetInt32();
        reader.Read();
        double? speed = reader.TokenType == JsonTokenType.Null ? null : reader.GetDouble();
        reader.Read();
        var gap = reader.GetBoolean();
        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("expected end of array");
        }
        return new SnapshotTrailPoint(lat, lon, altitude, speed, gap);
    }

    public override void Write(
        Utf8JsonWriter writer, SnapshotTrailPoint value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Lat);
        writer.WriteNumberValue(value.Lon);
        if (value.Altitude is int a)
        {
            writer.WriteNumberValue(a);
        }
        else
        {
            writer.WriteNullValue();
        }
        if (value.Speed is double s)
        {
            writer.WriteNumberValue(s);
        }
        else
        {
            writer.WriteNullValue();
        }
        writer.WriteBooleanValue(value.Gap);
        writer.WriteEndArray();
    }
}
