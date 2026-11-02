// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KaitenCloud.Sdk;

public static class KaitenJson
{
    public static JsonSerializerOptions CreateOptions() => Create();
    internal static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 64
        };
        options.Converters.Add(new LicenseTermStatusConverter());
        options.Converters.Add(new JsonStringEnumConverter<UsageReportBehavior>(JsonNamingPolicy.CamelCase, false));
        options.Converters.Add(new EntitlementValueConverter());
        options.Converters.Add(new FlagTargetingConverter());
        options.Converters.Add(new DefaultFlagVariantConverter());
        return options;
    }

    internal static T Deserialize<T>(ReadOnlySpan<byte> bytes)
    {
        try { return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new KaitenProtocolException(); }
        catch (JsonException) { throw new KaitenProtocolException(); }
        catch (NotSupportedException) { throw new KaitenProtocolException(); }
    }

    internal static string Discriminator(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            throw new JsonException("Missing type discriminator.");
        return type.GetString()!;
    }
    internal static void WriteUnion(Utf8JsonWriter writer, object value, string type, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", type);
        foreach (var property in JsonSerializer.SerializeToElement(value, value.GetType(), options).EnumerateObject())
            property.WriteTo(writer);
        writer.WriteEndObject();
    }
}

// This property is a database instant, not a local/unspecified calendar value.
// Do not register globally: unrelated date fields retain their wire behavior.
internal sealed class LicenseTermInstantConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Invalid license term database instant.");
        var text = reader.GetString()!;
        var explicitZone = text.EndsWith('Z') || text.Length >= 25 && text[^6] is '+' or '-' && text[^3] == ':';
        if (text.Length < 20 || text[10] != 'T' || !explicitZone || !reader.TryGetDateTimeOffset(out var instant))
            throw new JsonException("Invalid license term database instant.");
        return instant;
    }
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

// Assignment response boundaries must be complete instants before an update can echo them.
// Property-scoped, separate from term classification and request UTC serialization.
internal sealed class InstanceAssignmentInstantConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Invalid instance assignment database instant.");
        var text = reader.GetString()!;
        var explicitZone = text.EndsWith('Z') || text.Length >= 25 && text[^6] is '+' or '-' && text[^3] == ':';
        if (text.Length < 20 || text[10] != 'T' || !explicitZone || !reader.TryGetDateTimeOffset(out var instant))
            throw new JsonException("Invalid instance assignment database instant.");
        return instant;
    }
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

internal sealed class LicenseTermStatusConverter : JsonConverter<LicenseTermStatus>
{
    public override LicenseTermStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("License status must be an exact wire string.");
        return reader.GetString() switch
        {
            "ACTIVE" => LicenseTermStatus.Active,
            "NOT_STARTED" => LicenseTermStatus.NotStarted,
            "EXPIRED" => LicenseTermStatus.Expired,
            _ => throw new JsonException("Unsupported license status.")
        };
    }

    public override void Write(Utf8JsonWriter writer, LicenseTermStatus value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            LicenseTermStatus.Active => "ACTIVE",
            LicenseTermStatus.NotStarted => "NOT_STARTED",
            LicenseTermStatus.Expired => "EXPIRED",
            _ => throw new JsonException("Unsupported license status.")
        });
}

internal sealed class EntitlementValueConverter : JsonConverter<EntitlementValue>
{
    public override EntitlementValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        EntitlementValue value = KaitenJson.Discriminator(element) switch
        {
            "number" => element.Deserialize<NumberEntitlementValue>(options)!,
            "boolean" => element.Deserialize<BooleanEntitlementValue>(options)!,
            "object" => element.Deserialize<ObjectEntitlementValue>(options)!,
            _ => throw new JsonException("Unsupported entitlement type.")
        };
        if (value is ObjectEntitlementValue { Value: null }) throw new JsonException("Missing object value.");
        value.Extensions?.Remove("type");
        return value;
    }
    public override void Write(Utf8JsonWriter writer, EntitlementValue value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case NumberEntitlementValue number:
                writer.WriteString("type", "number"); writer.WriteNumber("value", number.Value); break;
            case BooleanEntitlementValue boolean:
                writer.WriteString("type", "boolean"); writer.WriteBoolean("value", boolean.Value); break;
            case ObjectEntitlementValue config:
                writer.WriteString("type", "object"); writer.WritePropertyName("value");
                JsonSerializer.Serialize(writer, config.Value, options); break;
            default: throw new JsonException("Unsupported entitlement type.");
        }
        // event_count and additive response fields are never written into strict request bodies.
        writer.WriteEndObject();
    }
}
internal sealed class FlagTargetingConverter : JsonConverter<FlagTargeting>
{
    public override FlagTargeting Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var e = doc.RootElement;
        return KaitenJson.Discriminator(e) switch
        {
            "basic" => e.Deserialize<BasicFlagTargeting>(options)!,
            "rollout_date" => e.Deserialize<DateFlagTargeting>(options)!,
            "rollout_percentage" => e.Deserialize<PercentageFlagTargeting>(options)!,
            _ => throw new JsonException("Unsupported targeting type.")
        };
    }
    public override void Write(Utf8JsonWriter writer, FlagTargeting value, JsonSerializerOptions options)
        => KaitenJson.WriteUnion(writer, value, value switch
        {
            BasicFlagTargeting => "basic",
            DateFlagTargeting => "rollout_date",
            PercentageFlagTargeting => "rollout_percentage",
            _ => throw new JsonException()
        }, options);
}
internal sealed class DefaultFlagVariantConverter : JsonConverter<DefaultFlagVariant>
{
    public override DefaultFlagVariant Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var e = doc.RootElement;
        return KaitenJson.Discriminator(e) switch
        {
            "basic" => e.Deserialize<BasicDefaultFlagVariant>(options)!,
            "rollout_date" => e.Deserialize<DateDefaultFlagVariant>(options)!,
            "rollout_percentage" => e.Deserialize<PercentageDefaultFlagVariant>(options)!,
            _ => throw new JsonException("Unsupported default variant type.")
        };
    }
    public override void Write(Utf8JsonWriter writer, DefaultFlagVariant value, JsonSerializerOptions options)
        => KaitenJson.WriteUnion(writer, value, value switch
        {
            BasicDefaultFlagVariant => "basic",
            DateDefaultFlagVariant => "rollout_date",
            PercentageDefaultFlagVariant => "rollout_percentage",
            _ => throw new JsonException()
        }, options);
}
