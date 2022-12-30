using System;

namespace Astap.Lib.Devices;

public interface IExternal
{
    void Sleep(TimeSpan duration);

    void LogInfo(string info);

    void LogWarning(string warning);

    void LogError(string error);

    void LogException(Exception ex, string extra);

    string OutputFolder { get; }
}
