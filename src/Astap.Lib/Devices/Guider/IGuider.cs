/*

MIT License

Copyright (c) 2018 Andy Galasso

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/

using Astap.Lib.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Astap.Lib.Devices.Guider;

public interface IGuider : IDeviceDriver
{
    /// <summary>
    /// support raw JSONRPC method invocation. Generally you won't need to
    /// use this function as it is much more convenient to use the higher-level methods below
    /// </summary>
    /// <param name="method"></param>
    /// <param name="params"></param>
    /// <returns></returns>
    JsonDocument Call(string method, params object[] @params);

    /// <summary>
    /// Start guiding with the given settling parameters. PHD2 takes care of looping exposures,
    /// guide star selection, and settling. Call <see cref="TryGetSettleProgress(out SettleProgress?)"/> periodically to see when settling
    /// is complete.
    /// </summary>
    /// <param name="settlePixels">settle threshold in pixels</param>
    /// <param name="settleTime">settle time in seconds</param>
    /// <param name="settleTimeout">settle timeout in seconds</param>
    void Guide(double settlePixels, double settleTime, double settleTimeout);

    /// <summary>
    /// Dither guiding with the given dither amount and settling parameters. Call <see cref="TryGetSettleProgress(out SettleProgress?)"/> or <see cref="IsSettling()"/>
    /// periodically to see when settling is complete.
    /// </summary>
    /// <param name="ditherPixels"></param>
    /// <param name="settlePixels"></param>
    /// <param name="settleTime"></param>
    /// <param name="settleTimeout"></param>
    void Dither(double ditherPixels, double settlePixels, double settleTime, double settleTimeout, bool raOnly = false);

    /// <summary>
    /// CHecks if phd2 is currently looping exposures
    /// </summary>
    /// <returns></returns>
    bool IsLooping();

    /// <summary>
    /// Check if guider is currently in the process of settling after a Guide or Dither.
    /// A simplified version of <see cref="TryGetSettleProgress(out SettleProgress?)"/>
    /// </summary>
    /// <returns>true if settling is in progress.</returns>
    /// <exception cref="GuiderException">Throws if not connected or command</exception>
    bool IsSettling();

    /// <summary>
    /// Returns true if settling is in progress and additional information in <paramref name="settleProgress"/>
    /// </summary>
    /// <param name="settleProgress"></param>
    /// <returns>true if still settling</returns>
    /// <exception cref="GuiderException">Throws if not connected or command</exception>
    public bool TryGetSettleProgress([NotNullWhen(true)] out SettleProgress? settleProgress);

    /// <summary>
    /// Get the guider statistics since guiding started. Frames captured while settling is in progress
    /// are excluded from the stats.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="GuiderException">Throws if not connected</exception>
    GuideStats? GetStats();

    /// <summary>
    /// stop looping and guiding
    /// </summary>
    /// <param name="timeoutSeconds">timeout after throwing exception</param>
    /// <param name="sleep">custom sleep function if any.</param>
    /// <exception cref="GuiderException">Throws if not connected or command could not be issued (see timeout)</exception>
    void StopCapture(uint timeoutSeconds = 10, Action<TimeSpan>? sleep = null);

    /// <summary>
    /// start looping exposures
    /// </summary>
    /// <param name="timeoutSeconds">timeout after looping attempt is cancelled</param>
    /// <returns>true if looping.</returns>
    /// <exception cref="GuiderException">Throws if not connected or command could not be issued</exception>
    bool Loop(uint timeoutSeconds = 10, Action<TimeSpan>? sleep = null);

    /// <summary>
    /// get the guider pixel scale in arc-seconds per pixel
    /// </summary>
    /// <returns>pixel scale of the guiding camera in arc-seconds per pixel</returns>
    /// <exception cref="GuiderException">Throws if not connected or command could not be issued</exception>
    double PixelScale();

    /// <summary>
    /// returns camera size in width, heiight (pixels)
    /// </summary>
    /// <returns>camera dimensions in pixel</returns>
    /// <exception cref="GuiderException">Throws if not connected or command could not be issued</exception>
    public (int width, int height)? CameraFrameSize();

    /// <summary>
    /// When true, <paramref name="dim"/> contains the image dimensions of the guiding exposure,
    /// <see cref="PixelScale()"/> and <see cref="CameraFrameSize()"/>.
    /// Might still throw exceptions when not connected.
    /// </summary>
    /// <param name="dim"></param>
    /// <returns>true if image dimensions could be obtained.</returns>
    /// <exception cref="GuiderException">Throws if not connected or command could not be issued</exception>
    public bool TryGetImageDim([NotNullWhen(true)] out ImageDim? dim)
        => (dim = CameraFrameSize() is var (width, height) && PixelScale() is var pixelScale and > 0
            ? new ImageDim(pixelScale, width, height)
            : default
        ) is not null;

    /// <summary>
    /// get the exposure time of each looping exposure.
    /// </summary>
    /// <returns>exposure time</returns>
    /// <exception cref="GuiderException">Throws if not connected or command could not be issued</exception>
    TimeSpan ExposureTime();

    /// <summary>
    /// Get a list of the equipment profile names
    /// </summary>
    /// <returns>List of profile names</returns>
    /// <exception cref="GuiderException">Throws if not connected or command could not be issued</exception>
    IReadOnlyList<string> GetEquipmentProfiles();

    /// <summary>
    /// Tries to obtain the active profile, useful for quick self-discovery.
    ///
    /// Assumes an active connection.
    /// </summary>
    /// <param name="activeProfileName"></param>
    /// <returns>true if <paramref name="activeProfileName"/> is the active profile (and not null)</returns>
    bool TryGetActiveProfileName([NotNullWhen(true)] out string? activeProfileName);

    /// <summary>
    /// connect the the specified profile as constructed.
    /// </summary>
    /// <exception cref="GuiderException">Throws if not connected or command could not be issued</exception>
    void ConnectEquipment();

    /// <summary>
    /// disconnect equipment
    /// </summary>
    /// <exception cref="GuiderException">Throws if not connected or command could not be issued</exception>
    void DisconnectEquipment();

    /// <summary>
    /// get the AppState (https://github.com/OpenPHDGuiding/phd2/wiki/EventMonitoring#appstate)
    /// and current guide error
    /// </summary>
    /// <param name="appState">application runtime state</param>
    /// <param name="avgDist">a smoothed average of the guide distance in pixels</param>
    /// <exception cref="GuiderException">Throws if not connected or command could not be issued</exception>
    void GetStatus(out string? appState, out double avgDist);

    /// <summary>
    /// check if currently guiding
    /// </summary>
    /// <returns></returns>
    /// <exception cref="GuiderException">Throws if not connected</exception>
    bool IsGuiding();

    /// <summary>
    /// pause guiding (looping exposures continues)
    /// </summary>
    /// <exception cref="GuiderException">Throws if not connected or command could not be issued</exception>
    void Pause();

    /// <summary>
    /// un-pause guiding.
    /// </summary>
    /// <exception cref="GuiderException">Throws if not connected or command could not be issued</exception>
    void Unpause();

    /// <summary>
    /// Save the current guide camera frame (FITS format), returning the name of the file.
    /// The caller will need to remove the file when done.
    /// It is advisable to use a subfolder of <see cref="System.IO.Path.GetTempPath"/>.
    /// THe implementation will copy the output file to <paramref name="outputFolder"/> and delete the temporary file created by the guider.
    /// &#x26A0; <em>This will only work as expected when the guider is on the same host.</em>.
    /// </summary>
    /// <returns>the full path of the output file if successfully captured.</returns>
    /// <exception cref="GuiderException">Throws if not connected or command could not be issued</exception>
    string? SaveImage(string outputFolder);

    /// <summary>
    /// Event that is triggered when an exception occurs.
    /// </summary>
    event EventHandler<GuidingErrorEventArgs>? GuidingErrorEvent;

    /// <summary>
    /// Event that is triggered when the application state changes.
    /// </summary>
    event EventHandler<GuiderStateChangedEventArgs>? GuiderStateChangedEvent;
}
