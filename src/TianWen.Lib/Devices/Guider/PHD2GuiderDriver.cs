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
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace TianWen.Lib.Devices.Guider;

internal class PHD2GuiderDriver : IGuider, IDeviceSource<GuiderDevice>
{
    Thread? m_worker;
    volatile bool m_terminate;
    readonly object m_sync = new();
    readonly Dictionary<int, JsonDocument> _responses = [];
    readonly GuiderDevice _guiderDevice;
    private string? _selectedProfileName;

    string Host { get; }
    uint Instance { get; }
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

    /// <summary>
    /// PHD2 is in principle always supported on any platform.
    /// </summary>
    public bool IsSupported { get; } = true;

    public PHD2GuiderDriver(GuiderDevice guiderDevice, IExternal external)
        : this(guiderDevice, new GuiderConnection(), external)
    {
        // calls below
    }

    public PHD2GuiderDriver(GuiderDevice guiderDevice, IGuiderConnection connection, IExternal external)
    {
        External = external;
        _guiderDevice = guiderDevice;

        if (guiderDevice.DeviceType != DeviceType.DedicatedGuiderSoftware)
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
        Connection = connection;
        if ((deviceIdSplit.Length > 2 ? deviceIdSplit[2] : guiderDevice.DisplayName) is var profile && string.IsNullOrWhiteSpace(profile))
        {
            _selectedProfileName = profile;
        }
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
                    OnGuidingErrorEvent(new GuidingErrorEventArgs(_guiderDevice, _selectedProfileName, $"Error {ex.Message} while reading from input stream", ex));
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
                    OnGuidingErrorEvent(new GuidingErrorEventArgs(
                        _guiderDevice,
                        _selectedProfileName,
                        $"ignoring invalid json from server: {ex.Message}: {line}",
                        ex
                    ));
                    continue;
                }

                if (j.RootElement.TryGetProperty("jsonrpc", out var _) && j.RootElement.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id))
                {
                    lock (m_sync)
                    {
                        if (_responses.TryGetValue(id, out var old) && _responses.Remove(id))
                        {
                            old.Dispose();
                        }

                        _responses[id] = j;
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
            OnGuidingErrorEvent(new GuidingErrorEventArgs(_guiderDevice, _selectedProfileName, $"caught exception in worker thread while processing: {line}: {ex.Message}", ex));
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
        string? newAppState = null;

        if (eventName == "AppState")
        {
            lock (m_sync)
            {
                newAppState = AppState = @event.RootElement.GetProperty("State").GetString();
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

            lock (m_sync)
            {
                Stats = new GuideStats();
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
                newAppState = AppState = "Guiding";
                AverageDistance = @event.RootElement.GetProperty("AvgDist").GetDouble();
                if (IsAccumActive)
                {
                    Stats = stats;
                }
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
                Status = 0,
                StarLocked = @event.RootElement.GetProperty("StarLocked").GetBoolean(),
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
                newAppState = AppState = "Paused";
            }
        }
        else if (eventName == "StartCalibration")
        {
            lock (m_sync)
            {
                newAppState = AppState = "Calibrating";
            }
        }
        else if (eventName == "StarSelected")
        {
            lock (m_sync)
            {
                newAppState = AppState = "Selected";
            }
        }
        else if (eventName == "LoopingExposures")
        {
            lock (m_sync)
            {
                newAppState = AppState = "Looping";
            }
        }
        else if (eventName == "LoopingExposuresStopped" || eventName == "GuidingStopped")
        {
            lock (m_sync)
            {
                newAppState = AppState = "Stopped";
            }
        }
        else if (eventName == "StarLost")
        {
            lock (m_sync)
            {
                newAppState = AppState = "LostLock";
                AverageDistance = @event.RootElement.GetProperty("AvgDist").GetDouble();
            }
        }

        OnGuiderStateChangedEvent(new GuiderStateChangedEventArgs(_guiderDevice, _selectedProfileName, eventName ?? "Unknown", newAppState ?? "Unknown"));
    }

    static int MessageId = 1;
    static (Utf8JsonWriter jsonWriter, ArrayBufferWriter<byte> buffer, int id) StartJsonRPCCall(string method)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var req = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        var id = Interlocked.Increment(ref MessageId);

        req.WriteStartObject();
        req.WriteString("method", method);
        req.WriteNumber("id", id);

        return (req, buffer, id);
    }

    static ReadOnlyMemory<byte> EndJsonRPCCall(Utf8JsonWriter jsonWriter, ArrayBufferWriter<byte> buffer)
    {
        jsonWriter.WriteEndObject();
        jsonWriter.Dispose();
        return buffer.WrittenMemory;
    }

    static (ReadOnlyMemory<byte> buffer, int id) MakeJsonRPCCall(string method, params object[] @params)
    {
        var (req, buffer, id) = StartJsonRPCCall(method);

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

        return (EndJsonRPCCall(req, buffer), id);
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
            string errorMsg = $"Could not connect to PHD2 instance {Instance} on {Host}:{port}";
            OnGuidingErrorEvent(new GuidingErrorEventArgs(_guiderDevice, _selectedProfileName, errorMsg, e));
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
        RaRMS = ra.Stdev(),
        DecRMS = dec.Stdev(),
        PeakRa = ra.Peak(),
        PeakDec = dec.Peak()
    };

    static bool IsGuidingAppState(string? appState) => appState == "Guiding" || appState == "LostLock";

    /// <summary>
    /// support raw JSONRPC method invocation. Generally you won't need to
    /// use this function as it is much more convenient to use the higher-level methods below
    /// </summary>
    /// <param name="method"></param>
    /// <param name="params"></param>
    /// <returns></returns>
    protected JsonDocument Call(string method, params object[] @params)
    {
        var (memory, id) = MakeJsonRPCCall(method, @params);

        // send request
        if (!Connection.WriteLine(memory))
        {
            throw new GuiderException($"Failed to send message {method} params: {string.Join(", ", @params)}");
        }

        // wait for response

        lock (m_sync)
        {
            JsonDocument? response;
            while ((response = _responses.TryGetValue(id, out var actualResponse) ? actualResponse : null) == null)
            {
                Monitor.Wait(m_sync);
            }

            _ = _responses.Remove(id);

            if (IsFailedResponse(response))
            {
                throw new GuiderException(
                    (response.RootElement.GetProperty("error").TryGetProperty("message", out var message) ? message.GetString() : null)
                        ?? "error response did not contain error message");
            }

            if (!response.RootElement.TryGetProperty("id", out var responseIdElement) || !responseIdElement.TryGetInt32(out int responseId) || responseId != id)
            {
                throw new GuiderException($"Response id was not {id}: {response.RootElement}");
            }

            return response;
        }
    }

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DriverType];

    public string Name => "PHD2 Driver";

    public string? Description => "PHD2 Driver uses JSON RPC event stream to drive an instance of PHD2";

    public string? DriverInfo => $"PHD2 {Version} {PHDSubvVersion}";

    public string? DriverVersion => Version;

    public DeviceType DriverType { get; } = DeviceType.PHD2;

    public IExternal External { get; }

    void EnsureConnected()
    {
        if (!Connected)
        {
            throw new GuiderException("PHD2 Server disconnected");
        }
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
            {
                throw new GuiderException("cannot guide while settling");
            }
            Settle = settleProgress;
        }

        try
        {
            using var response = Call("guide", new SettleRequest(settlePixels, settleTime, settleTimeout), false /* don't force calibration */);
            SettlePixels = settlePixels;
        }
        catch (Exception ex)
        {
            var guidingErrorEventArgs = new GuidingErrorEventArgs(_guiderDevice, _selectedProfileName, $"while calling guide({settlePixels}, {settleTime}, {settleTimeout}): {ex.Message}", ex);
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
                _guiderDevice,
                _selectedProfileName,
                $"while calling dither(ditherPixels: {ditherPixels}, raOnly: {raOnly}, " +
                $"(settlePixels: {settlePixels}, settleTime: {settleTime}, settleTimeout: {settleTimeout}): {ex.Message}",
                ex
            );
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

    public bool TryGetSettleProgress([NotNullWhen(true)] out SettleProgress? settleProgress)
    {
        EnsureConnected();

        lock (m_sync)
        {
            if (Settle == null)
            {
                settleProgress = null;
                return false;
            }

            if (Settle.Done)
            {
                settleProgress = new SettleProgress
                {
                    Done = true,
                    Status = Settle.Status,
                    Error = Settle.Error
                };
                Settle = null;

                return true;
            }
            else
            {
                settleProgress = new SettleProgress
                {
                    Done = false,
                    Distance = Settle.Distance,
                    SettlePx = SettlePixels,
                    Time = Settle.Time,
                    SettleTime = Settle.SettleTime
                };

                return true;
            }
        }
    }

    public GuideStats? GetStats()
    {
        EnsureConnected();

        GuideStats? stats;
        double? lastRaErr;
        double? lastDecErr;
        lock (m_sync)
        {
            stats = Stats?.Clone();
            lastRaErr = AccumRA.Last;
            lastDecErr = AccumDEC.Last;
        }
        if (stats is not null)
        {
            stats.TotalRMS = Math.Sqrt(stats.RaRMS * stats.RaRMS + stats.DecRMS * stats.DecRMS);
            stats.LastRaErr = lastRaErr;
            stats.LastDecErr = lastDecErr;
        }
        return stats;
    }

    public void StopCapture(TimeSpan timeout, Action<TimeSpan>? sleep = null)
    {
        sleep ??= Thread.Sleep;
        using var stopCaptureResponse = Call("stop_capture");

        var totalSeconds = (uint)timeout.TotalSeconds;
        for (uint i = 0; i < totalSeconds; i++)
        {
            string? appstate;
            lock (m_sync)
            {
                appstate = AppState;
            }
            Debug.WriteLine($"StopCapture: AppState = {appstate}");
            if (appstate == "Stopped")
                return;

            sleep(TimeSpan.FromSeconds(1));
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

        throw new GuiderException($"guider did not stop capture after {totalSeconds} seconds!");
    }

    public bool Loop(TimeSpan timeout, Action<TimeSpan>? sleep = null)
    {
        sleep ??= Thread.Sleep;

        EnsureConnected();

        // already looping?
        lock (m_sync)
        {
            if (AppState == "Looping")
            {
                return true;
            }
        }

        var exposureTime = ExposureTime();

        using var loopingResponse = Call("loop");

        sleep(exposureTime);

        var totalSeconds = (uint)timeout.TotalSeconds;
        for (uint i = 0; i < totalSeconds; i++)
        {
            lock (m_sync)
            {
                if (AppState == "Looping")
                {
                    return true;
                }
            }

            sleep(TimeSpan.FromSeconds(1));

            EnsureConnected();
        }

        return false;
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

    public IReadOnlyList<string> GetEquipmentProfiles()
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

    static readonly TimeSpan DEFAULT_STOPCAPTURE_TIMEOUT = TimeSpan.FromSeconds(10);

    public event EventHandler<GuidingErrorEventArgs>? GuidingErrorEvent;

    protected virtual void OnGuidingErrorEvent(GuidingErrorEventArgs eventArgs) => GuidingErrorEvent?.Invoke(this, eventArgs);

    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;

    public event EventHandler<GuiderStateChangedEventArgs>? GuiderStateChangedEvent;

    protected virtual void OnGuiderStateChangedEvent(GuiderStateChangedEventArgs eventArgs) => GuiderStateChangedEvent?.Invoke(this, eventArgs);

    public bool TryGetActiveProfileName([NotNullWhen(true)] out string? activeProfileName)
    {
        if (!Connected)
        {
            activeProfileName = null;
            return false;
        }

        using var profileResponse = Call("get_profile");

        if (profileResponse.RootElement.TryGetProperty("result", out var activeProfileProp)
            && activeProfileProp.TryGetProperty("name", out var nameProp)
            && nameProp.ValueKind is JsonValueKind.String
            && nameProp.GetString() is string name
        )
        {
            activeProfileName = name;

            return true;
        }

        activeProfileName = null;
        return false;
    }

    public void ConnectEquipment()
    {
        // this allows us to reuse the connection if we just want to connect to whatever profile has been selected by the user
        if (TryGetActiveProfileName(out var activeProfileName) && (_selectedProfileName ??= activeProfileName) !=  _selectedProfileName)
        {
            using var profilesResponse = Call("get_profiles");
            var profiles = profilesResponse.RootElement.GetProperty("result");
            int profileId = -1;
            foreach (var profile in profiles.EnumerateArray())
            {
                string? name = profile.GetProperty("name").GetString();
                Debug.WriteLine($"found profile {name}");
                if (name == _selectedProfileName)
                {
                    profileId = profile.TryGetProperty("id", out var id) ? id.GetInt32() : -1;
                    Debug.WriteLine($"found profid {profileId}");
                    break;
                }
            }

            if (profileId == -1)
            {
                throw new GuiderException("invalid phd2 profile name: " + _selectedProfileName + " active is: " + activeProfileName);
            }

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

    public string? SaveImage(string outputFolder)
    {
        using var response = Call("save_image");
        if (response.RootElement.GetProperty("result").GetProperty("filename").GetString() is { Length: > 0 } tempFileName
            && File.Exists(tempFileName)
        )
        {
            var outputFolderFullName = Directory.CreateDirectory(outputFolder).FullName;
            var copiedFile = Path.Combine(outputFolderFullName, $"{Guid.NewGuid():D}.fits");
            File.Copy(tempFileName, copiedFile);
            File.Delete(tempFileName);

            return copiedFile;
        }

        return null;
    }

    public override string ToString() =>
        Connected
            ? $"PHD2 {_guiderDevice.DeviceId} {Version}/{PHDSubvVersion}: Looping? {IsLooping()}, Guiding? {IsGuiding()}, settling? {IsSettling()}"
            : $"PHD2 {_guiderDevice.DeviceId} not connected!";

    /// <summary>
    /// Caller should ensure that device is connected
    /// </summary>
    /// <param name="deviceType"></param>
    /// <returns></returns>
    public IEnumerable<GuiderDevice> RegisteredDevices(DeviceType deviceType)
    {
        if (deviceType != DeviceType.DedicatedGuiderSoftware)
        {
            yield break;
        }

        foreach (var profile in GetEquipmentProfiles())
        {
            yield return new GuiderDevice(deviceType, string.Join('/', _guiderDevice.DeviceId, profile), profile);
        }
    }
}