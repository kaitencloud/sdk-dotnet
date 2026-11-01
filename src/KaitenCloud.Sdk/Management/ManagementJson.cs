// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using KaitenCloud.Sdk.Management.Models;
namespace KaitenCloud.Sdk.Management;

internal sealed record ManagementOperation(string Id, string Method, int Status, bool HasBody, string Path, string Authentication, bool NullCollection, bool NonNullItems);
internal static class ManagementJson
{
    internal static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var options = KaitenJson.CreateOptions();
        options.Converters.Add(new UtcInstantConverter());
        return options;
    }
    internal static byte[] SerializeRequest(object request)
    {
        try
        {
            if (request is IManagementModel model) model.Validate();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, request.GetType(), Options);
            // STJ callbacks also validate nested typed inputs; no generic schema interpreter.
            _ = JsonSerializer.Deserialize(bytes, request.GetType(), Options);
            return bytes;
        }
        catch (JsonException) { throw new ArgumentException("Invalid Kaiten management request.", nameof(request)); }
        catch (NotSupportedException) { throw new ArgumentException("Unsupported Kaiten management request.", nameof(request)); }
    }
    internal static T Deserialize<T>(byte[] bytes, bool nullCollection = false, bool nonNullItems = false)
    {
        try
        {
            var result = JsonSerializer.Deserialize<T>(bytes, Options);
            if (result is null && nullCollection) return JsonSerializer.Deserialize<T>("[]"u8, Options)!;
            if (nonNullItems && result is IEnumerable sequence && sequence.Cast<object?>().Any(item => item is null))
                throw new JsonException("Invalid typed collection member.");
            return result ?? throw new KaitenProtocolException();
        }
        catch (JsonException) { throw new KaitenProtocolException(); }
        catch (NotSupportedException) { throw new KaitenProtocolException(); }
    }
    private sealed class UtcInstantConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || !reader.TryGetDateTimeOffset(out var value)) throw new JsonException("Invalid timestamp.");
            return value;
        }
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value.UtcDateTime);
    }
}
