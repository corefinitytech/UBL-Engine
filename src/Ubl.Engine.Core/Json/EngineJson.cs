using System.Text.Json;
using System.Text.Json.Serialization;
using Ubl.Engine.Core.Geometry;

namespace Ubl.Engine.Core.Json;

public static class EngineJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        o.Converters.Add(new PtConverter());
        o.Converters.Add(new BoxConverter());
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return o;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Pt as [x, y], rounded to 1/100 inch.</summary>
    private sealed class PtConverter : JsonConverter<Pt>
    {
        public override Pt Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            var a = JsonSerializer.Deserialize<double[]>(ref r, o) ?? throw new JsonException("point expected");
            return new Pt(a[0], a[1]);
        }

        public override void Write(Utf8JsonWriter w, Pt p, JsonSerializerOptions o)
        {
            w.WriteStartArray();
            w.WriteNumberValue(Math.Round(p.X, 2));
            w.WriteNumberValue(Math.Round(p.Y, 2));
            w.WriteEndArray();
        }
    }

    /// <summary>Box as [x0, y0, x1, y1].</summary>
    private sealed class BoxConverter : JsonConverter<Box>
    {
        public override Box Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            var a = JsonSerializer.Deserialize<double[]>(ref r, o) ?? throw new JsonException("box expected");
            return new Box(a[0], a[1], a[2], a[3]);
        }

        public override void Write(Utf8JsonWriter w, Box b, JsonSerializerOptions o)
        {
            w.WriteStartArray();
            foreach (var v in new[] { b.X0, b.Y0, b.X1, b.Y1 }) w.WriteNumberValue(Math.Round(v, 2));
            w.WriteEndArray();
        }
    }
}
