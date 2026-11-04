// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace KaitenCloud.ContractTool;

internal static class Json
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty JSON document.");
    internal static string Text(this JsonNode? node) => node?.GetValue<string>() ?? "";
    internal static bool HasScalarNullType(this JsonNode schema) => schema["type"] is JsonValue value && value.TryGetValue<string>(out var type) && type == "null";
    internal static bool Flag(this JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    internal static IEnumerable<JsonNode> Items(this JsonNode? node) => node is JsonArray array ? array.Select(item => item ?? throw new InvalidDataException("Unexpected null schema.")) : [];
    internal static IEnumerable<KeyValuePair<string, JsonNode?>> Fields(this JsonNode? node) => node is JsonObject obj ? obj : [];
    internal static bool Has(this JsonNode? node, string key) => node is JsonObject obj && obj.ContainsKey(key);
    internal static bool ContainsText(this JsonNode? node, string text) => node is JsonArray ? node.Items().Any(item => item.Text() == text) : node.Text() == text;
    internal static string Quote(string text) => JsonSerializer.Serialize(text, Options);
    internal static string Render(JsonNode node) => node.ToJsonString(Options) + "\n";
}
