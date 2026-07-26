using System.Runtime.CompilerServices;

// NinaApiJsonContext stays internal (nothing outside the shim speaks PascalCase single-OTA), but the
// NaN-guard tests must serialize through the REAL context: duplicating its options in the test would
// let a test pass while the actual endpoint still throws.
[assembly: InternalsVisibleTo("TianWen.Lib.Tests")]
