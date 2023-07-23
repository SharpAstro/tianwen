using Astap.Lib.Devices.Builtin;
using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Astap.Lib.Tests;

public class ProfileTests
{
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "Empty profile", "profile://profile/00000000-0000-0000-0000-000000000000?values=e30#Empty profile")]
    public void GivenGuidAndProfileNameAProfileUriIsCreated(string guid, string name, string expectedUriStr)
    {
        var actualUri = Profile.CreateProfileUri(new Guid(guid), name);

        actualUri.ToString().ShouldBe(expectedUriStr);
    }

    [Theory]
    [InlineData("11111111-2222-3333-4444-555555555555", "Saved profile", "Key", "some:://value")]
    public async Task GivenProfileWhenSavedAndLoadedThenItIsIdentical(string guid, string name, string key, string valueUriStr)
    {
        // given
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("D")));
        try
        {
            var profile = new Profile(new Guid(guid), name, new Dictionary<string, Uri> { [key] = new Uri(valueUriStr) });

            // when
            await profile.SaveAsync(dir);
            var actualProfiles = await Profile.LoadExistingProfilesAsync(dir);

            // then
            actualProfiles.ShouldNotBeEmpty();
            actualProfiles.Count.ShouldBe(1);

            actualProfiles[0].ShouldBeEquivalentTo(profile);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
