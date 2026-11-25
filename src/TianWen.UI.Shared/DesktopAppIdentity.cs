using SdlVulkan.Renderer;

namespace TianWen.UI.Shared;

/// <summary>
/// Who each desktop app is to the operating system, written once for every place that says it.
///
/// <para>One <see cref="Id"/> per app, reverse-DNS, serves three readers: Windows, where it is the app id
/// (AppUserModelID) that SharpAstro.AppShell's <c>AppIdentity.Claim</c> gives the process and its Start menu
/// shortcut, so the taskbar groups the windows and the shell can name the app; SDL, which hands it to Wayland
/// (<c>app_id</c>), X11 (<c>WM_CLASS</c>) and macOS, and on Windows registers its own desktop notifications under it
/// (in the SDL releases that have them); and the macOS bundle's <c>CFBundleIdentifier</c> (<c>packaging/macos</c>).
/// One string for all of them keeps such a notification in the same group as the app's windows. <see cref="DisplayName"/> is the shortcut's name and the bundle's
/// <c>CFBundleName</c>; the viewer's is also its Store package's display name.
/// <c>DesktopAppIdentityTests</c> holds these to the packaging files.</para>
///
/// <para>Inside the Store package the package's manifest names the app, and <c>Claim</c> leaves it alone (AppShell
/// 1.5).</para>
/// </summary>
/// <param name="Id">Reverse-DNS, lower case, no spaces: the same on every launch and every platform.</param>
/// <param name="DisplayName">The name the shell shows.</param>
public sealed record DesktopAppIdentity(string Id, string DisplayName)
{
    /// <summary><c>tianwen-gui</c>.</summary>
    public static DesktopAppIdentity Gui { get; } = new("com.sharpastro.tianwen.gui", "TianWen");

    /// <summary><c>tianwen-fits</c>, published in the Store as Astro Photo Viewer.</summary>
    public static DesktopAppIdentity Viewer { get; } = new("com.sharpastro.tianwen.fits", "Astro Photo Viewer");

    /// <summary>The same identity for SDL, which applies it before <c>SDL_Init</c>; the version stays the entry
    /// assembly's.</summary>
    public SdlAppMetadata SdlMetadata => new(Name: DisplayName, Identifier: Id, Creator: "SharpAstro",
        Url: "https://github.com/SharpAstro/tianwen");
}
