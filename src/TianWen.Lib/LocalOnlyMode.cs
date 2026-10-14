using System;

namespace TianWen.Lib;

/// <summary>
/// Whether this process binds nothing but loopback and the node's own socket: <c>TIANWEN_LOCAL_ONLY=1</c> in its
/// environment, or <c>tianwen-server --local-only</c>. Windows Defender Firewall asks once per executable PATH when a
/// process binds a non-loopback address, and never for loopback, so a build in a fresh folder (a worktree, a scratch
/// publish) asks again unless it is local-only. That is what an agent's launcher turns it on for
/// (<c>tools/start-app.ps1</c>); the default stays off, since WiFi cameras and mounts, Alpaca discovery and LAN rig
/// sharing are features.
/// <para>
/// Decided ONCE per process, here: the environment is read on first access and frozen, as
/// <see cref="TianWenDataRoot"/> and <c>StartupTimeOverride</c> are, and the server ORs its command line in. Everything
/// that would bind a network address is handed the decision (through DI, or by the host that builds it), never reads
/// the environment itself. In the mode:
/// </para>
/// <list type="bullet">
/// <item>the node listens on its socket only (no TCP 1888, no LAN announcement), whatever the share setting;</item>
/// <item>a device source skips its NETWORK discovery and keeps the rest (Canon over USB and WPD, OnStep and Skywatcher
/// over serial); Alpaca discovery asks loopback only, so a simulator on this machine is still found;</item>
/// <item>a WiFi Skywatcher mount, the one device reached over UDP, is not connected; a device reached as a TCP client
/// (OnStep or Canon over WiFi, Alpaca, PHD2) opens no listener and is kept;</item>
/// <item>the GUI runs no LAN discovery (its Home tab finds no LAN rig, a rig bound by address is still reached);</item>
/// <item>a node a client starts is started local-only too (<c>LocalNodeLauncher</c>).</item>
/// </list>
/// The DEBUG inspectors are left alone: from DIR.Lib 11.21 their default discovery is a socket-free file registry, and
/// turning it off would take the app out of that registry as well.
/// </summary>
public sealed record LocalOnlyMode(bool IsOn)
{
    /// <summary>Set to <c>1</c> (or <c>true</c>) to make a process, and any node it starts, local-only.</summary>
    public const string EnvironmentVariable = "TIANWEN_LOCAL_ONLY";

    /// <summary>The server's command-line switch for the same mode, which a client passes to a node it starts.</summary>
    public const string Argument = "--local-only";

    public static LocalOnlyMode Off { get; } = new LocalOnlyMode(false);

    public static LocalOnlyMode On { get; } = new LocalOnlyMode(true);

    // Frozen on first access, like the data root: a process is in the mode for its whole life or not at all.
    private static readonly LocalOnlyMode _fromEnvironment = FromEnvironmentValue(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// This process's mode: its environment's, or on when <paramref name="toldOnTheCommandLine"/> (the server's
    /// <see cref="Argument"/>). A client has no command line for it and asks with none.
    /// </summary>
    public static LocalOnlyMode OfThisProcess(bool toldOnTheCommandLine = false) => toldOnTheCommandLine ? On : _fromEnvironment;

    /// <summary>The mode <see cref="EnvironmentVariable"/>'s value asks for: on for <c>1</c> or <c>true</c>, off for anything else.</summary>
    internal static LocalOnlyMode FromEnvironmentValue(string? value) =>
        value?.Trim() is { } set && (set == "1" || set.Equals("true", StringComparison.OrdinalIgnoreCase)) ? On : Off;

    /// <summary>
    /// Whether this process may bind a network address that is not loopback: listen on the LAN, join a multicast group,
    /// or broadcast. The one question every such site asks.
    /// </summary>
    public bool MayBindTheNetwork => !IsOn;
}
