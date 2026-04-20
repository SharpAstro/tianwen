using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

public class ProfileTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "Empty profile", "profile://profile/00000000-0000-0000-0000-000000000000?data=eyJNb3VudCI6Im5vbmU6Ly9Ob25lRGV2aWNlL05vbmUjTm9uZSIsIkd1aWRlciI6Im5vbmU6Ly9Ob25lRGV2aWNlL05vbmUjTm9uZSIsIk9UQXMiOltdLCJHdWlkZXJDYW1lcmEiOm51bGwsIkd1aWRlckZvY3VzZXIiOm51bGwsIk9BR19PVEFfSW5kZXgiOm51bGwsIkd1aWRlckZvY2FsTGVuZ3RoIjpudWxsLCJXZWF0aGVyIjpudWxsLCJTaXRlTGF0aXR1ZGUiOm51bGwsIlNpdGVMb25naXR1ZGUiOm51bGwsIlNpdGVFbGV2YXRpb24iOm51bGwsIlNpdGVUaWVCcmVha2VyIjowfQ#Empty profile")]
    public void GivenGuidAndProfileNameAProfileUriIsCreated(string guid, string name, string expectedUriStr)
    {
        var actualUri = Profile.CreateProfileUri(new Guid(guid), name, ProfileData.Empty);

        actualUri.ToString().ShouldBe(expectedUriStr);
    }

    [Theory]
    [InlineData("11111111-2222-3333-4444-555555555555", "Saved profile")]
    public async Task GivenProfileWhenSavedAndLoadedThenItIsIdentical(string guid, string name)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("D")));
        try
        {
            var external = new FakeExternal(outputHelper, dir);
            var profileIterator = new ProfileIterator(external, NullLogger<ProfileIterator>.Instance);

            var profile = new Profile(new Guid(guid), name, ProfileData.Empty);
            await profile.SaveAsync(external, cancellationToken);

            // when
            await profileIterator.DiscoverAsync(cancellationToken);
            var enumeratedProfiles = profileIterator.RegisteredDevices(DeviceType.Profile);

            // then
            profileIterator.RegisteredDeviceTypes.ShouldBe([DeviceType.Profile]);
            (await profileIterator.CheckSupportAsync(cancellationToken)).ShouldBeTrue();

            enumeratedProfiles.ShouldHaveSingleItem().ShouldNotBeNull().DeviceUri.ShouldBe(profile.DeviceUri);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}