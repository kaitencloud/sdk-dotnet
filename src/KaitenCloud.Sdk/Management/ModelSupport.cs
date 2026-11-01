// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using System.Text.Json.Serialization;
namespace KaitenCloud.Sdk.Management.Models;

internal interface IManagementModel { void Validate(); }
internal interface IOptional { bool IsSpecified { get; } object? UntypedValue { get; } }

/// <summary>An omitted value differs from an explicitly supplied null, false or zero.</summary>
[JsonConverter(typeof(OptionalJsonConverterFactory))]
public readonly struct Optional<T> : IOptional
{
    public bool IsSpecified { get; }
    public T? Value { get; }
    object? IOptional.UntypedValue => Value;
    public Optional(T? value) { IsSpecified = true; Value = value; }
    public static implicit operator Optional<T>(T? value) => new(value);
}
internal sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    public OptionalJsonConverterFactory() { }
    public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Optional<>);
    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(type.GetGenericArguments()), nonPublic: true)!;
    private sealed class Converter<T> : JsonConverter<Optional<T>>
    {
        public override bool HandleNull => true;
        public override Optional<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => new(JsonSerializer.Deserialize<T>(ref reader, options));
        public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
        {
            if (!value.IsSpecified) throw new JsonException("Unspecified optional value.");
            JsonSerializer.Serialize(writer, value.Value, options);
        }
    }
}

public sealed record ManagementPage<T> : IJsonOnDeserialized
{
    public required IReadOnlyList<T>? Items { get; init; }
    public required bool HasMore { get; init; }
    public string? NextCursor { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
    void IJsonOnDeserialized.OnDeserialized()
    {
        // The contract permits null items for an empty collection, never an incomplete page.
        if (HasMore && (Items is null || string.IsNullOrEmpty(NextCursor)) || Items?.Any(item => item is null) == true)
            throw new JsonException("Invalid management page.");
    }
}
