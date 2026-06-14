using System;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Weather;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Covers the credential store fallback (<see cref="FileCredentialStore"/> — the path CI/Linux
/// exercises; the Windows Credential Manager backend is not unit-tested to avoid writing into the
/// real per-user vault) and the property that fixes the OpenWeatherMap-key-wiped-on-provider-switch
/// bug: the secret is keyed by device, not by the device URI.
/// </summary>
public class CredentialStoreTests(ITestOutputHelper output)
{
    [Fact]
    public void FileCredentialStore_SetGetUpdateDelete_RoundTrips()
    {
        ICredentialStore store = new FileCredentialStore(new FakeExternal(output));
        var key = ICredentialStore.KeyFor("openweathermap", "apiKey");

        store.Get(key).ShouldBeNull();

        store.Set(key, "secret-1");
        store.Get(key).ShouldBe("secret-1");

        // Overwrite.
        store.Set(key, "secret-2");
        store.Get(key).ShouldBe("secret-2");

        // Null removes it.
        store.Set(key, null);
        store.Get(key).ShouldBeNull();
    }

    [Fact]
    public void OpenWeatherMapApiKey_IsKeyedByDevice_NotUri_SoSurvivesProviderSwitch()
    {
        ICredentialStore store = new FileCredentialStore(new FakeExternal(output));

        // The key the user entered, stored against the configured device (which may carry stale
        // query params from an earlier reconcile).
        var configured = new OpenWeatherMapDevice(
            new Uri("weather://OpenWeatherMapDevice/openweathermap?foo=bar#OpenWeatherMap"));
        store.Set(configured.CredentialKey, "my-owm-key");

        // Switching the weather provider away and back yields a freshly-discovered, *keyless* OWM
        // device URI. Because the credential key is derived from the device id (not the URI), the
        // secret is still found — the bug (key wiped on provider switch) is gone.
        var rediscovered = new OpenWeatherMapDevice();
        rediscovered.CredentialKey.ShouldBe(configured.CredentialKey);
        store.Get(rediscovered.CredentialKey).ShouldBe("my-owm-key");
    }
}
