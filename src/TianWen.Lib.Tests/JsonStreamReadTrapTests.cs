using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A shape guard (#1356): no JSON type holds a nullable struct (<c>T?</c>) that is read through its constructor and whose
/// last property can be null, unless it is listed below with the reason no stream read can trip on it.
/// <para>
/// A STREAM read of that shape (<c>JsonSerializer.DeserializeAsync</c>, <c>ReadFromJsonAsync</c>) throws "The JSON value
/// could not be converted" when the reader's buffer ends between the struct's trailing <c>null</c> and its closing brace,
/// while the same bytes parse whole. Measured: about one buffer offset in 350; the struct non-nullable, a record class,
/// or a struct ending in a number never failed; the reader fills its 16 KiB buffer before parsing, so only a payload past
/// 16 KiB is exposed. It broke #1281's 2,600-frame statistics file (fixed by parsing the file's bytes whole, #1357).
/// </para>
/// <para>
/// The shape lives in the type graph, which only the serializer's own metadata and the types show, so this reads types,
/// as <see cref="SignalDefaultsTests"/> does for a record struct's defaults. A new entry below is a decision: say why no
/// stream read can trip on it (the payload stays under 16 KiB, or it is parsed from bytes), or make the read whole.
/// </para>
/// </summary>
public class JsonStreamReadTrapTests
{
    /// <summary>The shapes in the build today, each as "Holder.property is Inner?", with why none can trip a stream read.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        // PlanetaryCaptureStatistics.TryLoadAsync parses the file's bytes whole (#1357); nothing reads it through a stream.
        ["FrameLimb.fit is LimbFit?"] = "read whole",
        ["CaptureStatistics.limbAll is LimbFit?"] = "read whole",
        ["CaptureStatistics.limbBest is LimbFit?"] = "read whole",
        // FrameWire parses its header from bytes; Image.FromStreamAsync reads an ImageMeta of a few KB, from test data only.
        ["ImageMeta.colour_calibration is ColourCalibration?"] = "bytes, or a payload under 16 KiB",
        // A profile answer is about 1 KB (the largest profile 1,118 bytes): the first 16 KiB fill holds it whole.
        ["ProfileDetailDto.data is ProfileData?"] = "a payload under 16 KiB",
    };

    [Fact]
    public void NoJsonTypeHoldsANullableStructThatEndsInNullUnlessItsReadIsSafe()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var context in Contexts())
        {
            Walk(context, found);
        }

        // Every context of the build was reached: a scan that found none would pass on nothing.
        found.ShouldContain("FrameLimb.fit is LimbFit?");
        var unexplained = found.Where(f => !Allowed.ContainsKey(f)).ToArray();
        unexplained.ShouldBeEmpty(
            "a JSON type holds a nullable struct read through its constructor whose last property can be null: a stream read "
            + "throws when its buffer ends between that null and the brace (#1356). Parse the payload whole, or keep it under "
            + "16 KiB and add it to Allowed with the reason: " + string.Join("; ", unexplained));
        Allowed.Keys.Where(k => !found.Contains(k)).ShouldBeEmpty("an allowed shape no longer exists: delete its line");
    }

    /// <summary>Every source-generated context in the TianWen assemblies beside the tests (the tests' own excluded).</summary>
    private static IEnumerable<JsonSerializerContext> Contexts()
    {
        var dir = AppContext.BaseDirectory;
        var assemblies = Directory.GetFiles(dir, "TianWen*.dll").Concat(Directory.GetFiles(dir, "tianwen*.dll"))
            .Where(f => !Path.GetFileName(f).Contains(".Tests", StringComparison.OrdinalIgnoreCase))
            .Select(Assembly.LoadFrom)
            .Distinct();
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = [.. e.Types.OfType<Type>()];
            }
            foreach (var type in types.Where(t => typeof(JsonSerializerContext).IsAssignableFrom(t) && !t.IsAbstract))
            {
                if (type.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is JsonSerializerContext context)
                {
                    yield return context;
                }
            }
        }
    }

    // Breadth first from the context's own types through every element, key and property type.
    private static void Walk(JsonSerializerContext context, SortedSet<string> found)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>(context.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(JsonTypeInfo<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0]));
        while (queue.TryDequeue(out var type))
        {
            if (!seen.Add(type) || TypeInfo(context, type) is not { } info)
            {
                continue;
            }
            if (info.ElementType is { } element)
            {
                queue.Enqueue(element);
            }
            if (info.KeyType is { } key)
            {
                queue.Enqueue(key);
            }
            if (info.Kind != JsonTypeInfoKind.Object)
            {
                continue;
            }
            foreach (var property in info.Properties)
            {
                queue.Enqueue(property.PropertyType);
                if (Nullable.GetUnderlyingType(property.PropertyType) is not { } inner)
                {
                    continue;
                }
                queue.Enqueue(inner);
                if (TypeInfo(context, inner) is not { Kind: JsonTypeInfoKind.Object, Properties.Count: > 0 } innerInfo
                    || !inner.GetConstructors().Any(c => c.GetParameters().Length > 0))
                {
                    continue;
                }
                var last = innerInfo.Properties[^1].PropertyType;
                if (!last.IsValueType || Nullable.GetUnderlyingType(last) is not null)
                {
                    found.Add($"{type.Name}.{property.Name} is {inner.Name}?");
                }
            }
        }
    }

    private static JsonTypeInfo? TypeInfo(JsonSerializerContext context, Type type)
    {
        try
        {
            return context.GetTypeInfo(type);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
