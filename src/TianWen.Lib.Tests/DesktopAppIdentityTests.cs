using System.IO;
using System.Text.RegularExpressions;
using Shouldly;
using TianWen.UI.Shared;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The desktop apps' identity is written once (<see cref="DesktopAppIdentity"/>) and repeated by the packaging: the macOS
/// bundles' <c>CFBundleIdentifier</c> and <c>CFBundleName</c>, and the Store package's display name for the viewer. A
/// rename in one place and not the other gives the same app two names, and on Windows two taskbar groups.
/// </summary>
public partial class DesktopAppIdentityTests
{
    [GeneratedRegex(@"<key>CFBundleIdentifier</key>\s*<string>(?<value>[^<]+)</string>")]
    private static partial Regex BundleIdentifier { get; }

    [GeneratedRegex(@"<key>CFBundleName</key>\s*<string>(?<value>[^<]+)</string>")]
    private static partial Regex BundleName { get; }

    // The package's own <Properties><DisplayName>, the first DisplayName element in the manifest.
    [GeneratedRegex(@"<DisplayName>(?<value>[^<]+)</DisplayName>")]
    private static partial Regex PackageDisplayName { get; }

    // Reverse-DNS: lower-case labels joined by dots. An AppUserModelID allows at most 128 characters and no spaces.
    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9-]+)+$")]
    private static partial Regex ReverseDns { get; }

    [Fact]
    public void EachIdIsReverseDnsAndFitsAWindowsAppId()
    {
        foreach (var identity in new[] { DesktopAppIdentity.Gui, DesktopAppIdentity.Viewer })
        {
            ReverseDns.IsMatch(identity.Id).ShouldBeTrue($"{identity.Id} is reverse-DNS, lower case, no spaces");
            identity.Id.Length.ShouldBeLessThanOrEqualTo(128);
        }

        DesktopAppIdentity.Gui.Id.ShouldNotBe(DesktopAppIdentity.Viewer.Id, "two apps, two taskbar groups");
    }

    [Fact]
    public void SdlIsToldTheSameIdentity()
    {
        var metadata = DesktopAppIdentity.Viewer.SdlMetadata;
        metadata.Identifier.ShouldBe(DesktopAppIdentity.Viewer.Id);
        metadata.Name.ShouldBe(DesktopAppIdentity.Viewer.DisplayName);
        metadata.Version.ShouldBeNull("the entry assembly's version is SDL's default, and the right one");
    }

    [Theory]
    [InlineData("tianwen-gui.Info.plist.in", nameof(DesktopAppIdentity.Gui))]
    [InlineData("tianwen-fits.Info.plist.in", nameof(DesktopAppIdentity.Viewer))]
    public void TheMacOsBundleSaysTheSame(string plist, string app)
    {
        if (ChromeMeasuresThroughTheEngineTests.FindSourceRoot() is not { } src)
        {
            Assert.Skip("the sources are not beside the binaries here");
            return;
        }

        var identity = app == nameof(DesktopAppIdentity.Gui) ? DesktopAppIdentity.Gui : DesktopAppIdentity.Viewer;
        var text = File.ReadAllText(Path.Combine(src, "..", "packaging", "macos", plist));

        BundleIdentifier.Match(text).Groups["value"].Value.ShouldBe(identity.Id);
        BundleName.Match(text).Groups["value"].Value.ShouldBe(identity.DisplayName);
    }

    [Fact]
    public void TheStorePackageNamesTheViewerTheSame()
    {
        if (ChromeMeasuresThroughTheEngineTests.FindSourceRoot() is not { } src)
        {
            Assert.Skip("the sources are not beside the binaries here");
            return;
        }

        var manifest = File.ReadAllText(Path.Combine(src, "..", "packaging", "windows", "msix", "AppxManifest.xml"));
        PackageDisplayName.Match(manifest).Groups["value"].Value.ShouldBe(DesktopAppIdentity.Viewer.DisplayName);
    }
}
