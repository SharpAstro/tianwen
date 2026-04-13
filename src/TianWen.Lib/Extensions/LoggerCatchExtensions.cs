using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Extensions;

/// <summary>
/// Extension methods on <see cref="ILogger"/> that mirror the former <c>IExternal.Catch*</c> default methods.
/// </summary>
public static class LoggerCatchExtensions
{
    /// <summary>
    /// Uses <see langword="try"/> <see langword="catch"/> to safely execute <paramref name="action"/>.
    /// Returns <see langword="true"/> on success and <see langword="false"/> on failure, logging errors.
    /// </summary>
    public static bool Catch(this ILogger logger, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception {Message} while executing: {Method}", ex.Message, action.Method.Name);
            return false;
        }
    }

    /// <summary>
    /// Uses <see langword="try"/> <see langword="catch"/> to safely execute <paramref name="func"/>.
    /// Returns result or <paramref name="default"/> on failure, logging errors.
    /// </summary>
    public static T Catch<T>(this ILogger logger, Func<T> func, T @default = default)
        where T : struct
    {
        try
        {
            return func();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception {Message} while executing: {Method}", ex.Message, func.Method.Name);
            return @default;
        }
    }

    /// <summary>
    /// Asynchronously awaits <paramref name="asyncFunc"/>, returning <see langword="false"/> if an exception occurred.
    /// </summary>
    public static async ValueTask<bool> CatchAsync(this ILogger logger, Func<CancellationToken, ValueTask> asyncFunc, CancellationToken cancellationToken)
    {
        try
        {
            await asyncFunc(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception {Message} while executing: {Method}", ex.Message, asyncFunc.Method.Name);
            return false;
        }
    }

    /// <summary>
    /// Asynchronously awaits <paramref name="asyncFunc"/>, returning <paramref name="default"/> if an exception occurred.
    /// </summary>
    public static async ValueTask<T> CatchAsync<T>(this ILogger logger, Func<CancellationToken, ValueTask<T>> asyncFunc, CancellationToken cancellationToken, T @default = default) where T : struct
    {
        try
        {
            return await asyncFunc(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception {Message} while executing: {Method}", ex.Message, asyncFunc.Method.Name);
            return @default;
        }
    }

    /// <summary>
    /// If <paramref name="condition"/> is true, awaits <paramref name="asyncFunc"/>, returning <paramref name="default"/> on exception or when condition is false.
    /// </summary>
    public static ValueTask<T> CatchAsyncIf<T>(this ILogger logger, bool condition, Func<CancellationToken, ValueTask<T>> asyncFunc, CancellationToken cancellationToken, T @default = default) where T : struct
        => condition ? logger.CatchAsync(asyncFunc, cancellationToken, @default) : ValueTask.FromResult(@default);

    /// <summary>
    /// Asynchronously awaits <paramref name="asyncFunc"/>, returning <see langword="false"/> if an exception occurred.
    /// </summary>
    public static async Task<bool> CatchAsync(this ILogger logger, Func<CancellationToken, Task> asyncFunc, CancellationToken cancellationToken)
    {
        try
        {
            await asyncFunc(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception {Message} while executing: {Method}", ex.Message, asyncFunc.Method.Name);
            return false;
        }
    }

    /// <summary>
    /// Asynchronously awaits <paramref name="asyncFunc"/>, returning <paramref name="default"/> if an exception occurred.
    /// </summary>
    public static async Task<T> CatchAsync<T>(this ILogger logger, Func<CancellationToken, Task<T>> asyncFunc, CancellationToken cancellationToken, T @default = default) where T : struct
    {
        try
        {
            return await asyncFunc(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception {Message} while executing: {Method}", ex.Message, asyncFunc.Method.Name);
            return @default;
        }
    }
}
