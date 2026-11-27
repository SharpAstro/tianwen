using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>What kind of control a setting is edited with.</summary>
public enum OperationSettingKind
{
    /// <summary>On or off: a switch, or a checkbox where the engine has no switch.</summary>
    Bool,

    /// <summary>One of a few named values (an enum): a dropdown.</summary>
    Choice,

    /// <summary>A whole number: a field.</summary>
    Integer,

    /// <summary>A number with a fraction: a field.</summary>
    Number,

    /// <summary>Text: a field.</summary>
    Text,

    /// <summary>A list or a record (an exclusion polygon, a white balance): shown, edited in the project file or on the command line.</summary>
    Other,
}

/// <summary>One setting of an operation as a panel edits it, taken from the settings type's JSON contract and the operation's description.</summary>
/// <param name="Name">The property's name in the settings JSON (camelCase).</param>
/// <param name="Kind">The control it is edited with.</param>
/// <param name="Nullable">True for a setting that can be left out (a null stands for the operation's own value).</param>
/// <param name="Choices">For <see cref="OperationSettingKind.Choice"/>: the values, as the JSON writes them.</param>
/// <param name="Default">The default as JSON text; null for a setting whose default is none.</param>
/// <param name="Summary">What it does, from <see cref="OperationDescription.Settings"/>; empty where the operation says nothing of it.</param>
public sealed record OperationSettingField(string Name, OperationSettingKind Kind, bool Nullable, ImmutableArray<string> Choices, string? Default, string Summary);

/// <summary>
/// An operation's settings as controls, derived from its settings type's <see cref="JsonTypeInfo"/> in
/// <see cref="OperationSettingsJsonContext"/>: no reflection over the type, so a new setting shows up with no change here.
/// </summary>
public static class OperationSettingsForm
{
    /// <summary>The fields of <paramref name="operation"/>'s settings, in the type's own order; empty for an operation that offers no contract.</summary>
    public static ImmutableArray<OperationSettingField> Describe(IOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.SettingsContract is not { Kind: JsonTypeInfoKind.Object } info)
        {
            return [];
        }

        var defaults = JsonNode.Parse(operation.DefaultSettings) as JsonObject;
        var fields = ImmutableArray.CreateBuilder<OperationSettingField>(info.Properties.Count);
        foreach (var property in info.Properties)
        {
            var type = property.PropertyType;
            var underlying = Nullable.GetUnderlyingType(type);
            var plain = underlying ?? type;
            var isNullable = underlying is not null || !type.IsValueType && type == typeof(string);
            var choices = ImmutableArray<string>.Empty;
            OperationSettingKind kind;
            if (plain == typeof(bool))
            {
                kind = OperationSettingKind.Bool;
            }
            else if (plain.IsEnum)
            {
                kind = OperationSettingKind.Choice;
                choices = [.. Enum.GetNames(plain)];
            }
            else if (plain == typeof(int) || plain == typeof(long) || plain == typeof(short) || plain == typeof(byte))
            {
                kind = OperationSettingKind.Integer;
            }
            else if (plain == typeof(double) || plain == typeof(float) || plain == typeof(decimal))
            {
                kind = OperationSettingKind.Number;
            }
            else if (plain == typeof(string))
            {
                kind = OperationSettingKind.Text;
            }
            else
            {
                kind = OperationSettingKind.Other;
            }

            var summary = operation.Description.Settings.FirstOrDefault(setting => string.Equals(setting.Name, property.Name, StringComparison.Ordinal))?.Summary ?? string.Empty;
            string? fallback = null;
            if (defaults is not null && defaults.TryGetPropertyValue(property.Name, out var node) && node is not null)
            {
                fallback = node.ToJsonString();
            }

            fields.Add(new OperationSettingField(property.Name, kind, isNullable, choices, fallback, summary));
        }

        return fields.ToImmutable();
    }

    /// <summary>
    /// The JSON value a text typed into a control stands for, or why not in words. An empty text is the setting left out (null) where the
    /// setting can be left out, and a refusal where it cannot. Numbers are read the same on every computer (no thousands, a point).
    /// </summary>
    public static bool TryParse(OperationSettingField field, string text, out JsonNode? value, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(field);
        value = null;
        refusal = null;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0 && field.Nullable)
        {
            return true;
        }

        switch (field.Kind)
        {
            case OperationSettingKind.Bool when bool.TryParse(trimmed, out var flag):
                value = JsonValue.Create(flag);
                return true;
            case OperationSettingKind.Integer when long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole):
                value = JsonValue.Create(whole);
                return true;
            case OperationSettingKind.Number when double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number):
                value = JsonValue.Create(number);
                return true;
            case OperationSettingKind.Choice when field.Choices.Contains(trimmed, StringComparer.Ordinal):
                value = JsonValue.Create(trimmed);
                return true;
            case OperationSettingKind.Text:
                value = JsonValue.Create(trimmed);
                return true;
        }

        refusal = field.Kind switch
        {
            OperationSettingKind.Integer => $"{field.Name} takes a whole number, such as 3",
            OperationSettingKind.Number => $"{field.Name} takes a number, such as 0.5",
            OperationSettingKind.Choice => $"{field.Name} is one of {string.Join(", ", field.Choices)}",
            OperationSettingKind.Bool => $"{field.Name} is on or off",
            OperationSettingKind.Other => $"{field.Name} is edited in the project file or on the command line",
            _ => $"{field.Name} cannot be read from '{trimmed}'",
        };
        return false;
    }
}
