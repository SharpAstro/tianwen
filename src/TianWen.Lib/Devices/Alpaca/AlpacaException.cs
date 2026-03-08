using System;

namespace TianWen.Lib.Devices.Alpaca;

/// <summary>
/// Exception thrown when an Alpaca device returns a non-zero error code.
/// </summary>
public class AlpacaException : Exception
{
    public int ErrorNumber { get; }

    public AlpacaException(int errorNumber, string? errorMessage)
        : base(errorMessage ?? $"Alpaca error {errorNumber}")
    {
        ErrorNumber = errorNumber;
    }
}
