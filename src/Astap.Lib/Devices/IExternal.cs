using System;

namespace Astap.Lib.Devices;

public interface IExternal
{
    void Sleep(TimeSpan duration);

    void LogInfo(string info);

    void LogError(string error);

    string OutputFolder { get; }
}
