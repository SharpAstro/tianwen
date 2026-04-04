using System.Text.Json.Serialization;

namespace TianWen.Lib.Astrometry.Catalogs;

internal record SimbadCatalogDto(
    string MainId,
    string[] Ids,
    string ObjType,
    double Ra,
    double Dec,
    double? VMag,
    double? BMinusV
);

[JsonSerializable(typeof(SimbadCatalogDto))]
internal partial class SimbadCatalogDtoJsonSerializerContext : JsonSerializerContext
{
}