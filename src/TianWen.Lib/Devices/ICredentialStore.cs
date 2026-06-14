namespace TianWen.Lib.Devices;

/// <summary>
/// Stores small secrets (e.g. weather-service API keys) outside the profile JSON — in the OS
/// credential vault on Windows (<see cref="WindowsCredentialStore"/>) and an owner-restricted file
/// elsewhere (<see cref="FileCredentialStore"/>).
/// <para>
/// Keys follow the convention <c>{deviceId}/{settingKey}</c> (e.g. <c>openweathermap/apiKey</c>) —
/// keyed by device, NOT by the full device URI, so the secret survives the URI being replaced when
/// the user switches providers or a device is re-discovered (the bug this fixes: the OWM key used to
/// live in <c>?apiKey=</c> on the URI and was wiped on every re-assign).
/// </para>
/// </summary>
public interface ICredentialStore
{
    /// <summary>Returns the stored secret for <paramref name="key"/>, or <c>null</c> if none is set.</summary>
    string? Get(string key);

    /// <summary>
    /// Stores the secret for <paramref name="key"/> when <paramref name="value"/> is non-null,
    /// or removes it when <paramref name="value"/> is <c>null</c>.
    /// </summary>
    void Set(string key, string? value);

    /// <summary>Builds a credential key from a device id and a setting key (the storage convention).</summary>
    static string KeyFor(string deviceId, string settingKey) => $"{deviceId}/{settingKey}";
}
