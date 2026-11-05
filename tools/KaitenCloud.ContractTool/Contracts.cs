// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Security.Cryptography;
using System.Text.Json.Nodes;
namespace KaitenCloud.ContractTool;

internal static class Contracts
{
    internal static readonly HashSet<string> Methods = ["get", "post", "put", "patch", "delete", "head", "options", "trace"];
    internal static bool ForbiddenPath(string path) => path == "/ofrep/v1" || path.StartsWith("/ofrep/v1/", StringComparison.Ordinal);
    internal static Dictionary<string, JsonNode> Load(string root)
    {
        var source = Json.Read(Path.Combine(root, "contracts/source.json"));
        var documents = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var (name, pin) in source["documents"].Fields())
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, pin!["path"].Text()));
            if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != pin["sha256"].Text())
                throw new InvalidDataException($"{name} contract checksum changed; explicitly re-pin the approved source");
            var document = JsonNode.Parse(bytes) ?? throw new InvalidDataException("Empty contract.");
            if (document["openapi"].Text() != "3.1.0") throw new InvalidDataException("Only pinned OpenAPI 3.1 is supported");
            var operations = document["paths"].Fields().SelectMany(path => path.Value.Fields().Where(method => Methods.Contains(method.Key)).Select(_ => path.Key)).ToArray();
            if (operations.Length != pin["restOperations"]!.GetValue<int>() || operations.Count(path => !ForbiddenPath(path)) != pin["permittedOperations"]!.GetValue<int>())
                throw new InvalidDataException($"{name} operation count differs from source pin");
            if (document["webhooks"].Fields().Count() != pin["webhooks"]!.GetValue<int>()) throw new InvalidDataException($"{name} webhook declarations changed");
            documents.Add(name, document);
        }
        return documents;
    }

    internal static void Check(string root)
    {
        var documents = Load(root);
        var expected = documents.SelectMany(doc => doc.Value["paths"].Fields().SelectMany(path => path.Value.Fields()
            .Where(method => Methods.Contains(method.Key)).Select(method => (Document: doc.Key, Path: path.Key, Method: method.Key.ToUpperInvariant(), Operation: method.Value!["operationId"].Text()))))
            .OrderBy(row => row.Document, StringComparer.Ordinal).ThenBy(row => row.Path, StringComparer.Ordinal).ThenBy(row => row.Method, StringComparer.Ordinal).ToArray();
        var coverage = Json.Read(Path.Combine(root, "contracts/coverage.json"))["operations"].Items().ToArray();
        var actual = coverage.Select(row => (Document: row["document"].Text(), Path: row["path"].Text(), Method: row["method"].Text(), Operation: row["operationId"].Text())).ToArray();
        if (!actual.SequenceEqual(expected)) throw new InvalidDataException("Every REST operation must have exactly one coverage entry");
        var wired = Json.Read(Path.Combine(root, "contracts/batch1-api.json")).Items().Select(row => row["operationId"].Text()).ToHashSet(StringComparer.Ordinal);
        foreach (var row in coverage)
        {
            var status = row["status"].Text();
            if ((status == "management-facade") != (row["document"].Text() == "core" && wired.Contains(row["operationId"].Text())))
                throw new InvalidDataException("Facade coverage must match actual generated resource bindings");
            if (ForbiddenPath(row["path"].Text()))
            {
                if (status != "excluded-official-provider") throw new InvalidDataException("OFREP must never be a Kaiten client operation");
            }
            else if (status is not ("existing-client" or "management-facade" or "pending-parity")) throw new InvalidDataException("Unknown coverage status");
            if (string.IsNullOrEmpty(row["reason"].Text())) throw new InvalidDataException("Coverage requires an explicit implementation/exclusion reason");
        }
    }
}
