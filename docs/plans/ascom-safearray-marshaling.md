# ASCOM SAFEARRAY marshaling bug (handoff for the x64 box)

**Status:** OPEN. Blocks the last 2 ASCOM simulator tests (leg is 8/10). Found by the device-simulator
CI ([device-simulator-ci.md](device-simulator-ci.md)) running the native-COM ASCOM leg against
Platform 7 on a `windows-latest` runner. Continue this on a real Windows + ASCOM Platform x64 box
(the fix is Windows-COM-only and best validated locally against a live SAFEARRAY).

Branch: `feat/sim-ci-followup` (PR #74). Everything below except the SAFEARRAY fix is already committed.

## Root cause (the third real bug the sim CI found)

Array-typed ASCOM COM properties are broken. `DispatchObject`'s array getters
(`src/TianWen.Lib/Devices/Ascom/ComInterop/DispatchObject.cs`) marshal the return VARIANT with the
BCL `ComVariant.As<T>()`:

```csharp
public string[] GetStringArray(string name) => ... variant.As<string[]>() ?? [];   // line ~101
public int[]    GetIntArray(string name)    => ... variant.As<int[]>()    ?? [];   // line ~114
public int[,]   GetInt2DArray(string name)  => ... variant.As<int[,]>()   ?? new int[0,0]; // line ~127
```

**`System.Runtime.InteropServices.Marshalling.ComVariant.As<T>()` does not marshal SAFEARRAYs** -- it
throws `ArgumentException: "Unsupported type" (Parameter 'T')` for array `T`. So every array-returning
ASCOM COM property fails. `SafeGet` swallows the throw to the fallback (`[]` / `null`), which is why it
surfaced as "0 registered filters" and "ImageData null", not a hard crash.

### Evidence (CI run 28688589035, ascom-sim step)

```
ASCOM ASCOM.OmniSim.Camera      ImageData       threw ArgumentException: Unsupported type (Parameter 'T')
   at AscomDispatchCamera.get_ImageArray()      (-> DispatchObject.GetInt2DArray -> variant.As<int[,]>())
ASCOM ASCOM.OmniSim.FilterWheel InitDeviceAsync threw ArgumentException: Unsupported type (Parameter 'T')
   at AscomDispatchFilterWheel.get_Names()       (-> DispatchObject.GetStringArray -> variant.As<string[]>())
```

Failing tests: `GivenAConnectedAscomSimulatorCameraWhenImageReadyThenItCanBeDownloaded`
(`driver.ImageData` null) and `GivenAConnectedAscomSimulatorFilterWheelWhenMovedThenPositionChanges`
(`fw.Filters.Count` == 0).

### Why it "worked on the x64 box before"

It predates the refactor to `ComVariant` + the `DispatchInterfaceGenerator` source generator. The old
hand-written COM marshaling handled SAFEARRAYs; the new `ComVariant.As<T[]>()` path silently does not,
and no test exercised a real array property until this CI leg -- so the regression shipped unnoticed.

### Affected properties (audit all of these)

- Camera `ImageArray` (`int[,]`)
- FilterWheel `Names` (`string[]`), `FocusOffsets` (`int[]`)
- Camera `Gains` / `Offsets` (`string[]`) and any other `Get{String,Int}Array` / `GetInt2DArray` caller
- Grep: `GetStringArray|GetIntArray|GetInt2DArray` usages, and the generator's array cases in
  `DispatchInterfaceGenerator.GetDispatchGetterCall` (`int[,]`/`string[]`/`int[]` -> those getters).

## Fix plan

Replace `variant.As<T[]>()` in the three getters with **manual SAFEARRAY marshaling** via P/Invoke.
Do NOT use `Marshal.GetObjectForNativeVariant` -- it is `[RequiresDynamicCode]` and `AddAscom()` ships
in the AOT binaries (CLI/Server/GUI/Fits). Keep it AOT-clean (`LibraryImport`).

Sketch:
1. Get the `SAFEARRAY*` from the VARIANT. `ComVariant` stores the union; for `VT_ARRAY` the `parray`
   pointer is the raw data: `nint psa = variant.GetRawDataRef<nint>();` (verify against `variant.VarType`
   having the `VT_ARRAY` flag; element type is the low bits, e.g. `VT_I4`, `VT_BSTR`, `VT_VARIANT`).
2. Add `OleAut32` imports to `ComInterop/NativeMethods.cs` (`LibraryImport`):
   `SafeArrayGetDim`, `SafeArrayGetLBound`, `SafeArrayGetUBound`, `SafeArrayGetElemsize`,
   `SafeArrayAccessData`, `SafeArrayUnaccessData` (and optionally `SafeArrayGetVartype`).
3. `GetIntArray` (1-D `int[]`): dim 1, bounds via LBound/UBound, `AccessData` -> copy `int` elements.
4. `GetInt2DArray` (2-D `int[,]`): dim 2. **Watch the ASCOM ImageArray convention** -- it is
   `[width, height]` (x-major), and `Channel.FromWxHImageData` already transposes W x H -> H x W, so
   marshal the SAFEARRAY in its native `[d0, d1]` order and let `FromWxHImageData` do the transpose
   (mirror how the Alpaca path documents wire-order in `AlpacaImageBytes` / CLAUDE.md). ASCOM
   SAFEARRAYs are column-major; index carefully (row/col vs bound order) and pin with a round-trip test.
5. `GetStringArray` (1-D BSTR): elements are BSTR pointers -> `Marshal.PtrToStringBSTR(elemPtr)`.
6. Always `SafeArrayUnaccessData` in a `finally`; keep the existing `variant.Dispose()` finally.
7. Some ASCOM drivers return `VT_ARRAY | VT_VARIANT` (each element a VARIANT) rather than a typed
   SAFEARRAY -- handle the VARIANT-element case (read each element's VARIANT then coerce), OmniSim may
   use one or the other. The reg-dump + a probe test will show which.

### Validate

- Local (best): add a unit test that builds a real SAFEARRAY via `SafeArrayCreate`/`SafeArrayPutElement`
  P/Invoke and round-trips it through the three getters -- validates the marshaling on the dev box with
  no ASCOM Platform needed. (This was the "Fix SafeArray + add unit test" option.)
- Full: on the x64 box with ASCOM Platform 7 installed, `TIANWEN_ASCOM_CI=1 dotnet test
  TianWen.Lib.Tests.Simulators --filter FullyQualifiedName~Ascom`, or dispatch
  `gh workflow run simulators.yml --ref feat/sim-ci-followup -f suite=ascom`. Expect 10/10 (8 currently
  pass; the camera + filter-wheel array tests are the 2 that flip green).

## What is already fixed on this branch (do NOT redo)

- **Install hang** -> ASCOM Platform 7 is an InstallAware package; silent switch is `/s` (not Inno
  `/SILENT`). `simulators.yml` uses `/s` + a `NetFx3` enable step + a WaitForExit diagnostic. (~58 s install.)
- **Discovery "0 registered"** -> `AscomDeviceIterator` now reads the Platform version + `<Type> Drivers`
  lists from BOTH registry views (Platform 7 registers 64-bit), and `_allSupportedDeviceTypes` now
  includes `CoverCalibrator` + `Switch` (drivers + mapping already existed).
- **Tests never populated devices** -> the tests now call `DiscoverAsync` (via `ResolveSimulatorAsync`).
- **`No service for type ILoggerFactory`** -> ASCOM tests build the SP via a `BuildServiceProvider`
  helper (IExternal + real `SystemTimeProvider` + xUnit logging), mirroring the Alpaca `BuildAlpaca`.
- **Non-deterministic sim pick** -> `ResolveSimulatorAsync` prefers OmniSim (matches the Alpaca leg),
  then classic `ASCOM.Simulator.*`, then any sim -- never `FirstOrDefault` over unstable registry order.

## Related follow-up (separate)

Kill the last external-`lzip` dependency by extracting the lzip codec into its own SharpAstro sibling
repo (`SharpAstro.Lzip`, like `SharpAstro.Jpeg` / `SER.Lib`): move `LzipDecoder`, add the missing
managed `LzipEncoder` (LZMA1 + lzip member framing + `-b` chunking to preserve parallel decode), publish
to NuGet, and have TianWen depend on it. Build/runtime only ever DECODE; only the offline regeneration
scripts (`Get-Tycho2Catalogs.ps1`, `generate_milkyway.cs`) compress, so the encoder is the new work.
This is its own repo/PR, not part of #74.
