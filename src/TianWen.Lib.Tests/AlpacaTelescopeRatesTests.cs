using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Alpaca;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The Alpaca mount driver's rates against a stub server: the axis rates it caches at connect (#430,
/// which used to throw and so crashed polar alignment on an Alpaca mount), the tracking rates it
/// enumerates (#429), and the DriveRates numbering on the tracking-rate property (#771).
/// </summary>
public class AlpacaTelescopeRatesTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AxisRatesAreTheServedRatesPerAxis()
    {
        var server = new StubTelescopeServer();
        server.AxisRatesJson[0] = """[{"Minimum":0.0,"Maximum":0.5},{"Minimum":1.0,"Maximum":4.0}]""";
        server.AxisRatesJson[1] = """[{"Minimum":0.25,"Maximum":2.0}]""";

        var driver = await ConnectAsync(server);

        driver.AxisRates(TelescopeAxis.Primary).ShouldBe([new AxisRate(0.0, 0.5), new AxisRate(1.0, 4.0)]);
        driver.AxisRates(TelescopeAxis.Seconary).ShouldBe([new AxisRate(0.25, 2.0)]);
        // The tertiary axis cannot move, so its rates are never asked for.
        driver.AxisRates(TelescopeAxis.Tertiary).ShouldBeEmpty();
        server.Requests.ShouldNotContain(r => r.Member == "axisrates" && r.Axis == "2");
    }

    [Fact]
    public async Task AFailedAxisRatesReadGivesAnEmptyList()
    {
        var server = new StubTelescopeServer();
        server.AxisRatesJson[0] = null; // the axis can move, but its rates answer an ASCOM error
        server.AxisRatesJson[1] = """[{"Minimum":0.25,"Maximum":2.0}]""";

        var driver = await ConnectAsync(server);

        driver.Connected.ShouldBeTrue();
        driver.AxisRates(TelescopeAxis.Primary).ShouldBeEmpty();
        driver.AxisRates(TelescopeAxis.Seconary).ShouldBe([new AxisRate(0.25, 2.0)]);
        server.Requests.ShouldContain(r => r.Member == "axisrates" && r.Axis == "0");
    }

    [Fact]
    public async Task TheAxisQueryReachesTheServerAsItsOwnParameter()
    {
        var server = new StubTelescopeServer();
        server.AxisRatesJson[0] = """[{"Minimum":0.0,"Maximum":1.0}]""";

        await ConnectAsync(server);

        // Before the client joined its fields with '&', the server read Axis as "0?ClientID=1".
        server.Requests.ShouldContain(r => r.Member == "canmoveaxis" && r.Axis == "0");
        server.Requests.ShouldContain(r => r.Member == "axisrates" && r.Axis == "0");
    }

    [Fact]
    public async Task TrackingSpeedsAreTheServedDriveRatesMapped()
    {
        var server = new StubTelescopeServer { TrackingRatesJson = "[0,1,2,3]" };

        var driver = await ConnectAsync(server);

        driver.TrackingSpeeds.ShouldBe([TrackingSpeed.Sidereal, TrackingSpeed.Lunar, TrackingSpeed.Solar, TrackingSpeed.King]);
    }

    [Fact]
    public async Task AnUnknownDriveRateInTheEnumerationIsSkipped()
    {
        var server = new StubTelescopeServer { TrackingRatesJson = "[0,7,2]" };

        var driver = await ConnectAsync(server);

        driver.TrackingSpeeds.ShouldBe([TrackingSpeed.Sidereal, TrackingSpeed.Solar]);
    }

    [Fact]
    public async Task AFailedTrackingRatesReadGivesAnEmptyList()
    {
        var server = new StubTelescopeServer { TrackingRatesJson = null };

        var driver = await ConnectAsync(server);

        driver.Connected.ShouldBeTrue();
        driver.TrackingSpeeds.ShouldBeEmpty();
    }

    [Fact]
    public async Task WireZeroReadsAsSidereal()
    {
        var server = new StubTelescopeServer { TrackingRate = 0 };

        var driver = await ConnectAsync(server);

        (await driver.GetTrackingSpeedAsync(TestContext.Current.CancellationToken)).ShouldBe(TrackingSpeed.Sidereal);
    }

    [Fact]
    public async Task AnUnknownWireTrackingRateThrowsRatherThanBeingGuessed()
    {
        var server = new StubTelescopeServer { TrackingRate = 9 };

        var driver = await ConnectAsync(server);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await driver.GetTrackingSpeedAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(TrackingSpeed.Sidereal, "0")]
    [InlineData(TrackingSpeed.Lunar, "1")]
    [InlineData(TrackingSpeed.King, "3")]
    public async Task SettingASpeedSendsItsDriveRate(TrackingSpeed speed, string expected)
    {
        var server = new StubTelescopeServer();

        var driver = await ConnectAsync(server);
        await driver.SetTrackingSpeedAsync(speed, TestContext.Current.CancellationToken);

        server.Requests.Last(r => r.Method == HttpMethod.Put && r.Member == "trackingrate").Form["TrackingRate"].ShouldBe(expected);
    }

    [Fact]
    public async Task SettingNoneSendsNothing()
    {
        var server = new StubTelescopeServer();

        var driver = await ConnectAsync(server);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await driver.SetTrackingSpeedAsync(TrackingSpeed.None, TestContext.Current.CancellationToken));

        server.Requests.ShouldNotContain(r => r.Method == HttpMethod.Put && r.Member == "trackingrate");
    }

    private async Task<IMountDriver> ConnectAsync(StubTelescopeServer server)
    {
        var device = new AlpacaDevice(DeviceType.Telescope, "stub-mount", "mount.invalid", 11111, 0, "Stub Mount")
        {
            Client = new AlpacaClient(new HttpClient(server))
        };
        var sp = new FakeExternal(output).BuildServiceProvider();
        var driver = new AlpacaTelescopeDriver(device, sp);
        await driver.ConnectAsync(TestContext.Current.CancellationToken);
        return driver;
    }

    private sealed record StubRequest(HttpMethod Method, string Member, string? Axis, IReadOnlyDictionary<string, string> Form);

    /// <summary>
    /// Answers the members the driver reads at connect. Anything not served answers ASCOM NotImplemented,
    /// which the driver must tolerate exactly as it would from a real server.
    /// </summary>
    private sealed class StubTelescopeServer : HttpMessageHandler
    {
        private const int NotImplemented = 0x400;

        public ConcurrentQueue<StubRequest> Requests { get; } = new ConcurrentQueue<StubRequest>();

        /// <summary>Per axis, the JSON array served for axisrates, or null to answer with an error.</summary>
        public string?[] AxisRatesJson { get; } = [null, null, null];

        /// <summary>Per axis, what canmoveaxis answers.</summary>
        public bool[] CanMove { get; } = [true, true, false];

        /// <summary>The JSON array served for trackingrates, or null to answer with an error.</summary>
        public string? TrackingRatesJson { get; init; } = "[0]";

        public int TrackingRate { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("request has no URI");
            var member = uri.Segments[^1].ToLowerInvariant();
            var axis = HttpUtility.ParseQueryString(uri.Query)["Axis"];

            var form = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (request.Content is { } content)
            {
                var parsed = HttpUtility.ParseQueryString(await content.ReadAsStringAsync(cancellationToken));
                foreach (var key in parsed.AllKeys)
                {
                    if (key is not null)
                    {
                        form[key] = parsed[key] ?? "";
                    }
                }
            }
            Requests.Enqueue(new StubRequest(request.Method, member, axis, form));

            if (request.Method == HttpMethod.Put)
            {
                return Json("""{"ClientTransactionID":0,"ServerTransactionID":0,"ErrorNumber":0,"ErrorMessage":""}""");
            }

            var axisIndex = int.TryParse(axis, out var a) ? a : -1;
            var value = member switch
            {
                "connected" => "true",
                "equatorialsystem" => "2",
                "trackingrate" => TrackingRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "trackingrates" => TrackingRatesJson,
                "canmoveaxis" when axisIndex is >= 0 and < 3 => CanMove[axisIndex] ? "true" : "false",
                "axisrates" when axisIndex is >= 0 and < 3 => AxisRatesJson[axisIndex],
                _ => null
            };

            return value is null
                ? Json($$"""{"ClientTransactionID":0,"ServerTransactionID":0,"ErrorNumber":{{NotImplemented}},"ErrorMessage":"{{member}} not served"}""")
                : Json($$"""{"Value":{{value}},"ClientTransactionID":0,"ServerTransactionID":0,"ErrorNumber":0,"ErrorMessage":""}""");

            HttpResponseMessage Json(string body) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
        }
    }
}
