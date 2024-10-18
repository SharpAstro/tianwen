namespace TianWen.Lib.Astrometry.Catalogs;

internal record SimbadCatalogDto(
    string MainId,
    string[] Ids,
    string ObjType,
    double Ra,
    double Dec,
    double? VMag
);
