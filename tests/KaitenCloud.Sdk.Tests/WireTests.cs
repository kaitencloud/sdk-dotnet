// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text.Json;
using Xunit;
namespace KaitenCloud.Sdk.Tests;

public sealed class WireTests
{
    [Theory]
    [InlineData("""{"type":"number","value":12,"event_count":4}""", typeof(NumberEntitlementValue))]
    [InlineData("""{"type":"boolean","value":false}""", typeof(BooleanEntitlementValue))]
    [InlineData("""{"value":{"tier":"gold"},"type":"object","additional":42}""", typeof(ObjectEntitlementValue))]
    public void LowercaseUnionHandlesValuesAndAdditiveResponses(string json, Type type)
    {
        var value = KaitenJson.Deserialize<EntitlementValue>(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.IsType(type, value);
        var wire = JsonSerializer.Serialize<EntitlementValue>(value, KaitenJson.Options);
        Assert.DoesNotContain("event_count", wire); Assert.DoesNotContain("additional", wire);
        Assert.Contains("\"type\":", wire);
    }
    [Theory]
    [InlineData("""{"type":"boolean"}""")]
    [InlineData("""{"type":"number"}""")]
    [InlineData("""{"type":"boolean","value":null}""")]
    [InlineData("""{"type":"BOOLEAN","value":true}""")]
    [InlineData("""{"type":"object","value":null}""")]
    public void MissingOrUnsupportedSecurityValuesAreNeverDefaults(string json)
        => Assert.Throws<KaitenProtocolException>(() => KaitenJson.Deserialize<EntitlementValue>(System.Text.Encoding.UTF8.GetBytes(json)));
    [Fact]
    public async Task NumericReportHasExplicitBehaviorAndNoReadOnlyFields()
    {
        using var handler = new StubHandler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(new[] { "behavior", "value" }, body.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.Equal("append", body.RootElement.GetProperty("behavior").GetString());
            Assert.Equal("number", body.RootElement.GetProperty("value").GetProperty("type").GetString());
            return ClientTests.Json("""
            {"entitlementId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","entitlementSlug":"monthly-orders",
             "licenseId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","licenseSlug":"starter","value":{"type":"number","value":19,"event_count":2},"limit":{"type":"number","value":20}}
            """);
        });
        using var http = new HttpClient(handler); using var client = new KaitenClient(http, new() { BaseUri = new Uri("https://test.invalid/api") });
        var result = await client.ReportNumericUsageAsync("ninja-osaka-prod", "monthly-orders", 1, UsageReportBehavior.Append);
        Assert.Equal(19, ((NumberEntitlementValue)result.Value).Value); Assert.Equal(2, ((NumberEntitlementValue)result.Value).EventCount);
    }
    [Fact]
    public void FullFlagWritePreservesCurrentStrictSchemaAndAllTargetingTypes()
    {
        var json = """
        {"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","slug":"some-flag","type":"boolean","name":"Flag","description":null,
         "variants":[{"name":"on","description":"enabled","value":true}],"targetings":[
          {"type":"basic","name":"basic","rule":"true","variant":"on"},
          {"type":"rollout_percentage","name":"percent","rule":"true","distribution":{"on":100}},
          {"type":"rollout_date","name":"date","rule":"true","start":{"variant":"on","percentage":0,"date":"2026-01-01T00:00:00Z"},"end":{"variant":"on","percentage":100,"date":"2027-01-01T00:00:00Z"}}],
         "default_variant":{"type":"rollout_percentage","distribution":{"on":100}},"metadata":{"custom":42},"enabled":false,"event_name":"flag.event","future":true}
        """;
        var flag = KaitenJson.Deserialize<FeatureFlag>(System.Text.Encoding.UTF8.GetBytes(json));
        var wire = JsonSerializer.SerializeToElement(flag.ToUpdateRequest(), KaitenJson.Options);
        Assert.False(wire.TryGetProperty("id", out _)); Assert.False(wire.TryGetProperty("future", out _));
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("description").ValueKind);
        Assert.Equal("rollout_percentage", wire.GetProperty("default_variant").GetProperty("type").GetString());
        Assert.Equal(3, wire.GetProperty("targetings").GetArrayLength());
        Assert.Equal(42, wire.GetProperty("metadata").GetProperty("custom").GetInt32());
        Assert.True(flag.Extensions!["future"].GetBoolean());
    }
}
