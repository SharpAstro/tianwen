using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Hosting.Dto.NinaV2;

/// <summary>
/// Camera info DTO matching ninaAPI v2 <c>/v2/api/equipment/camera/info</c> response shape.
/// </summary>
public sealed class NinaCameraInfoDto
{
    public required bool Connected { get; init; }
    public required string Name { get; init; }
    public required double Temperature { get; init; }
    public required double TargetTemperature { get; init; }
    public required double CoolerPower { get; init; }
    public required bool CoolerOn { get; init; }
    public required bool IsExposing { get; init; }
    public required bool CanSetTemperature { get; init; }
    public required int BinX { get; init; }
    public required int BinY { get; init; }
    public required short Gain { get; init; }
    public required int Offset { get; init; }

    public static async Task<NinaCameraInfoDto> FromDriverAsync(ICameraDriver driver, CancellationToken ct)
    {
        var state = await driver.GetCameraStateAsync(ct);
        return new NinaCameraInfoDto
        {
            Connected = driver.Connected,
            Name = driver.Name,
            // A driver that cannot report a value (or reports it as unknown) yields NaN, which JSON
            // cannot express -- see JsonNumber. This 500'd /v2/api/equipment/camera/info for any
            // camera without a temperature sensor, and for every disconnected camera.
            Temperature = JsonNumber.Finite(driver.CanGetCCDTemperature ? await driver.GetCCDTemperatureAsync(ct) : double.NaN),
            TargetTemperature = JsonNumber.Finite(driver.CanSetCCDTemperature ? await driver.GetSetCCDTemperatureAsync(ct) : double.NaN),
            CoolerPower = JsonNumber.Finite(driver.CanGetCoolerPower ? await driver.GetCoolerPowerAsync(ct) : 0),
            CoolerOn = driver.CanGetCoolerOn && await driver.GetCoolerOnAsync(ct),
            IsExposing = state == CameraState.Exposing,
            CanSetTemperature = driver.CanSetCCDTemperature,
            BinX = driver.BinX,
            BinY = driver.BinY,
            Gain = await driver.GetGainAsync(ct),
            Offset = await driver.GetOffsetAsync(ct),
        };
    }

    public static NinaCameraInfoDto Disconnected { get; } = new NinaCameraInfoDto
    {
        Connected = false, Name = "", Temperature = 0, TargetTemperature = 0,
        CoolerPower = 0, CoolerOn = false, IsExposing = false, CanSetTemperature = false,
        BinX = 1, BinY = 1, Gain = 0, Offset = 0,
    };
}
