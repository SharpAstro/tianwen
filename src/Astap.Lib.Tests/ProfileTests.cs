using Astap.Lib.Devices.Builtin;
using Shouldly;
using System;
using Xunit;

namespace Astap.Lib.Tests;

public class ProfileTests
{
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "Empty profile", "device://profile/00000000-0000-0000-0000-000000000000?displayName=Empty profile&values=e30#Profile")]
    public void GivenGuidAndProfileNameAProfileUriIsCreated(string guid, string name, string expectedUriStr)
    {
        var actualUri = Profile.CreateProfileUri(new Guid(guid), name);

        actualUri.ToString().ShouldBe(expectedUriStr);
    }
}
