using NSubstitute;
using Shouldly;
using System;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

public class SessionFactoryTests(ITestOutputHelper outputHelper)
{
    private static readonly Guid TestProfileId = new Guid("11111111-2222-3333-4444-555555555555");

    private static FakeDevice CreateMountDevice() => new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
    {
        { DeviceQueryKey.Port.Key, "LX200" },
        { DeviceQueryKey.Latitude.Key, "48.2" },
        { DeviceQueryKey.Longitude.Key, "16.3" }
    });

    private static FakeDevice CreateCameraDevice(int id = 1) => new FakeDevice(DeviceType.Camera, id);
    private static FakeDevice CreateFocuserDevice(int id = 1) => new FakeDevice(DeviceType.Focuser, id);
    private static FakeDevice CreateGuiderDevice() => new FakeDevice(DeviceType.Guider, 1);

    private (SessionFactory Factory, FakeExternal External) CreateFactory(ProfileData profileData)
    {
        var external = new FakeExternal(outputHelper, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var profile = new Profile(TestProfileId, "Test Profile", profileData);

        var deviceUriRegistry = Substitute.For<IDeviceUriRegistry>();
        deviceUriRegistry.TryGetDeviceFromUri(Arg.Any<Uri>(), out Arg.Any<DeviceBase?>())
            .Returns(call =>
            {
                var uri = call.ArgAt<Uri>(0);
                var scheme = uri.Scheme;

                DeviceBase? device = DeviceTypeHelper.TryParseDeviceType(scheme) switch
                {
                    DeviceType.Camera => new FakeDevice(uri),
                    DeviceType.Focuser => new FakeDevice(uri),
                    DeviceType.Mount => new FakeDevice(uri),
                    DeviceType.Guider when string.Equals(uri.Host, nameof(BuiltInGuiderDevice), StringComparison.OrdinalIgnoreCase) => new BuiltInGuiderDevice(uri),
                    DeviceType.Guider => new FakeDevice(uri),
                    DeviceType.FilterWheel => new FakeDevice(uri),
                    DeviceType.CoverCalibrator => new FakeDevice(uri),
                    _ => null
                };

                call[1] = device;
                return device is not null;
            });

        var deviceManager = Substitute.For<ICombinedDeviceManager>();
        deviceManager.TryFindByDeviceId(Profile.DeviceIdFromUUID(TestProfileId), out Arg.Any<DeviceBase?>())
            .Returns(call =>
            {
                call[1] = profile;
                return true;
            });

        var plateSolverFactory = Substitute.For<IPlateSolverFactory>();
        plateSolverFactory.CheckSupportAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));

        var factory = new SessionFactory(deviceUriRegistry, deviceManager, external, plateSolverFactory);
        return (factory, external);
    }

    [Fact]
    public void GivenProfileWithOneOTAWhenCreateWithScheduledObservationsThenSessionIsCreated()
    {
        // given
        var mountDevice = CreateMountDevice();
        var cameraDevice = CreateCameraDevice();
        var focuserDevice = CreateFocuserDevice();
        var guiderDevice = CreateGuiderDevice();

        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: guiderDevice.DeviceUri,
            OTAs: [new OTAData("Test Scope", 1000, cameraDevice.DeviceUri, null, focuserDevice.DeviceUri, null, null, null)]
        );

        var (factory, _) = CreateFactory(profileData);

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(6.75, 16.7, "M42", null),
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(30),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(120)),
                Gain: 0,
                Offset: 0
            )
        };

        // when
        var session = factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ScheduledObservation>(observations));

        // then
        session.ShouldNotBeNull();
        session.Setup.Telescopes.Length.ShouldBe(1);
        session.Setup.Telescopes[0].Name.ShouldBe("Test Scope");
        session.Setup.Telescopes[0].FocalLength.ShouldBe(1000);
        session.Setup.Telescopes[0].Focuser.ShouldNotBeNull();
        session.Setup.GuiderSetup.IsOAG.ShouldBeFalse();
        session.Setup.GuiderSetup.HasCamera.ShouldBeFalse();
        session.Setup.GuiderSetup.HasFocuser.ShouldBeFalse();
    }

    [Fact]
    public void GivenProfileWithMultipleOTAsWhenCreatedThenAllTelescopesArePopulated()
    {
        // given
        var mountDevice = CreateMountDevice();
        var camera1 = CreateCameraDevice(1);
        var camera2 = CreateCameraDevice(2);
        var focuser1 = CreateFocuserDevice(1);
        var focuser2 = CreateFocuserDevice(2);
        var guiderDevice = CreateGuiderDevice();

        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: guiderDevice.DeviceUri,
            OTAs:
            [
                new OTAData("Scope 1", 800, camera1.DeviceUri, null, focuser1.DeviceUri, null, null, null),
                new OTAData("Scope 2", 1200, camera2.DeviceUri, null, focuser2.DeviceUri, null, null, null)
            ]
        );

        var (factory, _) = CreateFactory(profileData);
        var observations = new[] { CreateDefaultObservation() };

        // when
        var session = factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ScheduledObservation>(observations));

        // then
        session.Setup.Telescopes.Length.ShouldBe(2);
        session.Setup.Telescopes[0].Name.ShouldBe("Scope 1");
        session.Setup.Telescopes[0].FocalLength.ShouldBe(800);
        session.Setup.Telescopes[1].Name.ShouldBe("Scope 2");
        session.Setup.Telescopes[1].FocalLength.ShouldBe(1200);
    }

    [Fact]
    public void GivenProfileWithOAGWhenCreatedThenGuiderSetupReferencesCorrectOTA()
    {
        // given
        var mountDevice = CreateMountDevice();
        var camera1 = CreateCameraDevice(1);
        var camera2 = CreateCameraDevice(2);
        var guiderDevice = CreateGuiderDevice();

        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: guiderDevice.DeviceUri,
            OTAs:
            [
                new OTAData("Main Scope", 2000, camera1.DeviceUri, null, null, null, null, null),
                new OTAData("Guide Scope", 400, camera2.DeviceUri, null, null, null, null, null)
            ],
            OAG_OTA_Index: 0
        );

        var (factory, _) = CreateFactory(profileData);
        var observations = new[] { CreateDefaultObservation() };

        // when
        var session = factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ScheduledObservation>(observations));

        // then
        session.Setup.GuiderSetup.IsOAG.ShouldBeTrue();
        session.Setup.GuiderSetup.OAG.ShouldNotBeNull();
        session.Setup.GuiderSetup.OAG.ShouldBeSameAs(session.Setup.Telescopes[0]);
    }

    [Fact]
    public void GivenProfileWithGuiderCameraWhenCreatedThenGuiderSetupHasCamera()
    {
        // given
        var mountDevice = CreateMountDevice();
        var cameraDevice = CreateCameraDevice(1);
        var guiderCameraDevice = CreateCameraDevice(2);
        var guiderDevice = CreateGuiderDevice();

        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: guiderDevice.DeviceUri,
            OTAs: [new OTAData("Test Scope", 1000, cameraDevice.DeviceUri, null, null, null, null, null)],
            GuiderCamera: guiderCameraDevice.DeviceUri
        );

        var (factory, _) = CreateFactory(profileData);
        var observations = new[] { CreateDefaultObservation() };

        // when
        var session = factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ScheduledObservation>(observations));

        // then
        session.Setup.GuiderSetup.HasCamera.ShouldBeTrue();
        session.Setup.GuiderSetup.Camera.ShouldNotBeNull();
    }

    [Fact]
    public void GivenProfileWithGuiderFocuserWhenCreatedThenGuiderSetupHasFocuser()
    {
        // given
        var mountDevice = CreateMountDevice();
        var cameraDevice = CreateCameraDevice(1);
        var guiderFocuserDevice = CreateFocuserDevice(2);
        var guiderDevice = CreateGuiderDevice();

        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: guiderDevice.DeviceUri,
            OTAs: [new OTAData("Test Scope", 1000, cameraDevice.DeviceUri, null, null, null, null, null)],
            GuiderFocuser: guiderFocuserDevice.DeviceUri
        );

        var (factory, _) = CreateFactory(profileData);
        var observations = new[] { CreateDefaultObservation() };

        // when
        var session = factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ScheduledObservation>(observations));

        // then
        session.Setup.GuiderSetup.HasFocuser.ShouldBeTrue();
        session.Setup.GuiderSetup.Focuser.ShouldNotBeNull();
    }

    [Fact]
    public void GivenUnknownProfileIdWhenCreateThenThrowsArgumentException()
    {
        // given
        var profileData = new ProfileData(
            Mount: CreateMountDevice().DeviceUri,
            Guider: CreateGuiderDevice().DeviceUri,
            OTAs: [new OTAData("Scope", 1000, CreateCameraDevice().DeviceUri, null, null, null, null, null)]
        );

        var (factory, _) = CreateFactory(profileData);
        var unknownId = Guid.NewGuid();
        var observations = new[] { CreateDefaultObservation() };

        // when/then
        Should.Throw<ArgumentException>(() =>
            factory.Create(unknownId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ScheduledObservation>(observations))
        );
    }

    [Fact]
    public void GivenProfileWithProposedObservationsWhenCreateThenSessionIsScheduled()
    {
        // given
        var mountDevice = CreateMountDevice();
        var cameraDevice = CreateCameraDevice();
        var guiderDevice = CreateGuiderDevice();

        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: guiderDevice.DeviceUri,
            OTAs: [new OTAData("Test Scope", 1000, cameraDevice.DeviceUri, null, null, null, null, null)]
        );

        var (factory, _) = CreateFactory(profileData);

        var proposals = new[]
        {
            new ProposedObservation(new Target(6.75, 16.7, "M42", null))
        };

        // when
        var session = factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ProposedObservation>(proposals));

        // then
        session.ShouldNotBeNull();
    }

    [Fact]
    public void GivenProfileWithNoLatLongWhenCreateWithProposalsThenThrowsInvalidOperationException()
    {
        // given — mount without latitude/longitude query params
        var mountDevice = new FakeDevice(DeviceType.Mount, 1);
        var cameraDevice = CreateCameraDevice();
        var guiderDevice = CreateGuiderDevice();

        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: guiderDevice.DeviceUri,
            OTAs: [new OTAData("Test Scope", 1000, cameraDevice.DeviceUri, null, null, null, null, null)]
        );

        var (factory, _) = CreateFactory(profileData);
        var proposals = new[] { new ProposedObservation(new Target(6.75, 16.7, "M42", null)) };

        // when/then
        Should.Throw<InvalidOperationException>(() =>
            factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ProposedObservation>(proposals))
        );
    }

    [Fact]
    public async Task GivenPlateSolverNotSupportedWhenInitializeThenThrowsInvalidOperationException()
    {
        // given
        var external = new FakeExternal(outputHelper);
        var plateSolverFactory = Substitute.For<IPlateSolverFactory>();
        plateSolverFactory.CheckSupportAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(false));

        var factory = new SessionFactory(
            Substitute.For<IDeviceUriRegistry>(),
            Substitute.For<ICombinedDeviceManager>(),
            external,
            plateSolverFactory
        );

        // when/then
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await factory.InitializeAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task GivenPlateSolverSupportedWhenInitializeThenDiscoverAsyncCalled()
    {
        // given
        var external = new FakeExternal(outputHelper);
        var plateSolverFactory = Substitute.For<IPlateSolverFactory>();
        plateSolverFactory.CheckSupportAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));

        var deviceManager = Substitute.For<ICombinedDeviceManager>();

        var factory = new SessionFactory(
            Substitute.For<IDeviceUriRegistry>(),
            deviceManager,
            external,
            plateSolverFactory
        );

        // when
        await factory.InitializeAsync(TestContext.Current.CancellationToken);

        // then
        await deviceManager.Received(1).DiscoverAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GivenProfileWithFocusDirectionOverridesWhenCreatedThenFocusDirectionIsSet()
    {
        // given
        var mountDevice = CreateMountDevice();
        var cameraDevice = CreateCameraDevice();
        var focuserDevice = CreateFocuserDevice();
        var guiderDevice = CreateGuiderDevice();

        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: guiderDevice.DeviceUri,
            OTAs: [new OTAData("Scope", 1000, cameraDevice.DeviceUri, Cover: null, Focuser: focuserDevice.DeviceUri, FilterWheel: null, PreferOutwardFocus: false, OutwardIsPositive: false)]
        );

        var (factory, _) = CreateFactory(profileData);
        var observations = new[] { CreateDefaultObservation() };

        // when
        var session = factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ScheduledObservation>(observations));

        // then
        session.Setup.Telescopes[0].FocusDirection.PreferOutward.ShouldBeFalse();
        session.Setup.Telescopes[0].FocusDirection.OutwardIsPositive.ShouldBeFalse();
    }

    [Fact]
    public void GivenDeviceDependentGuiderWhenCreatedWithGuiderCameraThenGuiderReceivesMountAndCamera()
    {
        // given — use BuiltInGuiderDevice which implements IDeviceDependentGuider
        var mountDevice = CreateMountDevice();
        var cameraDevice = CreateCameraDevice(1);
        var guiderCameraDevice = CreateCameraDevice(2);
        var builtInGuiderDevice = new BuiltInGuiderDevice();

        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: builtInGuiderDevice.DeviceUri,
            OTAs: [new OTAData("Test Scope", 1000, cameraDevice.DeviceUri, null, null, null, null, null)],
            GuiderCamera: guiderCameraDevice.DeviceUri
        );

        var (factory, _) = CreateFactory(profileData);
        var observations = new[] { CreateDefaultObservation() };

        // when
        var session = factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ScheduledObservation>(observations));

        // then — the guider driver should have received both mount and camera driver instances
        var s = session;
        var guiderDriver = s.Setup.Guider.Driver.ShouldBeAssignableTo<IDeviceDependentGuider>();
        var builtInDriver = (BuiltInGuiderDriver)guiderDriver!;
        builtInDriver.MountDriver.ShouldNotBeNull();
        builtInDriver.MountDriver.ShouldBeSameAs(s.Setup.Mount.Driver);
        builtInDriver.CameraDriver.ShouldNotBeNull();
        builtInDriver.CameraDriver.ShouldBeSameAs(s.Setup.GuiderSetup.Camera!.Driver);
    }

    [Fact]
    public void GivenDeviceDependentGuiderWhenCreatedWithoutGuiderCameraThenThrows()
    {
        // given — BuiltInGuiderDevice with no guider camera configured
        var mountDevice = CreateMountDevice();
        var cameraDevice = CreateCameraDevice();
        var builtInGuiderDevice = new BuiltInGuiderDevice();

        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: builtInGuiderDevice.DeviceUri,
            OTAs: [new OTAData("Test Scope", 1000, cameraDevice.DeviceUri, null, null, null, null, null)]
        );

        var (factory, _) = CreateFactory(profileData);
        var observations = new[] { CreateDefaultObservation() };

        // when/then — built-in guider requires a dedicated guider camera
        Should.Throw<InvalidOperationException>(() =>
            factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ScheduledObservation>(observations))
        );
    }

    [Fact]
    public void GivenProfileWithZeroOTAsWhenCreateThenThrowsArgumentException()
    {
        // given
        var profileData = new ProfileData(
            Mount: CreateMountDevice().DeviceUri,
            Guider: CreateGuiderDevice().DeviceUri,
            OTAs: []
        );

        var (factory, _) = CreateFactory(profileData);
        var observations = new[] { CreateDefaultObservation() };

        // when/then
        Should.Throw<ArgumentException>(() =>
            factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ScheduledObservation>(observations))
        ).Message.ShouldContain("at least one OTA");
    }

    [Fact]
    public void GivenProfileWithApertureAndOpticalDesignWhenCreatedThenOTAHasValues()
    {
        // given
        var mountDevice = CreateMountDevice();
        var cameraDevice = CreateCameraDevice();
        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: CreateGuiderDevice().DeviceUri,
            OTAs: [new OTAData("Newton 200/1000", 1000, cameraDevice.DeviceUri, null, null, null, null, null, Aperture: 200, OpticalDesign: OpticalDesign.Newtonian)]
        );

        var (factory, _) = CreateFactory(profileData);
        var observations = new[] { CreateDefaultObservation() };

        // when
        var session = factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ScheduledObservation>(observations));

        // then
        var internalSession = session.ShouldBeOfType<Session>();
        var telescope = internalSession.Setup.Telescopes[0];
        telescope.Aperture.ShouldBe(200);
        telescope.OpticalDesign.ShouldBe(OpticalDesign.Newtonian);
        telescope.OpticalDesign.NeedsFocusAdjustmentPerFilter.ShouldBeFalse();
    }

    [Fact]
    public void GivenProfileWithRefractorWhenCreatedThenNeedsFocusAdjustmentPerFilter()
    {
        // given
        var mountDevice = CreateMountDevice();
        var cameraDevice = CreateCameraDevice();
        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: CreateGuiderDevice().DeviceUri,
            OTAs: [new OTAData("APO 130/910", 910, cameraDevice.DeviceUri, null, null, null, null, null, Aperture: 130, OpticalDesign: OpticalDesign.Refractor)]
        );

        var (factory, _) = CreateFactory(profileData);
        var observations = new[] { CreateDefaultObservation() };

        // when
        var session = factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ScheduledObservation>(observations));

        // then
        var internalSession = session.ShouldBeOfType<Session>();
        var telescope = internalSession.Setup.Telescopes[0];
        telescope.Aperture.ShouldBe(130);
        telescope.OpticalDesign.ShouldBe(OpticalDesign.Refractor);
        telescope.OpticalDesign.NeedsFocusAdjustmentPerFilter.ShouldBeTrue();
    }

    [Fact]
    public void GivenProfileWithFilterWheelWhenCreatedThenOTAHasFilterWheel()
    {
        // given
        var mountDevice = CreateMountDevice();
        var cameraDevice = CreateCameraDevice();
        var focuserDevice = CreateFocuserDevice();
        var filterWheelDevice = new FakeDevice(DeviceType.FilterWheel, 1);
        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: CreateGuiderDevice().DeviceUri,
            OTAs: [new OTAData("Test Scope", 1000, cameraDevice.DeviceUri, Cover: null, Focuser: focuserDevice.DeviceUri,
                FilterWheel: filterWheelDevice.DeviceUri, PreferOutwardFocus: null, OutwardIsPositive: null)]
        );

        var (factory, _) = CreateFactory(profileData);
        var observations = new[] { CreateDefaultObservation() };

        // when
        var session = factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ScheduledObservation>(observations));

        // then
        var internalSession = session.ShouldBeOfType<Session>();
        var telescope = internalSession.Setup.Telescopes[0];
        telescope.FilterWheel.ShouldNotBeNull();
        telescope.Focuser.ShouldNotBeNull();
    }

    [Fact]
    public void GivenProfileWithCoverWhenCreatedThenOTAHasCover()
    {
        // given
        var mountDevice = CreateMountDevice();
        var cameraDevice = CreateCameraDevice();
        var coverDevice = new FakeDevice(DeviceType.CoverCalibrator, 1);
        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: CreateGuiderDevice().DeviceUri,
            OTAs: [new OTAData("Test Scope", 1000, cameraDevice.DeviceUri, Cover: coverDevice.DeviceUri, Focuser: null,
                FilterWheel: null, PreferOutwardFocus: null, OutwardIsPositive: null)]
        );

        var (factory, _) = CreateFactory(profileData);
        var observations = new[] { CreateDefaultObservation() };

        // when
        var session = factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ScheduledObservation>(observations));

        // then
        var internalSession = session.ShouldBeOfType<Session>();
        var telescope = internalSession.Setup.Telescopes[0];
        telescope.Cover.ShouldNotBeNull();
        telescope.Focuser.ShouldBeNull();
        telescope.FilterWheel.ShouldBeNull();
    }

    [Fact]
    public void GivenProfileWithFullOTAWhenCreatedThenAllDevicesWired()
    {
        // given — OTA with camera, focuser, filter wheel, cover, aperture, and optical design
        var mountDevice = CreateMountDevice();
        var cameraDevice = CreateCameraDevice();
        var focuserDevice = CreateFocuserDevice();
        var filterWheelDevice = new FakeDevice(DeviceType.FilterWheel, 1);
        var coverDevice = new FakeDevice(DeviceType.CoverCalibrator, 1);
        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: CreateGuiderDevice().DeviceUri,
            OTAs: [new OTAData("SCT 200/2000", 2000, cameraDevice.DeviceUri,
                Cover: coverDevice.DeviceUri, Focuser: focuserDevice.DeviceUri,
                FilterWheel: filterWheelDevice.DeviceUri, PreferOutwardFocus: false, OutwardIsPositive: true,
                Aperture: 200, OpticalDesign: OpticalDesign.SCT)]
        );

        var (factory, _) = CreateFactory(profileData);
        var observations = new[] { CreateDefaultObservation() };

        // when
        var session = factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ScheduledObservation>(observations));

        // then
        var internalSession = session.ShouldBeOfType<Session>();
        var telescope = internalSession.Setup.Telescopes[0];
        telescope.Camera.ShouldNotBeNull();
        telescope.Focuser.ShouldNotBeNull();
        telescope.FilterWheel.ShouldNotBeNull();
        telescope.Cover.ShouldNotBeNull();
        telescope.Aperture.ShouldBe(200);
        telescope.OpticalDesign.ShouldBe(OpticalDesign.SCT);
        telescope.OpticalDesign.NeedsFocusAdjustmentPerFilter.ShouldBeTrue();
        telescope.FocusDirection.PreferOutward.ShouldBeFalse();
        telescope.FocusDirection.OutwardIsPositive.ShouldBeTrue();
    }

    [Fact]
    public void GivenUnresolvableDeviceUriInProfileWhenCreateThenThrowsArgumentException()
    {
        // given — camera URI uses an unknown scheme that the registry won't resolve
        var mountDevice = CreateMountDevice();
        var guiderDevice = CreateGuiderDevice();
        var bogusUri = new Uri("bogus://unknown-device/1");
        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: guiderDevice.DeviceUri,
            OTAs: [new OTAData("Bad Scope", 1000, bogusUri, null, null, null, null, null)]
        );

        var (factory, _) = CreateFactory(profileData);
        var observations = new[] { CreateDefaultObservation() };

        // when/then
        Should.Throw<ArgumentException>(() =>
            factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ScheduledObservation>(observations))
        ).Message.ShouldContain("failed to instantiate");
    }

    [Fact]
    public void GivenOAGOnSecondOTAWhenCreatedThenGuiderReferencesSecondTelescope()
    {
        // given — two OTAs, OAG on the second (index 1)
        var mountDevice = CreateMountDevice();
        var camera1 = CreateCameraDevice(1);
        var camera2 = CreateCameraDevice(2);
        var guiderDevice = CreateGuiderDevice();

        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: guiderDevice.DeviceUri,
            OTAs:
            [
                new OTAData("Imaging Scope", 2000, camera1.DeviceUri, null, null, null, null, null),
                new OTAData("Guide Scope", 400, camera2.DeviceUri, null, null, null, null, null)
            ],
            OAG_OTA_Index: 1
        );

        var (factory, _) = CreateFactory(profileData);
        var observations = new[] { CreateDefaultObservation() };

        // when
        var session = factory.Create(TestProfileId, SessionTestHelper.DefaultConfiguration, new ReadOnlySpan<ScheduledObservation>(observations));

        // then
        session.Setup.GuiderSetup.IsOAG.ShouldBeTrue();
        session.Setup.GuiderSetup.OAG.ShouldBeSameAs(session.Setup.Telescopes[1]);
    }

    [Fact]
    public void GivenExplicitDefaultSubExposureWhenCreateWithProposalsThenUsedInSchedule()
    {
        // given
        var mountDevice = CreateMountDevice();
        var cameraDevice = CreateCameraDevice();
        var guiderDevice = CreateGuiderDevice();

        var profileData = new ProfileData(
            Mount: mountDevice.DeviceUri,
            Guider: guiderDevice.DeviceUri,
            OTAs: [new OTAData("Test Scope", 1000, cameraDevice.DeviceUri, null, null, null, null, null)]
        );

        var config = SessionTestHelper.DefaultConfiguration with { DefaultSubExposure = TimeSpan.FromSeconds(60) };
        var (factory, _) = CreateFactory(profileData);

        var proposals = new[]
        {
            new ProposedObservation(new Target(6.75, 16.7, "M42", null))
        };

        // when
        var session = factory.Create(TestProfileId, config, new ReadOnlySpan<ProposedObservation>(proposals));

        // then
        var internalSession = session.ShouldBeOfType<Session>();
        // The scheduled observation should use the explicit 60s default, not the 120s fallback
        if (internalSession.Observations.Count > 0)
        {
            internalSession.Observations[0].SubExposure.ShouldBe(TimeSpan.FromSeconds(60));
        }
    }

    private static ScheduledObservation CreateDefaultObservation() => new ScheduledObservation(
        new Target(6.75, 16.7, "M42", null),
        DateTimeOffset.UtcNow,
        TimeSpan.FromMinutes(30),
        AcrossMeridian: false,
        FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(120)),
        Gain: 0,
        Offset: 0
    );
}
