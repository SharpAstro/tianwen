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
/// A shape guard (#1356): no JSON type holds a nullable struct (<c>T?</c>), as a property or as a sequence's element, whose
/// last property can be null, unless it is listed below with the reason no stream read can trip on it.
/// <para>
/// A STREAM read of that shape (<c>JsonSerializer.DeserializeAsync</c>, <c>ReadFromJsonAsync</c>) throws "The JSON value
/// could not be converted" when the reader's buffer ends between the struct's trailing <c>null</c> and its closing brace,
/// while the same bytes parse whole. It is System.Text.Json's own bug, open upstream as dotnet/runtime#110450 (a plain
/// settable struct fails there too, so a constructor is not needed); upstream confirms the symptom, and the trigger is what was
/// measured here. Measured: about one buffer offset in 350; the struct non-nullable, a record class, or a struct ending in a
/// number never failed; the reader fills its 16 KiB buffer before parsing, so only a payload past 16 KiB is exposed. It broke
/// #1281's 2,600-frame statistics file (fixed by parsing the file's bytes whole, #1357).
/// </para>
/// <para>
/// Where the struct is held decides it (#1414, measured on .NET 10 with the buffer's end swept over 288 positions of a 20-entry
/// payload): as a property it tripped 12 times, as a sequence's element (<c>T?[]</c>, <c>ImmutableArray&lt;T?&gt;</c>) 125
/// times; as the root, a dictionary's value, or one level down (a nullable struct whose last property is a non-nullable struct
/// that ends in null) it never did. So this checks a property and an element, and walks into every struct whatever holds it.
/// </para>
/// <para>
/// The remedies make the TYPE safe: end the struct on a value that cannot be null, a number or an enum where the data allows
/// (<see cref="TianWen.Lib.Imaging.ColourCalibration"/>'s source), or a last property saying what the nullable ones mean
/// (<see cref="TianWen.Lib.Imaging.Planetary.LimbFit.Ringed"/>); or write it through a context that omits nulls or defaults
/// (<c>WhenWritingNull</c>, <c>WhenWritingDefault</c>), which writes no null at all, so this guard counts it as safe by itself.
/// That holds only while nothing writes the context's types through other options: <c>HostingJsonContext</c> is also chained into
/// ASP.NET's, but only to bind request bodies, a read, while every response passes the context's own type info
/// (<c>EnvelopeResults.Json</c> takes nothing else). Walked without the skip, the build holds one shape there,
/// <c>ProfileDetailDto.Data</c> (a <c>ProfileData?</c> ending in <c>MountLimits</c>), a response, written with nulls omitted since
/// it was added. Parsing a payload from its bytes protects only the reads that do it.
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

    [Fact]
    public void TheWalkFindsEveryShapeAStreamReadTripsOnAndNoOther()
    {
        // #1414: the walk checked a nullable struct only as a property, so a T?[] or an ImmutableArray<T?>, the shape a stream read
        // trips on most, went unseen (seen failing: the old walk found the other three and neither element).
        var found = new SortedSet<string>(StringComparer.Ordinal);

        Walk(TrapShapesContext.Default, found);

        found.ShouldBe(
        [
            "EndsInNull?[] element is EndsInNull?",
            "HoldsTrap.Trap is EndsInNull?",
            "ImmutableArray<EndsInNull?> element is EndsInNull?",
            "TrapDerived.Trap is EndsInNull?",
            "TrapWrapper.Deep is EndsInNull?",
        ]);
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

    // Breadth first from the context's own types through every element, key, property and derived type, and into the struct
    // under every nullable one: the generator lists every type of the graph among the context's own, and these steps keep the walk
    // from leaning on that. A context that omits nulls (or every default) never writes a trailing null, so nothing it writes can
    // trip the read (the class's summary says when that holds).
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
            if (!seen.Add(type))
            {
                continue;
            }
            // A nullable struct's own metadata is a converter's, with no properties: what it holds is its underlying type's.
            if (Nullable.GetUnderlyingType(type) is { } underlying)
            {
                queue.Enqueue(underlying);
                continue;
            }
            if (TypeInfo(context, type) is not { } info)
            {
                continue;
            }
            if (info.ElementType is { } element)
            {
                // A sequence's element trips as a property does, and far more often; a dictionary's value never did.
                if (info.Kind == JsonTypeInfoKind.Enumerable)
                {
                    Check(context, element, $"{Named(type)} element", found);
                }
                queue.Enqueue(element);
            }
            if (info.KeyType is { } key)
            {
                queue.Enqueue(key);
            }
            foreach (var derived in info.PolymorphismOptions?.DerivedTypes ?? [])
            {
                queue.Enqueue(derived.DerivedType);
            }
            if (info.Kind != JsonTypeInfoKind.Object)
            {
                continue;
            }
            foreach (var property in info.Properties)
            {
                Check(context, property.PropertyType, $"{Named(type)}.{property.Name}", found);
                queue.Enqueue(property.PropertyType);
            }
        }
    }

    // A nullable struct held where a stream read trips on it (a property, or a sequence's element) whose own last property can be null.
    private static void Check(JsonSerializerContext context, Type held, string where, SortedSet<string> found)
    {
        if (Nullable.GetUnderlyingType(held) is not { } inner
            || TypeInfo(context, inner) is not { Kind: JsonTypeInfoKind.Object, Properties.Count: > 0 } innerInfo)
        {
            return;
        }
        var last = innerInfo.Properties[^1].PropertyType;
        if (!last.IsValueType || Nullable.GetUnderlyingType(last) is not null)
        {
            found.Add($"{where} is {inner.Name}?");
        }
    }

    // A type as C# writes it, so a generic sequence reads ImmutableArray<Foo?> rather than ImmutableArray`1.
    private static string Named(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner)
        {
            return Named(inner) + "?";
        }
        if (type.IsArray)
        {
            return Named(type.GetElementType() ?? typeof(object)) + "[]";
        }
        if (!type.IsGenericType)
        {
            return type.Name;
        }
        return $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(Named))}>";
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

// The shapes TheWalkFindsEveryShapeAStreamReadTripsOnAndNoOther walks: each one a stream read trips on, once, and each measured safe.
internal struct EndsInNull
{
    public int A { get; set; }
    public string? B { get; set; }
}

internal struct EndsInNumber
{
    public string? B { get; set; }
    public int A { get; set; }
}

// Ends in a number itself, so only what it holds trips; it is reached only as a nullable element.
internal struct TrapWrapper
{
    public EndsInNull? Deep { get; set; }
    public int N { get; set; }
}

// Ends in a non-nullable struct that ends in null: one level down, which never tripped.
internal struct TrapOuter
{
    public int A { get; set; }
    public EndsInNull Last { get; set; }
}

internal sealed class HoldsTrap
{
    public EndsInNull? Trap { get; set; }
}

internal sealed class HoldsSequences
{
    public EndsInNull?[] Array { get; set; } = [];
    public System.Collections.Immutable.ImmutableArray<EndsInNull?> Immutable { get; set; } = [];
    public TrapWrapper?[] Wrapped { get; set; } = [];
}

internal sealed class HoldsSafe
{
    public EndsInNumber? EndsInNumber { get; set; }
    public EndsInNull NotNullable { get; set; }
    public TrapOuter? OneLevelDown { get; set; }
    public Dictionary<string, EndsInNull?> Values { get; set; } = [];
}

[JsonDerivedType(typeof(TrapDerived), "derived")]
internal class TrapBase
{
    public int Id { get; set; }
}

internal sealed class TrapDerived : TrapBase
{
    public EndsInNull? Trap { get; set; }
}

[JsonSerializable(typeof(HoldsTrap))]
[JsonSerializable(typeof(HoldsSequences))]
[JsonSerializable(typeof(HoldsSafe))]
[JsonSerializable(typeof(TrapBase))]
[JsonSerializable(typeof(EndsInNull?))]
internal partial class TrapShapesContext : JsonSerializerContext;
