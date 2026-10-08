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
/// A shape guard (#1356): no JSON type holds a nullable struct (<c>T?</c>) whose last property can be null, unless it is
/// listed below with the reason no stream read can trip on it.
/// <para>
/// A STREAM read of that shape (<c>JsonSerializer.DeserializeAsync</c>, <c>ReadFromJsonAsync</c>) throws "The JSON value
/// could not be converted" when the reader's buffer ends between the struct's trailing <c>null</c> and its closing brace,
/// while the same bytes parse whole. It is System.Text.Json's own bug, open upstream as dotnet/runtime#110450 (a plain
/// settable struct fails there too, so a constructor is not needed). Measured: about one buffer offset in 350; the struct
/// non-nullable, a record class, or a struct ending in a number never failed; the reader fills its 16 KiB buffer before
/// parsing, so only a payload past 16 KiB is exposed. It broke #1281's 2,600-frame statistics file (fixed by parsing the
/// file's bytes whole, #1357).
/// </para>
/// <para>
/// The remedies make the TYPE safe: end the struct on a value that cannot be null, a number or an enum where the data allows
/// (<see cref="TianWen.Lib.Imaging.ColourCalibration"/>'s source), or a last property saying what the nullable ones mean
/// (<see cref="TianWen.Lib.Imaging.Planetary.LimbFit.Ringed"/>); or write it through a context that omits nulls or defaults
/// (<c>WhenWritingNull</c>, <c>WhenWritingDefault</c>), which writes no null at all, so this guard counts it as safe by itself.
/// Parsing a payload from its bytes protects only the reads that do it.
/// </para>
/// <para>
/// The shape lives in the type graph, which only the serializer's own metadata and the types show, so this reads types,
/// as <see cref="SignalDefaultsTests"/> does for a record struct's defaults.
/// </para>
/// </summary>
public class JsonStreamReadTrapTests
{
    /// <summary>
    /// Shapes allowed although they could end in null, each as "Holder.property is Inner?", with why no stream read can trip on it.
    /// Empty, and meant to stay so: a reason that rests on how a payload is read (from bytes, or under 16 KiB) is a promise
    /// nothing checks, where a sentinel makes the type itself safe whoever reads it. A line here is a decision made in review.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal);

    [Fact]
    public void NoJsonTypeHoldsANullableStructThatEndsInNullUnlessItsReadIsSafe()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var reached = new List<string>();
        foreach (var context in Contexts())
        {
            reached.Add(context.GetType().Name);
            Walk(context, found);
        }

        // The build's contexts were reached: a scan that found none would pass on nothing.
        reached.ShouldContain("PlanetaryStatisticsJsonContext");
        reached.ShouldContain("HostingJsonContext");
        var unexplained = found.Where(f => !Allowed.ContainsKey(f)).ToArray();
        unexplained.ShouldBeEmpty(
            "a JSON type holds a nullable struct whose last property can be null: a stream read throws when its buffer ends "
            + "between that null and the brace (#1356, dotnet/runtime#110450). End the struct on a value that cannot be null: "
            + "a number or an enum where the data allows (ColourCalibration's source), or a last property saying what the "
            + "nullable ones mean (LimbFit.Ringed; never a [JsonIgnore]d one), or write it through a context that omits nulls; "
            + "only as a last resort add it to Allowed with the reason: " + string.Join("; ", unexplained));
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

    // Breadth first from the context's own types through every element, key and property type. A context that omits nulls
    // (or every default) never writes a trailing null, so nothing it writes can trip the read.
    private static void Walk(JsonSerializerContext context, SortedSet<string> found)
    {
        if (context.Options.DefaultIgnoreCondition is JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault)
        {
            return;
        }
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
                if (TypeInfo(context, inner) is not { Kind: JsonTypeInfoKind.Object, Properties.Count: > 0 } innerInfo)
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
