using Shouldly;
using System;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for <see cref="DeviceQueryKeyExtensions.WithQueryValues"/> — the upsert helper used
/// to mirror session-inferred state (backlash, etc.) back into device URIs without disturbing
/// existing user-edited query keys.
/// </summary>
public class UriQueryUpsertTests
{
    [Fact]
    public void GivenNoUpdatesWhenWithQueryValuesThenReturnsSameInstance()
    {
        var uri = new Uri("focuser://Fake/123?focuserBacklashIn=50&filter1=Lum");

        var result = uri.WithQueryValues();

        result.ShouldBeSameAs(uri);
    }

    [Fact]
    public void GivenNewKeyWhenUpsertThenAppended()
    {
        var uri = new Uri("focuser://Fake/123?filter1=Lum");

        var result = uri.WithQueryValues(("focuserBacklashIn", "75"));

        result.QueryValue(DeviceQueryKey.FocuserBacklashIn).ShouldBe("75");
        result.Query.ShouldContain("filter1=Lum"); // pre-existing key preserved
    }

    [Fact]
    public void GivenExistingKeyWhenUpsertThenReplaced()
    {
        var uri = new Uri("focuser://Fake/123?focuserBacklashIn=50&filter1=Lum");

        var result = uri.WithQueryValues(("focuserBacklashIn", "80"));

        result.QueryValue(DeviceQueryKey.FocuserBacklashIn).ShouldBe("80");
        result.Query.ShouldContain("filter1=Lum");
    }

    [Fact]
    public void GivenMultipleUpdatesWhenUpsertThenAllApplied()
    {
        var uri = new Uri("focuser://Fake/123?filter1=Lum");

        var result = uri.WithQueryValues(
            ("focuserBacklashIn", "75"),
            ("focuserBacklashOut", "60"));

        result.QueryValue(DeviceQueryKey.FocuserBacklashIn).ShouldBe("75");
        result.QueryValue(DeviceQueryKey.FocuserBacklashOut).ShouldBe("60");
        result.Query.ShouldContain("filter1=Lum");
    }

    [Fact]
    public void GivenNullValueWhenUpsertThenKeyRemoved()
    {
        var uri = new Uri("focuser://Fake/123?focuserBacklashIn=50&filter1=Lum");

        var result = uri.WithQueryValues(("focuserBacklashIn", null));

        result.QueryValue(DeviceQueryKey.FocuserBacklashIn).ShouldBeNull();
        result.Query.ShouldContain("filter1=Lum");
    }

    [Fact]
    public void GivenAuthorityAndPathWhenUpsertThenPreserved()
    {
        var uri = new Uri("focuser://FakeFocuser/abc-123?existing=keep");

        var result = uri.WithQueryValues(("focuserBacklashIn", "100"));

        result.Scheme.ShouldBe("focuser");
        result.Host.ShouldBe("fakefocuser");
        result.AbsolutePath.ShouldBe("/abc-123");
    }
}
