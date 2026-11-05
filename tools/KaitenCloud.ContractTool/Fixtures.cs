// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Text.Json.Nodes;
namespace KaitenCloud.ContractTool;

internal sealed class Fixtures(JsonNode document)
{
    private readonly JsonNode schemas = document["components"]!["schemas"]!;
    internal JsonNode? Sample(JsonNode schema, bool full, bool request, string[]? trail = null)
    {
        trail ??= [];
        if (schema.Has("$ref"))
        {
            var name = schema["$ref"].Text().Split('/')[^1];
            if (trail.Contains(name)) return null;
            if (name == "LicenseTerm") return new JsonObject { ["status"] = "ACTIVE", ["evaluatedAt"] = "2026-11-01T00:00:00Z" };
            return Sample(schemas[name]!, full, request, [.. trail, name]);
        }
        var choices = schema["oneOf"] ?? schema["anyOf"];
        if (choices is JsonArray) return Sample(choices.Items().First(item => !item.HasScalarNullType()), full, request, trail);
        var type = schema["type"];
        var nullable = type is JsonArray && type.ContainsText("null");
        var kind = type is JsonArray ? type.Items().First(item => item.Text() != "null").Text() : type.Text();
        if (kind == "object")
        {
            if (!schema.Has("properties")) return new JsonObject { ["sample"] = Sample(schema["additionalProperties"] as JsonObject ?? new JsonObject(), full, request, trail) };
            var result = new JsonObject();
            foreach (var (key, value) in schema["properties"].Fields())
            {
                if (key == "licenseTerm" && trail.LastOrDefault() == "Instance") continue;
                if (request && value!["readOnly"].Flag() || !request && value!["writeOnly"].Flag()) continue;
                if (!full && !schema["required"].ContainsText(key)) continue;
                if (value!.Has("$ref") && trail.Contains(value!["$ref"].Text().Split('/')[^1])) continue;
                result[key] = Sample(value!, full, request, trail);
            }
            if (schema["properties"].Has("items") && schema["properties"].Has("hasMore")) { result["hasMore"] = false; result.Remove("nextCursor"); }
            if (result.ContainsKey("entitlementSlug")) result["entitlementSlug"] = "entitlement-slug";
            return result;
        }
        if (kind == "array")
        {
            var item = schema["items"]!;
            return item.Has("$ref") && trail.Contains(item["$ref"].Text().Split('/')[^1]) ? new JsonArray() : new JsonArray(Sample(item, full, request, trail));
        }
        if (nullable) return null;
        if (schema["enum"] is JsonArray values) return values.First(value => value is not null)!.DeepClone();
        if (kind == "boolean") return JsonValue.Create(false);
        if (kind is "integer" or "number") return JsonValue.Create(Math.Max(schema["minimum"]?.GetValue<double>() ?? 1, 1));
        if (kind == "string") return schema["format"].Text() switch
        {
            "date-time" => JsonValue.Create("2026-10-01T05:30:00+05:30"),
            "uuid" => JsonValue.Create("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            _ => schema["examples"] is JsonArray examples ? examples[0]!.DeepClone() : JsonValue.Create("example")
        };
        if (kind == "") return trail.LastOrDefault() == "Variant" ? JsonValue.Create(true) : new JsonObject { ["example"] = 42 };
        throw new InvalidDataException($"Unsupported fixture schema: {schema}");
    }

    internal JsonNode? QueryValues(JsonNode operation, bool full)
    {
        var result = new JsonObject();
        foreach (var parameter in operation["parameters"].Items().Where(p => p["in"].Text() == "query" && (full || p["required"].Flag())))
            result[parameter["name"].Text()] = Sample(parameter["schema"]!, true, true);
        return result.Count == 0 ? null : result;
    }

    internal JsonArray Render(JsonArray api)
    {
        var rows = new JsonArray();
        foreach (var row in api.Items())
        {
            var operation = document["paths"]![row["path"].Text()]![row["method"].Text().ToLowerInvariant()]!;
            foreach (var full in new[] { false, true })
            {
                var bodySchema = operation["requestBody"]?["content"]?["application/json"]?["schema"];
                var response = operation["responses"]![row["status"]!.ToString()]?["content"]?["application/json"]?["schema"];
                var pathParams = new JsonObject();
                foreach (var parameter in row["parameters"].Items())
                {
                    var name = parameter["name"].Text();
                    pathParams[name] = name switch { "externalId" => "external:42", "adapter" or "integrationName" => "sample.adapter", "instanceSlug" => "instance-slug", "entitlementSlug" => "entitlement-slug", _ => name.Replace("Slug", "-slug", StringComparison.Ordinal).ToLowerInvariant() };
                }
                var body = bodySchema is null ? null : Sample(bodySchema, full, true);
                var payload = response is null ? null : Sample(response, full, false);
                if (row["operationId"].Text() == "getInstance")
                {
                    payload!["slug"] = pathParams["instanceSlug"]!.DeepClone();
                    payload["licenseTerm"] = Sample(new JsonObject { ["$ref"] = "#/components/schemas/LicenseTerm" }, full, false);
                }
                if (payload is JsonObject obj) obj["futureProperty"] = new JsonObject { ["retained"] = true };
                rows.Add(new JsonObject { ["operationId"] = row["operationId"]!.DeepClone(), ["full"] = full, ["pathArguments"] = pathParams, ["request"] = body, ["query"] = QueryValues(operation, full), ["response"] = payload });
            }
        }
        return rows;
    }
}
