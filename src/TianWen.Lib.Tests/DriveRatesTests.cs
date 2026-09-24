using System;
using Shouldly;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="TrackingSpeed"/> starts at None = 0 while ASCOM and Alpaca DriveRates start at Sidereal = 0,
/// so a cast across the boundary put a mount asked for sidereal on lunar (#771). <see cref="DriveRates"/>
/// is the one mapping, and these pin it for every driver that uses it, ASCOM included.
/// </summary>
public class DriveRatesTests
{
    [Theory]
    [InlineData(TrackingSpeed.Sidereal, 0)]
    [InlineData(TrackingSpeed.Lunar, 1)]
    [InlineData(TrackingSpeed.Solar, 2)]
    [InlineData(TrackingSpeed.King, 3)]
    public void EverySpeedRoundTripsThroughItsDriveRate(TrackingSpeed speed, int driveRate)
    {
        DriveRates.ToDriveRate(speed).ShouldBe(driveRate);
        DriveRates.FromDriveRate(driveRate).ShouldBe(speed);
        DriveRates.FromDriveRate(DriveRates.ToDriveRate(speed)).ShouldBe(speed);
    }

    [Fact]
    public void SiderealIsZeroOnTheWire()
    {
        DriveRates.ToDriveRate(TrackingSpeed.Sidereal).ShouldBe(0);
        DriveRates.FromDriveRate(0).ShouldBe(TrackingSpeed.Sidereal);
    }

    [Fact]
    public void NoneHasNoDriveRate()
    {
        DriveRates.TryToDriveRate(TrackingSpeed.None, out _).ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => DriveRates.ToDriveRate(TrackingSpeed.None));
    }

    [Fact]
    public void AnUndefinedSpeedHasNoDriveRate()
    {
        DriveRates.TryToDriveRate((TrackingSpeed)42, out _).ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => DriveRates.ToDriveRate((TrackingSpeed)42));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void AnUnknownWireValueIsRefusedNotGuessed(int driveRate)
    {
        DriveRates.TryFromDriveRate(driveRate, out _).ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => DriveRates.FromDriveRate(driveRate));
    }
}
