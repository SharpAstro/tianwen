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
        foreach (var identity in new[] { DesktopAppIdentity.Gui, DesktopAppIdentity.Viewer, DesktopAppIdentity.Workbench })
        {
            ReverseDns.IsMatch(identity.Id).ShouldBeTrue($"{identity.Id} is reverse-DNS, lower case, no spaces");
            identity.Id.Length.ShouldBeLessThanOrEqualTo(128);
        }

        DesktopAppIdentity.Gui.Id.ShouldNotBe(DesktopAppIdentity.Viewer.Id, "two apps, two taskbar groups");
        DesktopAppIdentity.Workbench.Id.ShouldNotBe(DesktopAppIdentity.Viewer.Id, "two apps, two taskbar groups");
        DesktopAppIdentity.Workbench.Id.ShouldNotBe(DesktopAppIdentity.Gui.Id, "two apps, two taskbar groups");
    }

    [Fact]
    public void SdlIsToldTheSameIdentity()
    {
        var metadata = DesktopAppIdentity.Viewer.SdlMetadataFor(packaged: false);
        metadata.Identifier.ShouldBe(DesktopAppIdentity.Viewer.Id);
        metadata.Name.ShouldBe(DesktopAppIdentity.Viewer.DisplayName);
        metadata.Version.ShouldBeNull("the entry assembly's version is SDL's default, and the right one");
    }

    [Fact]
    public void SdlLeavesTheIdentifierToThePackageInsideOne()
    {
        DesktopAppIdentity.Viewer.SdlMetadataFor(packaged: true).Identifier.ShouldBeNull();
        DesktopAppIdentity.Viewer.SdlMetadataFor(packaged: true).Name.ShouldBe(DesktopAppIdentity.Viewer.DisplayName);
        DesktopAppIdentity.Viewer.SdlMetadataFor(packaged: false).Identifier.ShouldBe(DesktopAppIdentity.Viewer.Id);
    }

    [Theory]
    [Trait("Category", "Invariant")]
    [InlineData("TianWen.UI.Gui")]
    [InlineData("TianWen.UI.FitsViewer")]
    [InlineData("TianWen.UI.Workbench")]
    public void EachAppClaimsItsIdentityBeforeItsFirstWindowAndTellsSdl(string project)
    {
        if (ChromeMeasuresThroughTheEngineTests.FindSourceRoot() is not { } src)
        {
            Assert.Skip("the sources are not beside the binaries here");
            return;
        }

        var text = File.ReadAllText(Path.Combine(src, project, "Program.cs"));
        var claim = text.IndexOf("AppIdentity.Claim(", System.StringComparison.Ordinal);
        var create = text.IndexOf("SdlVulkanWindow.Create(", System.StringComparison.Ordinal);

        claim.ShouldBeGreaterThan(-1, $"{project} claims its app identity");
        create.ShouldBeGreaterThan(-1);
        claim.ShouldBeLessThan(create, "Windows gives a window its process's app id when the window is created");
        var call = text.Substring(create, text.IndexOf(';', create) - create);
        call.ShouldContain("appMetadata: appIdentity.SdlMetadataFor(");
        call.ShouldContain("startMenuShortcut == StartMenuShortcut.Packaged", Case.Sensitive,
            "SDL gets no identifier inside the Store package, or the id splits from the package's own");
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
