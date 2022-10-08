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
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;

namespace Astap.Lib.Devices.Guider;

internal class PHD2GuiderDriver : IGuider, IDeviceSource<GuiderDevice>
{
    internal const string PHD2 = "PHD2";
    public static string DeviceType { get; } = PHD2;

    Thread? m_worker;
    volatile bool m_terminate;
    readonly object m_sync = new();
    JsonDocument? m_response;
    readonly GuiderDevice _guiderDevice;

    string Host { get; }
    uint Instance { get; }
    string ProfileName { get; }
    IGuiderConnection Connection { get; }
    Accum AccumRA { get; } = new Accum();
    Accum AccumDEC { get; } = new Accum();
    bool IsAccumActive { get; set; }
    double SettlePixels { get; set; }
    string? AppState { get; set; }
    double AverageDistance { get; set; }
    GuideStats? Stats { get; set; }
    string? Version { get; set; }
    string? PHDSubvVersion { get; set; }
    SettleProgress? Settle { get; set; }

    public PHD2GuiderDriver(GuiderDevice guiderDevice)
        : this(guiderDevice, new GuiderConnection())
    {
        // calls below
    }

    public PHD2GuiderDriver(GuiderDevice guiderDevice, IGuiderConnection connection)
    {
        _guiderDevice = guiderDevice;

        if (guiderDevice.DeviceType != "PHD2")
        {
            throw new ArgumentException($"{guiderDevice} is not of type PHD2, but of type: {guiderDevice.DeviceType}", nameof(guiderDevice));
        }

        var deviceIdSplit = guiderDevice.DeviceId.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (deviceIdSplit.Length < 2 || !IsValidHost(deviceIdSplit[0]) || !uint.TryParse(deviceIdSplit[1], out uint instanceId))
        {
            throw new ArgumentException($"Could not parse {guiderDevice.DeviceId} in {guiderDevice}", nameof(guiderDevice));
        }

        Host = deviceIdSplit[0];
        Instance = instanceId;
        ProfileName = deviceIdSplit.Length > 2 ? deviceIdSplit[2] : guiderDevice.DisplayName;
        Connection = connection;
    }

    private void Worker()
    {
        string? line = null;
        try
        {
            while (!m_terminate)
            {
                try
                {
                    line = Connection.ReadLine();
                }
                catch (Exception ex)
                {
                    OnGuidingErrorEvent(new GuidingErrorEventArgs($"Error {ex.Message} while reading from input stream", ex));
                    // use recovery logic
                }

                if (line == null)
                {
                    // phd2 disconnected
                    // todo: re-connect (?)
                    m_terminate = true;
                    break;
                }

                JsonDocument j;
                try
                {
                    j = JsonDocument.Parse(line);
                }
                catch (JsonException ex)
                {
                    OnGuidingErrorEvent(new GuidingErrorEventArgs(string.Format("ignoring invalid json from server: {0}: {1}", ex.Message, line), ex));
                    continue;
                }

                if (j.RootElement.TryGetProperty("jsonrpc", out var _))
                {
                    lock (m_sync)
                    {
                        m_response?.Dispose();
                        m_response = j;
                        Monitor.Pulse(m_sync);
                    }
                }
                else
                {
                    HandleEvent(j);
                }
            }
        }
        catch (Exception ex)
        {
            OnGuidingErrorEvent(new GuidingErrorEventArgs($"caught exception in worker thread while processing: {line}: {ex.Message}", ex));
        }
        finally
        {
            Connection.Dispose();
        }
    }

    private static void Worker(object? obj)
    {
        if (obj is PHD2GuiderDriver phd2)
        {
            phd2.Worker();
        }
    }

    private void HandleEvent(JsonDocument @event)
    {
        string? eventName = @event.RootElement.GetProperty("Event").GetString();

        if (eventName == "AppState")
        {
            lock (m_sync)
            {
                AppState = @event.RootElement.GetProperty("State").GetString();
                if (IsGuidingAppState(AppState))
                {
                    AverageDistance = 0.0;   // until we get a GuideStep event
                }
            }
        }
        else if (eventName == "Version")
        {
            lock (m_sync)
            {
                Version = @event.RootElement.GetProperty("PHDVersion").GetString();
                PHDSubvVersion = @event.RootElement.GetProperty("PHDSubver").GetString();
            }
        }
        else if (eventName == "StartGuiding")
        {
            IsAccumActive = true;
            AccumRA.Reset();
            AccumDEC.Reset();

            GuideStats stats = AccumulateGuidingStats(AccumRA, AccumDEC);

            lock (m_sync)
            {
                Stats = stats;
            }
        }
        else if (eventName == "GuideStep")
        {
            GuideStats? stats = null;
            if (IsAccumActive)
            {
                AccumRA.Add(@event.RootElement.GetProperty("RADistanceRaw").GetDouble());
                AccumDEC.Add(@event.RootElement.GetProperty("DECDistanceRaw").GetDouble());
                stats = AccumulateGuidingStats(AccumRA, AccumDEC);
            }

            lock (m_sync)
            {
                AppState = "Guiding";
                AverageDistance = @event.RootElement.GetProperty("AvgDist").GetDouble();
                if (IsAccumActive)
                    Stats = stats;
            }
        }
        else if (eventName == "SettleBegin")
        {
            IsAccumActive = false;  // exclude GuideStep messages from stats while settling
        }
        else if (eventName == "Settling")
        {
            var settingProgress = new SettleProgress
            {
                Done = false,
                Distance = @event.RootElement.GetProperty("Distance").GetDouble(),
                SettlePx = SettlePixels,
                Time = @event.RootElement.GetProperty("Time").GetDouble(),
                SettleTime = @event.RootElement.GetProperty("SettleTime").GetDouble(),
                Status = 0
            };
            lock (m_sync)
            {
                Settle = settingProgress;
            }
        }
        else if (eventName == "SettleDone")
        {
            IsAccumActive = true;
            AccumRA.Reset();
            AccumDEC.Reset();

            GuideStats stats = AccumulateGuidingStats(AccumRA, AccumDEC);

            var settleProgress = new SettleProgress
            {
                Done = true,
                Status = @event.RootElement.GetProperty("Status").GetInt32(),
                Error = @event.RootElement.TryGetProperty("Error", out var error) ? error.GetString() : null
            };

            lock (m_sync)
            {
                Settle = settleProgress;
                Stats = stats;
            }
        }
        else if (eventName == "Paused")
        {
            lock (m_sync)
            {
                AppState = "Paused";
            }
        }
        else if (eventName == "StartCalibration")
        {
            lock (m_sync)
            {
                AppState = "Calibrating";
            }
        }
        else if (eventName == "LoopingExposures")
        {
            lock (m_sync)
            {
                AppState = "Looping";
            }
        }
        else if (eventName == "LoopingExposuresStopped" || eventName == "GuidingStopped")
        {
            lock (m_sync)
            {
                AppState = "Stopped";
            }
        }
        else if (eventName == "StarLost")
        {
            lock (m_sync)
            {
                AppState = "LostLock";
                AverageDistance = @event.RootElement.GetProperty("AvgDist").GetDouble();
            }
        }
        else
        {
            OnUnhandledEvent(new UnhandledEventArgs(eventName ?? "Unknown", @event.RootElement.GetRawText()));
        }
    }

    static (Utf8JsonWriter jsonWriter, ArrayBufferWriter<byte> buffer) StartJsonRPCCall(string method)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var req = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });

        req.WriteStartObject();
        req.WriteString("method", method);
        req.WriteNumber("id", 1);

        return (req, buffer);
    }

    static ReadOnlyMemory<byte> EndJsonRPCCall(Utf8JsonWriter jsonWriter, ArrayBufferWriter<byte> buffer)
    {
        jsonWriter.WriteEndObject();
        jsonWriter.Dispose();
        return buffer.WrittenMemory;
    }

    static ReadOnlyMemory<byte> MakeJsonRPCCall(string method, params object[] @params)
    {
        var (req, buffer) = StartJsonRPCCall(method);

        if (@params != null && @params.Length > 0) {
            req.WritePropertyName("params");

            req.WriteStartArray();
            foreach (var param in @params)
            {
                if (param is null)
                {
                    req.WriteNullValue();
                }
                else
                {
                    var typeCode = Type.GetTypeCode(param.GetType());
                    switch (typeCode)
                    {
                        case TypeCode.Boolean:
                            req.WriteBooleanValue((bool)param);
                            break;

                        case TypeCode.Int32:
                            req.WriteNumberValue((int)param);
                            break;

                        case TypeCode.Int64:
                            req.WriteNumberValue((long)param);
                            break;

                        case TypeCode.Single:
                            req.WriteNumberValue((float)param);
                            break;

                        case TypeCode.Double:
                            req.WriteNumberValue((double)param);
                            break;

                        case TypeCode.Object:
                            if (param is SettleRequest settleRequest)
                            {
                                req.WriteStartObject();
                                req.WriteNumber("pixels", settleRequest.Pixels);
                                req.WriteNumber("time", settleRequest.Time);
                                req.WriteNumber("timeout", settleRequest.Timeout);
                                req.WriteEndObject();
                            }
                            else
                            {
                                throw new ArgumentException($"Param {param} of type {param.GetType()} which is an object is not handled", nameof(@params));
                            }
                            break;

                        default:
                            throw new ArgumentException($"Param {param} of type {param.GetType()} which is type code {typeCode} is not handled", nameof(@params));
                    }
                }
            }
            req.WriteEndArray();
        }

        return EndJsonRPCCall(req, buffer);
    }

    static bool IsFailedResponse(JsonDocument response) => response.RootElement.TryGetProperty("error", out _);

    static bool IsValidHost(string host)
        => Uri.CheckHostName(host) switch
        {
            UriHostNameType.Dns or
            UriHostNameType.IPv4 or
            UriHostNameType.IPv6 => true,
            _ => false
        };

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Dispose(true);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (m_worker is Thread prev)
            {
                m_terminate = true;
                Connection.Dispose();
                prev.Join();
                m_worker = null;
            }

            Connection.Dispose();
        }
    }

    public bool Connected
    {
        get => Connection.IsConnected;
        set => Connect(value);
    }

    public void Connect(bool connect)
    {
        if (Connected)
        {
            Dispose(true);
        }

        if (!connect)
        {
            DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(connect));
            return;
        }

        ushort port = (ushort)(4400 + Instance - 1);

        try
        {
            Connection.Connect(Host, port);
        }
        catch (Exception e)
        {
            string errorMsg = string.Format("Could not connect to PHD2 instance {0} on {1}:{2}", Instance, Host, port);
            OnGuidingErrorEvent(new GuidingErrorEventArgs(errorMsg, e));
            throw new GuiderException(errorMsg);
        }

        m_terminate = false;

        var thread = new Thread(new ParameterizedThreadStart(Worker));
        thread.Start(this);
        m_worker = thread;

        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(connect));
    }

    static GuideStats AccumulateGuidingStats(Accum ra, Accum dec) => new()
    {
        rms_ra = ra.Stdev(),
        rms_dec = dec.Stdev(),
        peak_ra = ra.Peak(),
        peak_dec = dec.Peak()
    };

    static bool IsGuidingAppState(string? appState) => appState == "Guiding" || appState == "LostLock";

    public JsonDocument Call(string method, params object[] @params)
    {
        var memory = MakeJsonRPCCall(method, @params);

        // send request
        Connection.WriteLine(memory);

        // wait for response

        lock (m_sync)
        {
            while (m_response == null)
            {
                Monitor.Wait(m_sync);
            }

            JsonDocument response = m_response;
            m_response = null;

            if (IsFailedResponse(response))
            {
                throw new GuiderException(
                    (response.RootElement.GetProperty("error").TryGetProperty("message", out var message) ? message.GetString() : null)
                        ?? "error response did not contain error message");
            }

            return response;
        }
    }

    public IEnumerable<string> RegisteredDeviceTypes => new[] { DeviceType };

    public string Name => "PHD2 Driver";

    public string? Description => "PHD2 Driver uses JSON RPC event stream to drive an instance of PHD2";

    public string? DriverInfo => $"PHD2 {Version} {PHDSubvVersion}";

    public string? DriverVersion => Version;

    public string DriverType => DeviceType;

    void EnsureConnected()
    {
        if (!Connected)
            throw new GuiderException("PHD2 Server disconnected");
    }

    public void Guide(double settlePixels, double settleTime, double settleTimeout)
    {
        EnsureConnected();

        var settleProgress = new SettleProgress
        {
            Done = false,
            Distance = 0.0,
            SettlePx = settlePixels,
            Time = 0.0,
            SettleTime = settleTime,
            Status = 0
        };

        lock (m_sync)
        {
            if (Settle != null && !Settle.Done)
                throw new GuiderException("cannot guide while settling");
            Settle = settleProgress;
        }

        try
        {
            using var response = Call("guide", new SettleRequest(settlePixels, settleTime, settleTimeout), false /* don't force calibration */);
            SettlePixels = settlePixels;
        }
        catch (Exception ex)
        {
            var guidingErrorEventArgs = new GuidingErrorEventArgs($"while calling guide({settlePixels}, {settleTime}, {settleTimeout}): {ex.Message}", ex);
            OnGuidingErrorEvent(guidingErrorEventArgs);
            // failed - remove the settle state
            lock (m_sync)
            {
                Settle = null;
            }
            throw new GuiderException(guidingErrorEventArgs.Message, guidingErrorEventArgs.Exception);
        }
    }

    public void Dither(double ditherPixels, double settlePixels, double settleTime, double settleTimeout, bool raOnly = false)
    {
        EnsureConnected();

        var settleProgress = new SettleProgress()
        {
            Done = false,
            Distance = ditherPixels,
            SettlePx = settlePixels,
            Time = 0.0,
            SettleTime = settleTime,
            Status = 0
        };

        lock (m_sync)
        {
            if (Settle != null && !Settle.Done)
                throw new GuiderException("cannot dither while settling");

            Settle = settleProgress;
        }

        try
        {
            using var response = Call("dither", ditherPixels, raOnly, new SettleRequest(settlePixels, settleTime, settleTimeout));
            SettlePixels = settlePixels;
        }
        catch (Exception ex)
        {
            var guidingErrorEventArgs = new GuidingErrorEventArgs(
                $"while calling dither(ditherPixels: {ditherPixels}, raOnly: {raOnly}, (settlePixels: {settlePixels}, settleTime: {settleTime}, settleTimeout: {settleTimeout}): {ex.Message}", ex);
            OnGuidingErrorEvent(guidingErrorEventArgs);
            // call failed - remove the settle state
            lock (m_sync)
            {
                Settle = null;
            }
            throw new GuiderException(guidingErrorEventArgs.Message, guidingErrorEventArgs.Exception);
        }
    }

    public bool IsLooping()
    {
        EnsureConnected();

        lock (m_sync)
        {
            return AppState == "Looping";
        }
    }

    public bool IsSettling()
    {
        EnsureConnected();

        lock (m_sync)
        {
            if (Settle != null)
            {
                return !Settle.Done;
            }
        }

        // for app init, initialize the settle state to a consistent value
        // as if Guide had been called

        using var settlingResponse = Call("get_settling");

        bool isSettling = settlingResponse.RootElement.GetProperty("result").GetBoolean();

        if (isSettling)
        {
            var settleProgress = new SettleProgress
            {
                Done = false,
                Distance = -1.0,
                SettlePx = 0.0,
                Time = 0.0,
                SettleTime = 0.0,
                Status = 0
            };
            lock (m_sync)
            {
                Settle ??= settleProgress;
            }
        }

        return isSettling;
    }

    public SettleProgress CheckSettling()
    {
        EnsureConnected();

        var settleProgress = new SettleProgress();

        lock (m_sync)
        {
            if (Settle == null)
                throw new GuiderException("not settling");

            if (Settle.Done)
            {
                // settle is done
                settleProgress.Done = true;
                settleProgress.Status = Settle.Status;
                settleProgress.Error = Settle.Error;
                Settle = null;
            }
            else
            {
                // settle in progress
                settleProgress.Done = false;
                settleProgress.Distance = Settle.Distance;
                settleProgress.SettlePx = SettlePixels;
                settleProgress.Time = Settle.Time;
                settleProgress.SettleTime = Settle.SettleTime;
            }
        }

        return settleProgress;
    }

    public GuideStats? GetStats()
    {
        EnsureConnected();

        GuideStats? stats;
        lock (m_sync)
        {
            stats = Stats?.Clone();
        }
        if (stats is not null)
        {
            stats.rms_tot = Math.Sqrt(stats.rms_ra * stats.rms_ra + stats.rms_dec * stats.rms_dec);
        }
        return stats;
    }

    public void StopCapture(uint timeoutSeconds)
    {
        using var stopCaptureResponse = Call("stop_capture");

        for (uint i = 0; i < timeoutSeconds; i++)
        {
            string? appstate;
            lock (m_sync)
            {
                appstate = AppState;
            }
            Debug.WriteLine(String.Format("StopCapture: AppState = {0}", appstate));
            if (appstate == "Stopped")
                return;

            System.Threading.Thread.Sleep(1000);
            EnsureConnected();
        }
        Debug.WriteLine("StopCapture: timed-out waiting for stopped");

        // hack! workaround bug where PHD2 sends a GuideStep after stop request and fails to send GuidingStopped
        using var appStateResponse = Call("get_app_state");
        string? appState = appStateResponse.RootElement.GetProperty("result").GetString();

        lock (m_sync)
        {
            AppState = appState;
        }

        if (appState == "Stopped")
            return;
        // end workaround

        throw new GuiderException(string.Format("guider did not stop capture after {0} seconds!", timeoutSeconds));
    }

    public void Loop(uint timeoutSeconds)
    {
        EnsureConnected();

        // already looping?
        lock (m_sync)
        {
            if (AppState == "Looping")
                return;
        }

        var exposureTime = ExposureTime();

        using var loopingResponse = Call("loop");

        Thread.Sleep(exposureTime);

        for (uint i = 0; i < timeoutSeconds; i++)
        {
            lock (m_sync)
            {
                if (AppState == "Looping")
                    return;
            }

            Thread.Sleep(1000);
            EnsureConnected();
        }

        throw new GuiderException("timed-out waiting for guiding to start looping");
    }

    public double PixelScale()
    {
        EnsureConnected();

        using var response = Call("get_pixel_scale");
        return response.RootElement.GetProperty("result").GetDouble();
    }

    public (int width, int height)? CameraFrameSize()
    {
        EnsureConnected();

        using var response = Call("get_camera_frame_size");

        var result = response.RootElement.GetProperty("result");
        if (result.ValueKind is JsonValueKind.Array && result.GetArrayLength() == 2)
        {
            return (result[0].GetInt32(), result[1].GetInt32());
        }

        return default;
    }

    public TimeSpan ExposureTime()
    {
        EnsureConnected();

        using var exposureResponse = Call("get_exposure");
        return TimeSpan.FromMilliseconds(exposureResponse.RootElement.GetProperty("result").GetInt32());
    }

    public List<string> GetEquipmentProfiles()
    {
        using var response = Call("get_profiles");

        var profiles = new List<string>();
        var jsonResultArray = response.RootElement.GetProperty("result");
        foreach (var item in jsonResultArray.EnumerateArray())
        {
            if (item.GetProperty("name").GetString() is string name)
            {
                profiles.Add(name);
            }
        }

        return profiles;
    }

    static readonly uint DEFAULT_STOPCAPTURE_TIMEOUT = 10;

    public event EventHandler<UnhandledEventArgs>? UnhandledEvent;

    protected virtual void OnUnhandledEvent(UnhandledEventArgs eventArgs) => UnhandledEvent?.Invoke(this, eventArgs);

    public event EventHandler<GuidingErrorEventArgs>? GuidingErrorEvent;
    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;

    protected virtual void OnGuidingErrorEvent(GuidingErrorEventArgs eventArgs) => GuidingErrorEvent?.Invoke(this, eventArgs);

    public void ConnectEquipment()
    {
        using var profileResponse = Call("get_profile");

        var activeProfile = profileResponse.RootElement.GetProperty("result");

        if (activeProfile.GetProperty("name").GetString() != ProfileName)
        {
            using var profilesResponse = Call("get_profiles");
            var profiles = profilesResponse.RootElement.GetProperty("result");
            int profileId = -1;
            foreach (var profile in profiles.EnumerateArray())
            {
                string? name = profile.GetProperty("name").GetString();
                Debug.WriteLine(String.Format("found profile {0}", name));
                if (name == ProfileName)
                {
                    profileId = profile.TryGetProperty("id", out var id) ? id.GetInt32() : -1;
                    Debug.WriteLine(String.Format("found profid {0}", profileId));
                    break;
                }
            }

            if (profileId == -1)
                throw new GuiderException("invalid phd2 profile name: " + ProfileName);

            StopCapture(DEFAULT_STOPCAPTURE_TIMEOUT);

            using var disconnectResponse = Call("set_connected", false);
            using var updateProfileResponse = Call("set_profile", profileId);
        }

        using var connectResponse = Call("set_connected", true);
    }

    public void DisconnectEquipment()
    {
        StopCapture(DEFAULT_STOPCAPTURE_TIMEOUT);
        using var disconnectResponse = Call("set_connected", false);
    }

    public void GetStatus(out string? appState, out double avgDist)
    {
        EnsureConnected();

        lock (m_sync)
        {
            appState = AppState;
            avgDist = AverageDistance;
        }
    }

    public bool IsGuiding()
    {
        GetStatus(out string? appState, out double _ /* average distance */);
        return IsGuidingAppState(appState);
    }

    public void Pause()
    {
        using var response = Call("set_paused", true);
    }

    public void Unpause()
    {
        using var response = Call("set_paused", false);
    }

    public string? SaveImage()
    {
        using var response = Call("save_image");
        return response.RootElement.GetProperty("result").GetProperty("filename").GetString();
    }

    public override string ToString() =>
        Connected
            ? $"PHD2 {_guiderDevice} {Version}/{PHDSubvVersion}: Looping? {IsLooping()}, Guiding? {IsGuiding()}, settling? {IsSettling()}"
            : $"PHD2 {_guiderDevice} not connected!";

    /// <summary>
    /// Caller should ensure that device is connected
    /// </summary>
    /// <param name="deviceType"></param>
    /// <returns></returns>
    public IEnumerable<GuiderDevice> RegisteredDevices(string deviceType)
    {
        if (deviceType != DeviceType)
        {
            yield break;
        }

        foreach (var profile in GetEquipmentProfiles())
        {
            yield return new GuiderDevice(deviceType, _guiderDevice.DeviceId, profile);
        }
    }
}