using System;
using System.Globalization;

namespace TianWen.Lib.Devices.Skywatcher;

/// <summary>
/// Skywatcher mount model IDs as reported by the :e firmware response.
/// Values from GSServer's McModel enum (SharedResources.cs).
/// </summary>
internal enum SkywatcherMountModel
{
    Eq6 = 0x00,
    Heq5 = 0x01,
    Eq5 = 0x02,
    Eq3 = 0x03,
    Eq8 = 0x04,
    AzEq6 = 0x05,
    AzEq5 = 0x06,
    StarAdventurer = 0x07,
    StarAdventurerMini = 0x08,
    StarAdventurerGTi = 0x0C,
    Eqm35 = 0x1A,
    Eq8R = 0x20,
    AzEq6R = 0x22,
    Eq6R = 0x23,
    Neq6Pro = 0x24,
    Heq5R = 0x26,
    Eq3Gti = 0x30,
    Eq5Gti = 0x31,
    Heq5A = 0x38,
    A80Gt = 0x80,
    A114Gt = 0x82,
    StarDiscovery = 0xA2,
    AzGTi = 0xA5,
}

/// <summary>
/// Firmware info parsed from the :e response.
/// IntVersion = (modelByte &lt;&lt; 16) | (minor &lt;&lt; 8) | major.
/// </summary>
internal readonly record struct SkywatcherFirmwareInfo(SkywatcherMountModel MountModel, int IntVersion, string VersionString);

/// <summary>
/// Capability flags parsed from the :q1 010000 response.
/// </summary>
internal readonly record struct SkywatcherCapabilities(
    bool IsPPecTraining,
    bool IsPPecOn,
    bool CanDualEncoders,
    bool CanPPec,
    bool CanHomeSensors,
    bool CanAzEq,
    bool CanPolarLed,
    bool CanAxisSlewsIndependent,
    bool CanHalfCurrentTracking,
    bool CanWifi);

/// <summary>
/// Pure static helpers for the Skywatcher motor controller protocol.
/// Wire format: ASCII, commands ":CMD AXIS [DATA]\r", responses "=DATA\r" or "!ERR\r".
/// Data is hex ASCII, little-endian byte order. 24-bit positions offset by 0x800000.
/// </summary>
internal static class SkywatcherProtocol
{
    internal const int POSITION_OFFSET = 0x800000;
    internal const int DEFAULT_LEGACY_BAUD = 9600;
    internal const int DEFAULT_USB_BAUD = 115200;
    internal const int WIFI_PORT = 11880;

    /// <summary>
    /// Encode a 24-bit unsigned value to 6-char hex string in little-endian byte order.
    /// Example: 0x800000 → "000008"
    /// </summary>
    internal static string EncodeUInt24(uint value)
    {
        var b0 = (byte)(value & 0xFF);
        var b1 = (byte)((value >> 8) & 0xFF);
        var b2 = (byte)((value >> 16) & 0xFF);
        return $"{b0:X2}{b1:X2}{b2:X2}";
    }

    /// <summary>
    /// Decode a 6-char hex string in little-endian byte order to a 24-bit unsigned value.
    /// Example: "000008" → 0x800000
    /// </summary>
    internal static uint DecodeUInt24(ReadOnlySpan<char> hex)
    {
        if (hex.Length < 6)
        {
            throw new ArgumentException("Expected 6 hex characters", nameof(hex));
        }

        var b0 = byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b1 = byte.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b2 = byte.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (uint)(b0 | (b1 << 8) | (b2 << 16));
    }

    /// <summary>
    /// Encode a signed position to 6-char hex string.
    /// Position is offset by 0x800000 (home = 0x800000).
    /// </summary>
    internal static string EncodePosition(int steps)
    {
        return EncodeUInt24((uint)(steps + POSITION_OFFSET));
    }

    /// <summary>
    /// Decode a 6-char hex string to a signed position.
    /// </summary>
    internal static int DecodePosition(ReadOnlySpan<char> hex)
    {
        return (int)DecodeUInt24(hex) - POSITION_OFFSET;
    }

    /// <summary>
    /// Compute T1 preset for a given speed.
    /// T1_Preset = [N ×] TMR_Freq × 360 / SpeedDegPerSec / CPR
    /// When highSpeed is true, multiply by highSpeedRatio (N).
    /// </summary>
    internal static uint ComputeT1Preset(uint tmrFreq, uint cpr, double speedDegPerSec, bool highSpeed, uint highSpeedRatio)
    {
        var preset = (double)tmrFreq * 360.0 / speedDegPerSec / cpr;
        if (highSpeed)
        {
            preset *= highSpeedRatio;
        }
        return (uint)Math.Round(preset);
    }

    /// <summary>
    /// Build a Skywatcher protocol command: :CMD AXIS [DATA]\r
    /// </summary>
    internal static byte[] BuildCommand(char cmd, char axis, string? data = null)
    {
        if (data is null)
        {
            // :<cmd><axis>\r  = 3 bytes
            return [(byte)':', (byte)cmd, (byte)axis, (byte)'\r'];
        }
        else
        {
            // :<cmd><axis><data>\r
            var result = new byte[3 + data.Length + 1];
            result[0] = (byte)':';
            result[1] = (byte)cmd;
            result[2] = (byte)axis;
            for (var i = 0; i < data.Length; i++)
            {
                result[3 + i] = (byte)data[i];
            }
            result[^1] = (byte)'\r';
            return result;
        }
    }

    /// <summary>
    /// Try to parse a Skywatcher response. Valid responses start with '=' (ok) or '!' (error).
    /// Returns the data portion (after '=' and before '\r' if present).
    /// </summary>
    internal static bool TryParseResponse(string? response, out string data)
    {
        if (response is { Length: > 0 } && response[0] == '=')
        {
            data = response[1..];
            return true;
        }
        data = string.Empty;
        return false;
    }

    /// <summary>
    /// Parse firmware response from :e command.
    /// Response data: 6 hex chars representing 3 bytes: [boardVersion, minor, major].
    /// IntVersion = (boardVersion &lt;&lt; 16) | (minor &lt;&lt; 8) | major.
    /// MountModel is the high nibble of boardVersion.
    /// </summary>
    internal static bool TryParseFirmwareResponse(string responseData, out SkywatcherFirmwareInfo info)
    {
        if (responseData.Length >= 6)
        {
            var raw = DecodeUInt24(responseData.AsSpan(0, 6));
            // raw bytes: b0=boardVersion, b1=minor, b2=major (little-endian decode)
            var boardVersion = (int)(raw & 0xFF);
            var minor = (int)((raw >> 8) & 0xFF);
            var major = (int)((raw >> 16) & 0xFF);

            var mountModel = (SkywatcherMountModel)boardVersion;
            var intVersion = (boardVersion << 16) | (minor << 8) | major;
            var versionString = $"{major}.{minor:D2}";

            info = new SkywatcherFirmwareInfo(mountModel, intVersion, versionString);
            return true;
        }

        info = default;
        return false;
    }

    /// <summary>
    /// Parse capability flags from :q1 010000 response.
    /// Response data: 6 hex chars, each nibble = 4 flag bits.
    /// </summary>
    internal static SkywatcherCapabilities ParseCapabilities(string responseData)
    {
        if (responseData.Length < 6)
        {
            return default;
        }

        var raw = DecodeUInt24(responseData.AsSpan(0, 6));
        // raw bytes: b0 (nibble0 low, nibble1 high), b1 (nibble2 low, nibble3 high), b2 (nibble4 low, nibble5 high)
        // But we decode as 6 hex chars = 3 bytes LE.
        // hex[0..2] = b0 (least significant byte), hex[2..4] = b1, hex[4..6] = b2
        // Each byte has two nibbles, but the protocol doc says 6 nibbles from left to right
        // So let's just parse nibbles directly from the hex string
        var n0 = ParseHexNibble(responseData[0]);
        var n1 = ParseHexNibble(responseData[1]);
        var n2 = ParseHexNibble(responseData[2]);
        var n3 = ParseHexNibble(responseData[3]);
        var n4 = ParseHexNibble(responseData[4]);
        var n5 = ParseHexNibble(responseData[5]);

        // Per plan: nibble 0 = runtime PEC state, nibble 1 = hardware caps, nibble 2 = more caps
        // But the wire format is little-endian bytes, so the first two hex chars = byte0 (LE)
        // The response data from :q is actually the 6-char hex in the order the mount sends them
        // Looking at GSServer: the response is parsed as 6 hex chars directly, not LE-decoded.
        // So we parse the chars in order:
        return new SkywatcherCapabilities(
            IsPPecTraining: (n0 & 0x1) != 0,
            IsPPecOn: (n0 & 0x2) != 0,
            CanDualEncoders: (n1 & 0x1) != 0,
            CanPPec: (n1 & 0x2) != 0,
            CanHomeSensors: (n1 & 0x4) != 0,
            CanAzEq: (n1 & 0x8) != 0,
            CanPolarLed: (n2 & 0x1) != 0,
            CanAxisSlewsIndependent: (n2 & 0x2) != 0,
            CanHalfCurrentTracking: (n2 & 0x4) != 0,
            CanWifi: (n2 & 0x8) != 0
        );
    }

    /// <summary>
    /// Determine whether advanced 32-bit commands are supported based on firmware version and mount model.
    /// </summary>
    internal static bool SupportsAdvancedCommands(int intVersion, SkywatcherMountModel mountModel)
    {
        // Star Adventurer with exact version 0x038207 — excluded
        if (intVersion == 0x038207)
        {
            return false;
        }

        // AZ-GTi requires >= 3.40
        if (mountModel == SkywatcherMountModel.AzGTi)
        {
            return intVersion > (((int)SkywatcherMountModel.AzGTi << 16) | (40 << 8) | 3);
        }

        // General: > 0x032200 (fw > 3.22 on any mount)
        return intVersion > 0x032200;
    }

    /// <summary>
    /// Override gear ratio for 80GT/114GT mounts.
    /// </summary>
    internal static uint OverrideGearRatio(uint rawCpr, SkywatcherMountModel model) => model switch
    {
        SkywatcherMountModel.A80Gt => 78_848,
        SkywatcherMountModel.A114Gt => 78_848,
        _ => rawCpr
    };

    /// <summary>
    /// Guide speed fraction for index 0-4.
    /// 0=1.0x, 1=0.75x, 2=0.5x, 3=0.25x, 4=0.125x sidereal.
    /// </summary>
    internal static double GuideSpeedFraction(int index) => index switch
    {
        0 => 1.0,
        1 => 0.75,
        2 => 0.5,
        3 => 0.25,
        4 => 0.125,
        _ => 0.5
    };

    private static int ParseHexNibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => 0
    };
}

internal static class SkywatcherMountModelExtensions
{
    extension(SkywatcherMountModel model)
    {
        /// <summary>
        /// Display name for the mount model.
        /// </summary>
        public string DisplayName => model switch
        {
            SkywatcherMountModel.Eq6 => "EQ6",
            SkywatcherMountModel.Heq5 => "HEQ5",
            SkywatcherMountModel.Eq5 => "EQ5",
            SkywatcherMountModel.Eq3 => "EQ3",
            SkywatcherMountModel.Eq8 => "EQ8",
            SkywatcherMountModel.AzEq6 => "AZ-EQ6",
            SkywatcherMountModel.AzEq5 => "AZ-EQ5",
            SkywatcherMountModel.StarAdventurer => "Star Adventurer",
            SkywatcherMountModel.StarAdventurerMini => "Star Adventurer Mini",
            SkywatcherMountModel.StarAdventurerGTi => "Star Adventurer GTi",
            SkywatcherMountModel.Eqm35 => "EQM-35",
            SkywatcherMountModel.Eq8R => "EQ8-R",
            SkywatcherMountModel.AzEq6R => "AZ-EQ6",
            SkywatcherMountModel.Eq6R => "EQ6-R",
            SkywatcherMountModel.Neq6Pro => "NEQ6 PRO",
            SkywatcherMountModel.Heq5R => "HEQ5R",
            SkywatcherMountModel.Eq3Gti => "EQ3",
            SkywatcherMountModel.Eq5Gti => "EQ5",
            SkywatcherMountModel.Heq5A => "HEQ5",
            SkywatcherMountModel.A80Gt => "80GT",
            SkywatcherMountModel.A114Gt => "114GT",
            SkywatcherMountModel.StarDiscovery => "Star Discovery",
            SkywatcherMountModel.AzGTi => "AZ-GTi",
            _ => $"Unknown (0x{(int)model:X2})"
        };

        /// <summary>
        /// Adjust goto steps for StarDiscovery small-step workaround.
        /// Steps below 10 are clamped to 10. Other models pass through unchanged.
        /// </summary>
        public int AdjustGotoSteps(int steps) => model == SkywatcherMountModel.StarDiscovery && steps < 10 ? 10 : steps;
    }
}
