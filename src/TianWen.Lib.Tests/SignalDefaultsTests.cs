using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Guards against a C# record-struct footgun that produced the Solve and Sync "0s exposure"
/// bug: a record struct's IMPLICIT parameterless constructor zero-inits every field and
/// SKIPS the primary-ctor default values. So <c>new SomeSignal()</c> silently differs from
/// the declared defaults whenever a default is non-zero / non-null / non-false (e.g.
/// <c>ExposureSeconds = 5.0</c> came through as 0, capturing a floored ~10 ms frame).
/// <para>
/// The fix is an explicit parameterless ctor that chains to the primary ctor
/// (<c>public T() : this(...) {}</c>). This test fails the build the moment a signal record
/// struct re-introduces the trap, so the next person does not have to rediscover it the hard way.
/// </para>
/// </summary>
public class SignalDefaultsTests
{
    [Fact]
    public void SignalRecordStructs_ParameterlessNew_ReproduceDeclaredDefaults()
    {
        var assembly = typeof(SkyMapSolveSyncSignal).Assembly;
        var offenders = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsValueType || type.IsEnum
                || !type.Name.EndsWith("Signal", StringComparison.Ordinal))
            {
                continue;
            }

            // The "primary" ctor is the public instance ctor with the most parameters.
            var primary = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();
            var pars = primary?.GetParameters() ?? Array.Empty<ParameterInfo>();

            // Only signals whose every parameter has a default can be built no-arg AND rely
            // on those defaults. Signals with a required parameter document "always specify",
            // so a parameterless `new()` is a clear misuse rather than a silent default drop.
            if (pars.Length == 0 || pars.Any(p => !p.HasDefaultValue))
            {
                continue;
            }

            var withDeclaredDefaults = primary!.Invoke(pars.Select(p => p.DefaultValue).ToArray());
            // Mirrors what `new T()` does at the call site (honours an explicit parameterless
            // ctor when present, otherwise zero-inits the struct).
            var parameterless = Activator.CreateInstance(type);

            if (!Equals(withDeclaredDefaults, parameterless))
            {
                offenders.Add(type.Name);
            }
        }

        offenders.ShouldBeEmpty(
            "these signal record structs declare non-default primary-ctor defaults that a "
            + "parameterless `new()` silently drops -- add an explicit "
            + "`public <Signal>() : this(...) {}` ctor that chains to the primary ctor: "
            + string.Join(", ", offenders));
    }
}
