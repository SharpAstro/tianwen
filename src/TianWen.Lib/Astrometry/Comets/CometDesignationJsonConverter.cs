using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Astrometry.Comets;

/// <summary>
/// Serializes a <see cref="CometDesignation"/> as its single canonical string ("C/2023 A3", "13P"),
/// so the on-disk comet cache stays compact and human-readable and reparses losslessly on load
/// (rather than emitting the six packed fields of the struct). AOT-safe: registered by attribute on
/// <see cref="CometDesignation"/>, no reflection.
/// </summary>
public sealed class CometDesignationJsonConverter : JsonConverter<CometDesignation>
{
    public override CometDesignation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        return s is not null && CometDesignation.TryParse(s, out var designation)
            ? designation
            : throw new JsonException($"Invalid comet designation '{s}'");
    }

    public override void Write(Utf8JsonWriter writer, CometDesignation value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToCanonical());
}
