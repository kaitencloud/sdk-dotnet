// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Text.Json.Nodes;
namespace KaitenCloud.ContractTool;

internal static class Generation
{
    internal static void Run(string root, bool check)
    {
        var document = Contracts.Load(root)["core"];
        var config = Json.Read(Path.Combine(root, "contracts/batch1.json"));
        var first = new ManagementGenerator(document, config).Render();
        var second = new ManagementGenerator(document, config).Render();
        if (first.Files.Count != second.Files.Count || first.Files.Any(pair => !second.Files.TryGetValue(pair.Key, out var value) || value != pair.Value) || !JsonNode.DeepEquals(first.Api, second.Api))
            throw new InvalidDataException("Generation is not deterministic");
        var target = Path.Combine(root, "src/KaitenCloud.Sdk/Management/Generated");
        var existing = Directory.Exists(target) ? Directory.GetFiles(target, "*.cs", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(target, path).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal) : [];
        if (check)
        {
            var drift = first.Files.Keys.Union(existing.Keys).Where(key => !first.Files.TryGetValue(key, out var expected) || !existing.TryGetValue(key, out var actual) || actual != expected).ToArray();
            if (drift.Length > 0) throw new InvalidDataException("Generated source drift; regenerate and review: " + string.Join(", ", drift));
            if (!JsonNode.DeepEquals(Json.Read(Path.Combine(root, "contracts/batch1-api.json")), first.Api)) throw new InvalidDataException("Operation metadata drift");
        }
        else
        {
            // Do not silently remove unexpected source files owned by the caller.
            if (existing.Keys.Except(first.Files.Keys).Any()) throw new InvalidDataException("Unexpected generated files; review stale files before regeneration");
            foreach (var (path, content) in first.Files)
            {
                var destination = Path.Combine(target, path); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.WriteAllText(destination, content);
            }
            File.WriteAllText(Path.Combine(root, "contracts/batch1-api.json"), Json.Render(first.Api));
        }
    }
}
