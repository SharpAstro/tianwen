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

using DotNext.Threading;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices.Guider;

namespace TianWen.Lib.Devices.OpenPHD2;

internal class OpenPHD2GuiderDriver : IGuider, IDeviceSource<OpenPHD2GuiderDevice>
{
    private readonly ConcurrentDictionary<long, JsonDocument> _responses = [];
    private readonly OpenPHD2GuiderDevice _guiderDevice;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly AsyncManualResetEvent _receiveResponseSignal = new(false);
    private IUtf8TextBasedConnection? _connection;
    private string? _selectedProfileName;
    private List<OpenPHD2GuiderDevice> _equipmentProfiles = [];
    private CancellationTokenSource _cts = new();
    private Task? _receiveTask;

    private Accum AccumRA { get; } = new Accum();
    private Accum AccumDEC { get; } = new Accum();
    private bool IsAccumActive { get; set; }
    private double SettlePixels { get; set; }
    private string? AppState { get; set; }
    private double AverageDistance { get; set; }
    private GuideStats? Stats { get; set; }
    private string? Version { get; set; }
    private string? PHDSubvVersion { get; set; }
    private SettleProgress? Settle { get; set; }

    public OpenPHD2GuiderDriver(IExternal external) : this(MakeDefaultRootDevice(external), external)
    {
        // calls below
    }

    public OpenPHD2GuiderDriver(OpenPHD2GuiderDevice guiderDevice, IExternal external)
    {
        if (guiderDevice.DeviceType != DeviceType.Guider)
        {
            throw new ArgumentException($"{guiderDevice} is a guider, but of type: {guiderDevice.DeviceType}", nameof(guiderDevice));
        }

        External = external;
        _guiderDevice = guiderDevice;

        if (_guiderDevice.ProfileName is { } profileName)
        {
            _selectedProfileName = profileName;
        }
    }

    private OpenPHD2GuiderDevice ActiveGuiderDevice => _selectedProfileName is { } ? _guiderDevice.WithProfile(_selectedProfileName) : _guiderDevice;

    private static OpenPHD2GuiderDevice MakeDefaultRootDevice(IExternal external)
    {
        var ip = external.DefaultGuiderAddress;
        var instanceId = ip.Port - 4400 + 1;

        return new OpenPHD2GuiderDevice(DeviceType.Guider, string.Join('/', ip.Address, instanceId), $"PHD2 instance {instanceId} on {ip}");
    }

    /// <summary>
    /// PHD2 is in principle always supported on any platform.
    /// </summary>
    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public async ValueTask DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!Connected)
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        var profileNames = await GetEquipmentProfilesAsync(cancellationToken).ConfigureAwait(false);

        var equipmentProfiles = profileNames.Select(_guiderDevice.WithProfile).ToList();

        Interlocked.Exchange(ref _equipmentProfiles, equipmentProfiles);
    }

    /// <summary>
    /// Caller should ensure that device is connected
    /// </summary>
    /// <param name="deviceType"></param>
    /// <returns></returns>
    public IEnumerable<OpenPHD2GuiderDevice> RegisteredDevices(DeviceType deviceType) => deviceType is DeviceType.Guider ? _equipmentProfiles : [];

    private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
    {
        string? line = null;
        try
        {
            while (_connection is { } connection && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    line = await connection.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException oce) when (oce.CancellationToken.IsCancellationRequested)
                {
                    // cancellation requested
                    break;
                }
                catch (Exception ex)
                {
                    OnGuidingErrorEvent(new GuidingErrorEventArgs(ActiveGuiderDevice, $"Error {ex.Message} while reading from input stream", ex));
                    // use recovery logic
                }

                if (line == null)
                {
                    // phd2 disconnected
                    // todo: re-connect (?)
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
                        ActiveGuiderDevice,
                        $"ignoring invalid json from server: {ex.Message}: {line}",
                        ex
                    ));
                    continue;
                }

                if (j.RootElement.TryGetProperty("jsonrpc", out var _)
                    && j.RootElement.TryGetProperty("id", out var idProp)
                    && idProp.ValueKind is JsonValueKind.Number
                    && idProp.TryGetInt32(out var id)
                )
                {
                    _ = _responses.AddOrUpdate(id, j,
                        (_, old) =>
                        {
                            old.Dispose();
                            return j;
                        }
                    );

                    _receiveResponseSignal.Set(true);
                }
                else
                {
                    await HandleEventAsync(j, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            OnGuidingErrorEvent(new GuidingErrorEventArgs(ActiveGuiderDevice, $"caught exception in worker thread while processing: {line}: {ex.Message}", ex));
        }
        finally
        {
            Interlocked.Exchange(ref _connection, null)?.Dispose();
        }
    }

    private async ValueTask HandleEventAsync(JsonDocument @event, CancellationToken cancellationToken = default)
    {
        string? eventName = @event.RootElement.GetProperty("Event").GetString();
        string? newAppState = null;

        if (eventName is "AppState")
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                newAppState = AppState = @event.RootElement.GetProperty("State").GetString();
                if (IsGuidingAppState(AppState))
                {
                    AverageDistance = 0.0;   // until we get a GuideStep event
                }
            }
            finally
            {
                _sync.Release();
            }
        }
        else if (eventName is "Version")
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Version = @event.RootElement.GetProperty("PHDVersion").GetString();
                PHDSubvVersion = @event.RootElement.GetProperty("PHDSubver").GetString();
            }
            finally
            {
                _sync.Release();
            }
        }
        else if (eventName is "StartGuiding")
        {
            IsAccumActive = true;
            AccumRA.Reset();
            AccumDEC.Reset();

            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Stats = new GuideStats();
            }
            finally
            {
                _sync.Release();
            }
        }
        else if (eventName is "GuideStep")
        {
            GuideStats? stats = null;
            if (IsAccumActive)
            {
                AccumRA.Add(@event.RootElement.GetProperty("RADistanceRaw").GetDouble());
                AccumDEC.Add(@event.RootElement.GetProperty("DECDistanceRaw").GetDouble());
                stats = AccumulateGuidingStats(AccumRA, AccumDEC);
            }

            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                newAppState = AppState = "Guiding";
                AverageDistance = @event.RootElement.GetProperty("AvgDist").GetDouble();
                if (IsAccumActive)
                {
                    Stats = stats;
                }
            }
            finally
            {
                _sync.Release();
            }
        }
        else if (eventName is "SettleBegin")
        {
            IsAccumActive = false;  // exclude GuideStep messages from stats while settling
        }
        else if (eventName is "Settling")
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
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Settle = settingProgress;
            }
            finally
            {
                _sync.Release();
            }
        }
        else if (eventName is "SettleDone")
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

            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Settle = settleProgress;
                Stats = stats;
            }
            finally
            {
                _sync.Release();
            }
        }
        else if (eventName is "Paused")
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                newAppState = AppState = "Paused";
            }
            finally
            {
                _sync.Release();
            }
        }
        else if (eventName is "StartCalibration")
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                newAppState = AppState = "Calibrating";
            }
            finally
            {
                _sync.Release();
            }
        }
        else if (eventName is "StarSelected")
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                newAppState = AppState = "Selected";
            }
            finally
            {
                _sync.Release();
            }
        }
        else if (eventName is "LoopingExposures")
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                newAppState = AppState = "Looping";
            }
            finally
            {
                _sync.Release();
            }
        }
        else if (eventName is "LoopingExposuresStopped" or "GuidingStopped")
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                newAppState = AppState = "Stopped";
            }
            finally
            {
                _sync.Release();
            }
        }
        else if (eventName is "StarLost")
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                newAppState = AppState = "LostLock";
                AverageDistance = @event.RootElement.GetProperty("AvgDist").GetDouble();
            }
            finally
            {
                _sync.Release();
            }
        }

        OnGuiderStateChangedEvent(new GuiderStateChangedEventArgs(ActiveGuiderDevice, eventName ?? "Unknown", newAppState ?? "Unknown"));
    }

    static long MessageId = 0;

    static long MakeJsonRPCCall(IBufferWriter<byte> buffer, string method, params object[] @params)
    {
        using var req = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        var id = Interlocked.Increment(ref MessageId);

        req.WriteStartObject();
        req.WriteString("method", method);
        req.WriteNumber("id", id);

        if (@params is { Length: > 0 })
        {
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

        req.WriteEndObject();
        return id;
    }

    static bool IsFailedResponse(JsonDocument response) => response.RootElement.TryGetProperty("error", out _);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Dispose(true);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Dispose();
            Interlocked.Exchange(ref _connection, null)?.Dispose();
        }
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        await DisconnectAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // Perform async cleanup.
        await DisposeAsyncCore();

        // Dispose of unmanaged resources.
        Dispose(false);

        // Suppress finalization.
        GC.SuppressFinalize(this);
    }

    public bool Connected => _connection?.IsConnected ?? false;

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => SetConnectedAsync(true, cancellationToken);
    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => SetConnectedAsync(false, cancellationToken);

    private async ValueTask SetConnectedAsync(bool connect, CancellationToken cancellationToken = default)
    {
        if (Connected == connect)
        {
            return;
        }

        if (connect)
        {
            var instanceId = _guiderDevice.InstanceId;
            var host = _guiderDevice.Host;
            var port = (ushort)(4400 + instanceId - 1);
            EndPoint endPoint = IPAddress.TryParse(host, out var ipAddress)
                ? new IPEndPoint(ipAddress, port)
                : new DnsEndPoint(host, port);

            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var connection = await External.ConnectGuiderAsync(endPoint, CommunicationProtocol.JsonRPC, cancellationToken);

                Interlocked.Exchange(ref _connection, connection)?.Dispose();
                var cts = new CancellationTokenSource();
                var oldCts = Interlocked.Exchange(ref _cts, cts);
                var oldTask = Interlocked.Exchange(ref _receiveTask, ReceiveMessagesAsync(cts.Token));
                try
                {
                    if (oldTask is { IsCanceled: false })
                    {
                        await oldCts.CancelAsync();
                    }
                }
                finally
                {
                    oldCts.Dispose();
                }
            }
            catch (Exception e)
            {
                throw new GuiderException($"Failed to connect to {_guiderDevice.DisplayName}: {e.Message}", e);
            }
            finally
            {
                _sync.Release();
            }
        }
        else
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var oldCts = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
                try
                {
                    await oldCts.CancelAsync();
                }
                finally
                {
                    oldCts.Dispose();
                }
                Interlocked.Exchange(ref _connection, null)?.Dispose();
            }
            finally
            {
                _sync.Release();
            }
        }

        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(connect));
    }

    static GuideStats AccumulateGuidingStats(Accum ra, Accum dec) => new()
    {
        RaRMS = ra.Stdev,
        DecRMS = dec.Stdev,
        PeakRa = ra.Peak,
        PeakDec = dec.Peak
    };

    static bool IsGuidingAppState(string? appState) => appState is "Guiding" or "LostLock";

    /// <summary>
    /// support raw JSONRPC method invocation. Generally you won't need to
    /// use this function as it is much more convenient to use the higher-level methods below
    /// </summary>
    /// <param name="method"></param>
    /// <param name="params"></param>
    /// <returns></returns>
    protected async ValueTask<JsonDocument> CallAsync(string method, CancellationToken cancellationToken, params object[] @params)
    {
        EnsureConnected();

        var buffer = new ArrayBufferWriter<byte>(128);
        var id = MakeJsonRPCCall(buffer, method, @params);

        // send request
        if (_connection is not { } connection || !await connection.WriteLineAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false))
        {
            throw new GuiderException($"Failed to send message {method} params: {string.Join(", ", @params)}");
        }

        // wait for response
        JsonDocument? response;
        while (!_responses.TryRemove(id, out response))
        {
            await _receiveResponseSignal.WaitAsync(cancellationToken);
        }

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

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DriverType];

    public string Name => "PHD2 Driver";

    public string? Description => "PHD2 Driver uses JSON RPC event stream to drive an instance of PHD2";

    public string? DriverInfo => $"PHD2 {Version} {PHDSubvVersion}";

    public string? DriverVersion => Version;

    public DeviceType DriverType { get; } = DeviceType.Guider;

    public IExternal External { get; }

    [DebuggerStepThrough]
    void EnsureConnected()
    {
        if (!Connected)
        {
            throw new GuiderException("PHD2 Server disconnected");
        }
    }

    public async ValueTask GuideAsync(double settlePixels, double settleTime, double settleTimeout, CancellationToken cancellationToken = default)
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

        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Settle != null && !Settle.Done)
            {
                throw new GuiderException("cannot guide while settling");
            }
            Settle = settleProgress;
        }
        finally
        {
            _sync.Release();
        }


        try
        {
            using var response = await CallAsync("guide", cancellationToken, new SettleRequest(settlePixels, settleTime, settleTimeout), false /* don't force calibration */);
            SettlePixels = settlePixels;
        }
        catch (Exception ex)
        {
            var guidingErrorEventArgs = new GuidingErrorEventArgs(ActiveGuiderDevice, $"while calling guide({settlePixels}, {settleTime}, {settleTimeout}): {ex.Message}", ex);
            OnGuidingErrorEvent(guidingErrorEventArgs);
            // failed - remove the settle state
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Settle = null;
            }
            finally
            {
                _sync.Release();
            }
            throw new GuiderException(guidingErrorEventArgs.Message, guidingErrorEventArgs.Exception);
        }
    }

    public async ValueTask DitherAsync(double ditherPixels, double settlePixels, double settleTime, double settleTimeout, bool raOnly = false, CancellationToken cancellationToken = default)
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

        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Settle != null && !Settle.Done)
                throw new GuiderException("cannot dither while settling");

            Settle = settleProgress;
        }
        finally
        {
            _sync.Release();
        }

        try
        {
            using var response = await CallAsync("dither", cancellationToken, ditherPixels, raOnly, new SettleRequest(settlePixels, settleTime, settleTimeout)).ConfigureAwait(false);
            SettlePixels = settlePixels;
        }
        catch (Exception ex)
        {
            var guidingErrorEventArgs = new GuidingErrorEventArgs(
                ActiveGuiderDevice,
                $"while calling dither(ditherPixels: {ditherPixels}, raOnly: {raOnly}, " +
                $"(settlePixels: {settlePixels}, settleTime: {settleTime}, settleTimeout: {settleTimeout}): {ex.Message}",
                ex
            );
            OnGuidingErrorEvent(guidingErrorEventArgs);
            // call failed - remove the settle state
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Settle = null;
            }
            finally
            {
                _sync.Release();
            }
            throw new GuiderException(guidingErrorEventArgs.Message, guidingErrorEventArgs.Exception);
        }
    }

    public async ValueTask<bool> IsLoopingAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return AppState == "Looping";
        }
        finally
        {
            _sync.Release();
        }
    }

    public async ValueTask<bool> IsSettlingAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Settle != null)
            {
                return !Settle.Done;
            }
        }
        finally
        {
            _sync.Release();
        }

        // for app init, initialize the settle state to a consistent value
        // as if Guide had been called

        using var settlingResponse = await CallAsync("get_settling", cancellationToken).ConfigureAwait(false);

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
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Settle ??= settleProgress;
            }
            finally
            {
                _sync.Release();
            }
        }

        return isSettling;
    }

    public async ValueTask<SettleProgress?> GetSettleProgressAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Settle == null)
            {
                return null;
            }

            if (Settle.Done)
            {
                var settleProgress = new SettleProgress
                {
                    Done = true,
                    Status = Settle.Status,
                    Error = Settle.Error
                };
                Settle = null;

                return settleProgress;
            }
            else
            {
                return new SettleProgress
                {
                    Done = false,
                    Distance = Settle.Distance,
                    SettlePx = SettlePixels,
                    Time = Settle.Time,
                    SettleTime = Settle.SettleTime
                };
            }
        }
        finally
        {
            _sync?.Release();
        }
    }

    public async ValueTask<GuideStats?> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        GuideStats? stats;
        double? lastRaErr;
        double? lastDecErr;
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            stats = Stats?.Clone();
            lastRaErr = AccumRA.Last;
            lastDecErr = AccumDEC.Last;
        }
        finally
        {
            _sync?.Release();
        }

        if (stats is not null)
        {
            stats.TotalRMS = Math.Sqrt(stats.RaRMS * stats.RaRMS + stats.DecRMS * stats.DecRMS);
            stats.LastRaErr = lastRaErr;
            stats.LastDecErr = lastDecErr;
        }
        return stats;
    }

    public async ValueTask StopCaptureAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var stopCaptureResponse = await CallAsync("stop_capture", cancellationToken).ConfigureAwait(false);

        var totalSeconds = (uint)timeout.TotalSeconds;
        for (uint i = 0; i < totalSeconds; i++)
        {
            string? appstate;
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                appstate = AppState;
            }
            finally
            {
                _sync.Release();
            }

            if (appstate is "Stopped")
            {
                return;
            }

            await External.SleepAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            EnsureConnected();
        }

        // hack! workaround bug where PHD2 sends a GuideStep after stop request and fails to send GuidingStopped
        using var appStateResponse = await CallAsync("get_app_state", cancellationToken).ConfigureAwait(false);
        string? appState = appStateResponse.RootElement.GetProperty("result").GetString();

        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppState = appState;
        }
        finally
        {
            _sync.Release();
        }

        if (appState == "Stopped")
            return;
        // end workaround

        throw new GuiderException($"Guider did not stop capture after {totalSeconds} seconds!");
    }

    public async ValueTask<bool> LoopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        // already looping?
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (AppState == "Looping")
            {
                return true;
            }
        }
        finally
        {
            _sync.Release();
        }

        var exposureTime = await ExposureTimeAsync(cancellationToken).ConfigureAwait(false);

        using var loopingResponse = await CallAsync("loop", cancellationToken).ConfigureAwait(false);

        await External.SleepAsync(exposureTime, cancellationToken).ConfigureAwait(false);

        var totalSeconds = (uint)timeout.TotalSeconds;
        for (uint i = 0; i < totalSeconds; i++)
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (AppState == "Looping")
                {
                    return true;
                }
            }
            finally
            {
                _sync.Release();
            }

            await External.SleepAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

            EnsureConnected();
        }

        return false;
    }

    public async ValueTask<double> PixelScaleAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        using var response = await CallAsync("get_pixel_scale", cancellationToken).ConfigureAwait(false);
        return response.RootElement.GetProperty("result").GetDouble();
    }

    public async ValueTask<(int Width, int Height)?> CameraFrameSizeAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        using var response = await CallAsync("get_camera_frame_size", cancellationToken).ConfigureAwait(false);

        var result = response.RootElement.GetProperty("result");
        if (result.ValueKind is JsonValueKind.Array && result.GetArrayLength() == 2)
        {
            return (result[0].GetInt32(), result[1].GetInt32());
        }

        return default;
    }

    public async ValueTask<TimeSpan> ExposureTimeAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        using var exposureResponse = await CallAsync("get_exposure", cancellationToken).ConfigureAwait(false);
        return TimeSpan.FromMilliseconds(exposureResponse.RootElement.GetProperty("result").GetInt32());
    }

    public async ValueTask<IReadOnlyList<string>> GetEquipmentProfilesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await CallAsync("get_profiles", cancellationToken).ConfigureAwait(false);

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

    public async ValueTask<string?> GetActiveProfileNameAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        using var profileResponse = await CallAsync("get_profile", cancellationToken).ConfigureAwait(false);

        if (profileResponse.RootElement.TryGetProperty("result", out var activeProfileProp)
            && activeProfileProp.TryGetProperty("name", out var nameProp)
            && nameProp.ValueKind is JsonValueKind.String
            && nameProp.GetString() is string name
        )
        {
            return name;
        }

        return null;
    }

    public async ValueTask ConnectEquipmentAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        // this allows us to reuse the connection if we just want to connect to whatever profile has been selected by the user
        if (await GetActiveProfileNameAsync(cancellationToken).ConfigureAwait(false) is { } activeProfileName
            && (_selectedProfileName ??= activeProfileName) !=  _selectedProfileName
        )
        {
            using var profilesResponse = await CallAsync("get_profiles", cancellationToken).ConfigureAwait(false);
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

            await StopCaptureAsync(DEFAULT_STOPCAPTURE_TIMEOUT, cancellationToken).ConfigureAwait(false);

            using var disconnectResponse = await CallAsync("set_connected", cancellationToken, false).ConfigureAwait(false);
            using var updateProfileResponse = await CallAsync("set_profile", cancellationToken, profileId).ConfigureAwait(false);
        }

        using var connectResponse = await CallAsync("set_connected", cancellationToken, true).ConfigureAwait(false);
    }

    public async ValueTask DisconnectEquipmentAsync(CancellationToken cancellationToken = default)
    {
        await StopCaptureAsync(DEFAULT_STOPCAPTURE_TIMEOUT, cancellationToken).ConfigureAwait(false);
        using var disconnectResponse = await CallAsync("set_connected", cancellationToken, false).ConfigureAwait(false);
    }

    public async ValueTask<(string? AppState, double AvgDist)> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (AppState, AverageDistance);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async ValueTask<bool> IsGuidingAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var (appState, _) = await GetStatusAsync(cancellationToken).ConfigureAwait(false);

        return IsGuidingAppState(appState);
    }

    public async ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        using var response = await CallAsync("set_paused", cancellationToken, true).ConfigureAwait(false);
    }

    public async ValueTask UnpauseAsync(CancellationToken cancellationToken = default)
    {
        using var response = await CallAsync("set_paused", cancellationToken, false).ConfigureAwait(false);
    }

    public async ValueTask<string?> SaveImageAsync(string outputFolder, CancellationToken cancellationToken)
    {
        using var response = await CallAsync("save_image", cancellationToken).ConfigureAwait(false);
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
            ? $"PHD2 {_guiderDevice.DeviceId} {Version}/{PHDSubvVersion}: AppState: {AppState ?? "Unkown"}"
            : $"PHD2 {_guiderDevice.DeviceId} not connected!";
}