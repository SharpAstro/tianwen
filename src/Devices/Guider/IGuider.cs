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

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;

namespace Astap.Lib.Devices.Guider;

public class SettleProgress
{
    public bool Done { get; set; }
    public double Distance { get; set; }
    public double SettlePx { get; set; }
    public double Time { get; set; }
    public double SettleTime { get; set; }
    public int Status { get; set; }
    public string? Error { get; set; }
}

public class GuideStats
{
    public double rms_tot { get; set; }
    public double rms_ra { get; set; }
    public double rms_dec { get; set; }
    public double peak_ra { get; set; }
    public double peak_dec { get; set; }

    public GuideStats Clone() => (GuideStats)MemberwiseClone();
}

public class SettleRequest
{
    public SettleRequest(double pixels, double time, double timeout)
    {
        Pixels = pixels;
        Time = time;
        Timeout = timeout;
    }

    public double Pixels { get; set; }
    public double Time { get; set; }
    public double Timeout { get; set; }
}

[Serializable]
public class GuiderException : Exception
{
    public GuiderException(string message) : base(message) { }
    public GuiderException(string message, Exception? inner) : base(message, inner) { }

    protected GuiderException(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) : base(info, context)
    {

    }
}

public interface IGuider : IDisposable
{
    /// <summary>
    /// connect to PHD2 -- you'll need to call Connect before calling any of the server API methods below
    /// </summary>
    void Connect();

    /// <summary>
    /// disconnect from PHD2
    /// </summary>
    void Close();

    /// <summary>
    /// True if connected to event monitoring port of PHD2.
    /// </summary>
    bool IsConnected { get; }

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
    /// guide star selection, and settling. Call CheckSettling() periodically to see when settling
    /// is complete.
    /// </summary>
    /// <param name="settlePixels">settle threshold in pixels</param>
    /// <param name="settleTime">settle time in seconds</param>
    /// <param name="settleTimeout">settle timeout in seconds</param>
    void Guide(double settlePixels, double settleTime, double settleTimeout);

    /// <summary>
    /// Dither guiding with the given dither amount and settling parameters. Call <see cref="CheckSettling()"/>
    /// periodically to see when settling is complete.
    /// </summary>
    /// <param name="ditherPixels"></param>
    /// <param name="settlePixels"></param>
    /// <param name="settleTime"></param>
    /// <param name="settleTimeout"></param>
    void Dither(double ditherPixels, double settlePixels, double settleTime, double settleTimeout, bool raOnly = false);

    /// <summary>
    /// Check if phd2 is currently in the process of settling after a Guide or Dither
    /// </summary>
    /// <returns></returns>
    bool IsSettling();

    /// <summary>
    /// Get the progress of settling
    /// </summary>
    /// <returns></returns>
    SettleProgress CheckSettling();

    // Get the guider statistics since guiding started. Frames captured while settling is in progress
    // are excluded from the stats.
    GuideStats? GetStats();

    // stop looping and guiding
    void StopCapture(uint timeoutSeconds = 10);

    /// <summary>
    /// start looping exposures
    /// </summary>
    /// <param name="timeoutSeconds">timeout after looping attempt is cancelled</param>
    void Loop(uint timeoutSeconds = 10);

    /// <summary>
    /// get the guider pixel scale in arc-seconds per pixel
    /// </summary>
    /// <returns>pixel scale of the guiding camera in arc-seconds per pixel</returns>
    double PixelScale();

    /// <summary>
    /// get the exposure time of each looping exposure.
    /// </summary>
    /// <returns>exposure time</returns>
    TimeSpan ExposureTime();

    /// <summary>
    /// get a list of the Equipment Profile names
    /// </summary>
    /// <returns></returns>
    List<string> GetEquipmentProfiles();

    /// <summary>
    /// connect the the specified profile as constructed.
    /// </summary>
    void ConnectEquipment();

    /// <summary>
    /// disconnect equipment
    /// </summary>
    void DisconnectEquipment();

    /// <summary>
    /// get the AppState (https://github.com/OpenPHDGuiding/phd2/wiki/EventMonitoring#appstate)
    /// and current guide error
    /// </summary>
    /// <param name="appState">application runtime state</param>
    /// <param name="avgDist">a smoothed average of the guide distance in pixels</param>
    void GetStatus(out string? appState, out double avgDist);

    /// <summary>
    /// check if currently guiding
    /// </summary>
    /// <returns></returns>
    bool IsGuiding();

    /// <summary>
    /// pause guiding (looping exposures continues)
    /// </summary>
    void Pause();

    /// <summary>
    /// un-pause guiding
    /// </summary>
    void Unpause();

    /// <summary>
    /// save the current guide camera frame (FITS format), returning the name of the file.
    /// The caller will need to remove the file when done.
    /// </summary>
    /// <returns></returns>
    string? SaveImage();

    /// <summary>
    /// Event that is triggered when an unknown event is received from the guiding application.
    /// </summary>
    event EventHandler<UnhandledEventArgs>? UnhandledEvent;

    /// <summary>
    /// Event that is triggered when an exception occurs.
    /// </summary>
    event EventHandler<GuidingErrorEventArgs>? GuidingErrorEvent;
}

public class UnhandledEventArgs : EventArgs
{
    public UnhandledEventArgs(string @event, string payload)
    {
        Event = @event;
        Payload = payload;
    }

    public string Event { get; }

    public string Payload { get; }
}

public class GuidingErrorEventArgs : EventArgs
{
    public GuidingErrorEventArgs(string msg, Exception? ex = null)
    {
        Message = msg;
        Exception = ex;
    }

    public string Message { get; }

    public Exception? Exception { get; }
}

class GuiderConnection : IDisposable
{
    static readonly byte[] CRLF = new byte[2] { (byte)'\r', (byte)'\n' };

    private TcpClient? _tcpClient;
    private StreamReader? _streamReader;

    public GuiderConnection()
    {
        // empty
    }

    public void Connect(string hostname, ushort port)
    {
        try
        {
            _tcpClient = new TcpClient(hostname, port);
            _streamReader = new StreamReader(_tcpClient.GetStream());
        }
        catch
        {
            Close();
            throw;
        }
    }

    public virtual void OnConnectionError(GuidingErrorEventArgs eventArgs)
    {

    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        Dispose(true);
    }

    public void Close() => Dispose();

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _streamReader?.Close();
            _streamReader?.Dispose();
            _streamReader = null;

            _tcpClient?.Close();
            _tcpClient = null;
        }
    }

    public bool IsConnected => _tcpClient != null && _tcpClient.Connected;

    public string? ReadLine() => _streamReader?.ReadLine();

    public void WriteLine(ReadOnlyMemory<byte> jsonlUtf8Bytes)
    {
        var stream = _tcpClient?.GetStream();
        if (stream != null && stream.CanWrite)
        {
            stream.Write(jsonlUtf8Bytes.Span);
            stream.Write(CRLF);
            stream.Flush();
        }
    }

    public void Terminate() => _tcpClient?.Close();
}

class Accum
{
    uint n;
    double a;
    double q;
    double peak;

    public Accum() {
        Reset();
    }
    public void Reset() {
        n = 0;
        a = q = peak = 0;
    }
    public void Add(double x) {
        double ax = Math.Abs(x);
        if (ax > peak) peak = ax;
        ++n;
        double d = x - a;
        a += d / n;
        q += (x - a) * d;
    }
    public double Mean() {
        return a;
    }
    public double Stdev() {
        return n >= 1 ? Math.Sqrt(q / n) : 0.0;
    }
    public double Peak() {
        return peak;
    }
}