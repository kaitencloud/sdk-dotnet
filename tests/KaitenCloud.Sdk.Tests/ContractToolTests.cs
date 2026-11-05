// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using KaitenCloud.ContractTool;
using Xunit;

namespace KaitenCloud.Sdk.Tests;

public sealed class ContractToolTests
{
    private static readonly string Root = FindRoot();
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "contracts/source.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("SDK contract root missing.");
    }
    private static ManagementGenerator Generator(JsonNode? document = null) => new(document ?? Contracts.Load(Root)["core"], Json.Read(Path.Combine(Root, "contracts/batch1.json")));

    [Fact]
    public void NativeGenerationAndFixturesMatchEveryCheckedInByteAndManifest()
    {
        Contracts.Check(Root);
        Generation.Run(Root, check: true);
        var first = Generator().Render(); var second = Generator().Render();
        Assert.Equal(64, first.Api.Count);
        Assert.Equal(first.Files.OrderBy(pair => pair.Key), second.Files.OrderBy(pair => pair.Key));
        Assert.True(JsonNode.DeepEquals(first.Api, second.Api));
        Assert.DoesNotContain(first.Api.Items(), row => Contracts.ForbiddenPath(row["path"].Text()));
        Assert.DoesNotContain(first.Files.Values, text => text.Contains("EvaluateFlag", StringComparison.Ordinal));
        var fixtures = new Fixtures(Contracts.Load(Root)["core"]).Render(first.Api);
        Assert.Equal(128, fixtures.Count);
        Assert.Equal(File.ReadAllText(Path.Combine(Root, "tests/KaitenCloud.Sdk.Tests/Fixtures/management-wire.json")), Json.Render(fixtures));
        Assert.Equal(117, Contracts.Load(Root)["core"]["webhooks"].Fields().Count());
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("status")]
    [InlineData("facade")]
    [InlineData("ofrep")]
    [InlineData("reason")]
    public void SnapshotAndCoverageCorruptionFailsClosed(string corruption)
    {
        using var fixture = new ContractFixture();
        var coverage = Json.Read(Path.Combine(fixture.Root, "contracts/coverage.json"));
        var rows = (JsonArray)coverage["operations"]!;
        Assert.Equal(172, rows.Count(row => row!["status"].Text() == "pending-parity"));
        Assert.Equal(64, rows.Count(row => row!["status"].Text() == "management-facade"));
        Assert.Single(rows, row => row!["status"].Text() == "existing-client");
        switch (corruption)
        {
            case "checksum": File.AppendAllText(Path.Combine(fixture.Root, "contracts/core.openapi.json"), " "); break;
            case "duplicate": rows.Add(rows[0]!.DeepClone()); break;
            case "missing": rows.RemoveAt(0); break;
            case "status": rows[0]!["status"] = "generated-means-supported"; break;
            case "facade": rows.First(row => row!["status"].Text() == "management-facade")!["status"] = "pending-parity"; break;
            case "ofrep": rows.First(row => Contracts.ForbiddenPath(row!["path"].Text()))!["status"] = "existing-client"; break;
            case "reason": rows[0]!["reason"] = ""; break;
        }
        File.WriteAllText(Path.Combine(fixture.Root, "contracts/coverage.json"), Json.Render(coverage));
        Assert.Throws<InvalidDataException>(() => Contracts.Check(fixture.Root));
    }
    [Theory]
    [InlineData("/ofrep/v1", true)]
    [InlineData("/ofrep/v1/evaluate/flags/future", true)]
    [InlineData("/feature-flags/tracker", false)]
    [InlineData("/openfeature/v0/manifest", false)]
    public void EvaluationSubtreeIsExcludedButFlagManagementRemains(string path, bool forbidden) => Assert.Equal(forbidden, Contracts.ForbiddenPath(path));

    [Fact]
    public void LicenseTermsAndAssignmentInstantConvertersStayDetailAndResponseOnly()
    {
        var result = Generator().Render();
        Assert.DoesNotContain(result.Api.Items(), row => row["operationId"].Text() == "getInstanceLicenseValidation");
        Assert.Contains("LicenseTerm?", result.Files["Models/Instance.g.cs"]);
        Assert.DoesNotContain("LicenseTerm", result.Files["Models/CreateInstanceRequest.g.cs"]);
        Assert.DoesNotContain("LicenseTerm", result.Files["Models/UpdateInstanceRequest.g.cs"]);
        Assert.Contains("result.LicenseTerm is null", result.Files["Instances.g.cs"]);
        const string attribute = "[JsonConverter(typeof(global::KaitenCloud.Sdk.InstanceAssignmentInstantConverter))]";
        foreach (var (path, text) in result.Files)
            Assert.Equal(path is "Models/Instance.g.cs" or "Models/InstanceIntegrationResource.g.cs" ? 2 : 0, text.Split(attribute).Length - 1);
        var fixtures = new Fixtures(Contracts.Load(Root)["core"]).Render(result.Api);
        foreach (var row in fixtures.Items())
        {
            Assert.Equal(row["operationId"].Text() == "getInstance", ContainsTerm(row["response"]));
            Assert.False(ContainsTerm(row["request"]));
        }
    }
    private static bool ContainsTerm(JsonNode? node) => node is JsonObject obj ? obj.ContainsKey("licenseTerm") || obj.Any(pair => ContainsTerm(pair.Value)) : node is JsonArray array && array.Any(ContainsTerm);

    [Theory]
    [InlineData("oneOf")]
    [InlineData("anyOf")]
    public void NullableAssignmentInstantsKeepResponseConverterAndRequiredPresence(string keyword)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["startLicenseDate"] = new JsonObject { [keyword] = new JsonArray(JsonNode.Parse("""{"type":"string","format":"date-time"}"""), new JsonObject { ["type"] = "null" }) } },
            ["required"] = new JsonArray("startLicenseDate")
        };
        var generator = Generator();
        var response = generator.Model("Instance", schema, false);
        Assert.Contains("public required DateTimeOffset? StartLicenseDate", response);
        Assert.Contains("InstanceAssignmentInstantConverter", response);
        Assert.Contains("Condition = JsonIgnoreCondition.Never", response);
        Assert.DoesNotContain("InstanceAssignmentInstantConverter", generator.Model("Instance", schema, true));
        Assert.Equal(("DateTimeOffset", false), generator.TypeName(JsonNode.Parse("""{"type":"string","format":"date-time"}""")!, "OrdinaryDate"));
    }

    [Theory]
    [InlineData("oneOf", false)]
    [InlineData("oneOf", true)]
    [InlineData("anyOf", false)]
    [InlineData("anyOf", true)]
    public void CombinedAlternativesAndNullableTypeArraysPreserveGeneratorAndFixtureSemantics(string keyword, bool scalarNullBranch)
    {
        var branches = new JsonArray();
        if (scalarNullBranch) branches.Add(new JsonObject { ["type"] = "null" });
        branches.Add(new JsonObject { ["type"] = new JsonArray("string", "null") });
        var schema = new JsonObject { [keyword] = branches };
        var generator = Generator();
        Assert.Equal(("string", true), generator.TypeName(schema, "CombinedValue"));
        Assert.Equal(scalarNullBranch, ManagementGenerator.Nullable(schema));
        var model = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["value"] = schema.DeepClone() },
            ["required"] = new JsonArray("value")
        };
        foreach (var request in new[] { false, true })
        {
            var text = generator.Model("CombinedModel", model, request);
            Assert.Contains("public required string? Value", text);
            Assert.Contains("Condition = JsonIgnoreCondition.Never", text);
            foreach (var full in new[] { false, true })
            {
                var fixtures = new Fixtures(Contracts.Load(Root)["core"]);
                Assert.Null(fixtures.Sample(schema, full, request));
                var sample = (JsonObject)fixtures.Sample(model, full, request)!;
                Assert.True(sample.ContainsKey("value"));
                Assert.Null(sample["value"]);
            }
        }
        Assert.False(generator.NonnullableRootItems(new JsonObject { ["type"] = "array", ["items"] = schema.DeepClone() }, "CombinedItems"));
    }

    [Theory]
    [InlineData("oneOf")]
    [InlineData("anyOf")]
    public void CombinedAlternativesDoNotPermitUnsupportedTypeUnions(string keyword)
    {
        var unsupported = new JsonObject { [keyword] = new JsonArray(new JsonObject { ["type"] = new JsonArray("string", "number", "null") }) };
        Assert.Throws<InvalidDataException>(() => Generator().TypeName(unsupported, "CompoundUnion"));
        var multiple = new JsonObject
        {
            [keyword] = new JsonArray(new JsonObject { ["type"] = new JsonArray("string", "null") }, new JsonObject { ["type"] = "number" })
        };
        Assert.Throws<InvalidDataException>(() => Generator().TypeName(multiple, "MultipleAlternatives"));
        Assert.Throws<InvalidOperationException>(() => Generator().TypeName(new JsonObject { [keyword] = new JsonArray(new JsonObject { ["type"] = 42 }) }, "MalformedType"));
        Assert.Throws<InvalidOperationException>(() => new Fixtures(Contracts.Load(Root)["core"]).Sample(new JsonObject { [keyword] = new JsonArray(new JsonObject { ["type"] = 42 }) }, false, false));
    }

    [Theory]
    [InlineData("""{"type":["string","number"]}""")]
    [InlineData("""{"oneOf":[{"type":"string"},{"type":"number"}]}""")]
    [InlineData("""{"type":"object","additionalProperties":false}""")]
    [InlineData("""{"oneOf":[{"type":"string"},{"type":"number"}],"discriminator":{"propertyName":"type","mapping":{"number":"wrong","boolean":"wrong","object":"wrong"}}}""")]
    public void UnknownUnionsClosedObjectsAndChangedTaggedMappingsFail(string json) => Assert.Throws<InvalidDataException>(() => Generator().TypeName(JsonNode.Parse(json)!, "Unknown"));

    [Theory]
    [InlineData("oneOf")]
    [InlineData("anyOf")]
    [InlineData("allOf")]
    public void UnsupportedRootOrReferencedCompositionCannotWritePartialOutput(string keyword)
    {
        using var fixture = new ContractFixture();
        var document = Contracts.Load(fixture.Root)["core"];
        var schema = new JsonObject { [keyword] = new JsonArray(JsonNode.Parse("""{"type":"object","properties":{"requiredValue":{"type":"string"}},"required":["requiredValue"]}"""), JsonNode.Parse("""{"type":"object","properties":{"other":{"type":"string"}}}""")) };
        document["paths"]!["/customers"]!["post"]!["requestBody"]!["content"]!["application/json"]!["schema"] = schema;
        fixture.RepinCore(document); // Explicit mutation fixture, never a real source re-pin.
        Assert.Throws<InvalidDataException>(() => Generation.Run(fixture.Root, false));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "src")));
        var referenced = Contracts.Load(Root)["core"];
        referenced["components"]!["schemas"]!["CustomerIntegration"]![keyword] = schema[keyword]!.DeepClone();
        Assert.Throws<InvalidDataException>(() => Generator(referenced).Render());
    }

    [Fact]
    public void RequiredNullableOptionalReadonlyAndTypedMapGuardsKeepTheirContracts()
    {
        var files = Generator().Render().Files;
        Assert.Contains("public required string? Description", files["Models/UpdateFeatureFlagRequest.g.cs"]);
        Assert.Contains("public required IReadOnlyDictionary<string, JsonElement?>? Metadata", files["Models/UpdateFeatureFlagRequest.g.cs"]);
        Assert.Contains("Condition = JsonIgnoreCondition.Never", files["Models/UpdateFeatureFlagRequest.g.cs"]);
        Assert.DoesNotContain("public required string Id", files["Models/CreateCustomerRequest.g.cs"]);
        Assert.Contains("Optional<bool?> IsPublic", files["Models/UpdateLicenseFamilyRequest.g.cs"]);
        Assert.Contains("Integrations.Value.Values.Any(item => item is null)", files["Models/CreateCustomerRequest.g.cs"]);
        Assert.DoesNotContain("Metadata.Value.Values.Any(item => item is null)", files["Models/CustomerIntegrationInput.g.cs"]);
        Assert.DoesNotContain("Metadata.Values.Any(item => item is null)", files["Models/UpdateFeatureFlagRequest.g.cs"]);
    }

    [Fact]
    public void RequiredQueryMutationChangesModelSignatureMetadataAndMinimalFixture()
    {
        var document = Contracts.Load(Root)["core"];
        var operation = document["paths"]!["/entitlement-groups/{entitlementGroupSlug}/usage"]!["get"]!;
        var original = Generator(document).Render();
        Assert.Contains("public required string Instance", original.Files["Models/GetUsageEntitlementGroupQuery.g.cs"]);
        Assert.DoesNotContain("GetUsageEntitlementGroupQuery? query = null", original.Files["EntitlementGroups.g.cs"]);
        Assert.True(original.Api.Items().First(row => row["operationId"].Text() == "get-entitlement-group-usage")["queryParameters"]![0]!["required"].Flag());
        Assert.True(new Fixtures(document).QueryValues(operation, false).Has("instance"));
        operation["parameters"].Items().First(parameter => parameter["in"].Text() == "query")["required"] = false;
        var changed = Generator(document).Render();
        Assert.Contains("Optional<string> Instance", changed.Files["Models/GetUsageEntitlementGroupQuery.g.cs"]);
        Assert.Contains("GetUsageEntitlementGroupQuery? query = null", changed.Files["EntitlementGroups.g.cs"]);
        Assert.False(changed.Api.Items().First(row => row["operationId"].Text() == "get-entitlement-group-usage")["queryParameters"]![0]!["required"].Flag());
        Assert.Null(new Fixtures(document).QueryValues(operation, false));
    }

    [Theory]
    [InlineData("oneOf", false)]
    [InlineData("oneOf", true)]
    [InlineData("anyOf", false)]
    [InlineData("anyOf", true)]
    public void AlternativeNullabilityDrivesTypedMapMembersRootItemsAndOperationPolicy(string keyword, bool nullable)
    {
        var document = Contracts.Load(Root)["core"];
        JsonObject Alternative(string type)
        {
            var variants = new JsonArray(new JsonObject { ["$ref"] = "#/components/schemas/" + type });
            if (nullable) variants.Add(new JsonObject { ["type"] = "null" });
            return new JsonObject { [keyword] = variants };
        }
        document["components"]!["schemas"]!["Customer"]!["properties"]!["integrations"]!["additionalProperties"] = Alternative("CustomerIntegration");
        var usage = document["paths"]!["/instances/{instanceSlug}/entitlements/usage"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!;
        usage["items"] = Alternative("EntitlementUsage");
        var generator = Generator(document); var result = generator.Render();
        Assert.Equal(!nullable, result.Files["Models/CreateCustomerRequest.g.cs"].Contains("Values.Any(item => item is null)", StringComparison.Ordinal));
        Assert.Equal(nullable, result.Files["Models/CreateCustomerRequest.g.cs"].Contains("CustomerIntegrationInput?", StringComparison.Ordinal));
        Assert.Equal(nullable, result.Api.Items().First(row => row["operationId"].Text() == "getEntitlementsUsageMetrics")["responseType"].Text().Contains("EntitlementUsage?", StringComparison.Ordinal));
        Assert.Equal(!nullable, generator.NonnullableRootItems(usage, "Usage"));
        var policy = result.Files["Operations.g.cs"].Split('\n').Single(line => line.Contains("getEntitlementsUsageMetrics", StringComparison.Ordinal));
        Assert.EndsWith(nullable ? ", false);" : ", true);", policy);
        Assert.True(ManagementGenerator.Nullable(JsonNode.Parse("""{"type":["object","null"]}""")!));
        Assert.False(ManagementGenerator.Nullable(JsonNode.Parse("""{"type":"object"}""")!));
        Assert.False(ManagementGenerator.Nullable(JsonNode.Parse("""{"allOf":[{"type":"null"}]}""")!));
        var model = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["entries"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = Alternative("CustomerIntegration") } } };
        Assert.Equal(!nullable, generator.Model("Entries", model, true).Contains("Values.Any(item => item is null)", StringComparison.Ordinal));
        var tagged = JsonNode.Parse("""{"oneOf":[{"$ref":"#/components/schemas/NumberEntitlementValue"},{"$ref":"#/components/schemas/BooleanEntitlementValue"},{"$ref":"#/components/schemas/ConfigEntitlementValue"},{"type":"null"}],"discriminator":{"propertyName":"type","mapping":{"number":"#/components/schemas/NumberEntitlementValue","boolean":"#/components/schemas/BooleanEntitlementValue","object":"#/components/schemas/ConfigEntitlementValue"}}}""")!;
        if (keyword == "anyOf") { tagged["anyOf"] = tagged["oneOf"]!.DeepClone(); ((JsonObject)tagged).Remove("oneOf"); }
        model["properties"]!["entries"]!["additionalProperties"] = tagged;
        Assert.DoesNotContain("Values.Any(item => item is null)", generator.Model("TaggedEntries", model, true));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("extra-source")]
    [InlineData("manifest")]
    public void CheckModeDetectsGeneratedSourceAndApiDrift(string corruption)
    {
        using var fixture = new ContractFixture(); Generation.Run(fixture.Root, false);
        if (corruption == "manifest") File.WriteAllText(Path.Combine(fixture.Root, "contracts/batch1-api.json"), "[]");
        else File.AppendAllText(Path.Combine(fixture.Root, "src/KaitenCloud.Sdk/Management/Generated", corruption == "source" ? "Instances.g.cs" : "Unexpected.cs"), "// drift");
        Assert.Throws<InvalidDataException>(() => Generation.Run(fixture.Root, true));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../prior-candidate")]
    [InlineData("")]
    public async Task PackageCandidateLabelCannotEscapeTheArtifactDirectory(string label)
        => await Assert.ThrowsAsync<ArgumentException>(() => PackageCheck.RunAsync(Root, label));

    [Theory]
    [InlineData(false, "0.3.0-native-uncommitted")]
    [InlineData(true, "0.3.0")]
    public async Task PackageCheckSelectsOnlyTheExplicitCandidateVersion(bool published, string version)
    {
        using var fixture = new ContractFixture();
        var error = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => PackageCheck.RunAsync(fixture.Root, "fresh-feed", published));
        Assert.Contains($"KaitenCloud.Sdk.{version}.nupkg", error.Message, StringComparison.Ordinal);
    }

    private sealed class ContractFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "kaiten-contract-" + Guid.NewGuid().ToString("N"));
        internal ContractFixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "contracts"));
            foreach (var file in Directory.GetFiles(Path.Combine(ContractToolTests.Root, "contracts"), "*.json")) File.Copy(file, Path.Combine(Root, "contracts", Path.GetFileName(file)));
        }
        internal void RepinCore(JsonNode document)
        {
            var path = Path.Combine(Root, "contracts/core.openapi.json"); File.WriteAllText(path, Json.Render(document));
            var source = Json.Read(Path.Combine(Root, "contracts/source.json"));
            source["documents"]!["core"]!["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            File.WriteAllText(Path.Combine(Root, "contracts/source.json"), Json.Render(source));
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
