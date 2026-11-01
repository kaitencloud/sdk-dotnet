// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using Xunit;
namespace KaitenCloud.Sdk.Tests;

public sealed class ProtocolCorrectionTests
{
    [Theory]
    [InlineData("ACTIVE, ACTIVE")]
    [InlineData("ACTIVE, EXPIRED")]
    [InlineData("ACTIVE, NOT_STARTED")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("Active")]
    [InlineData("active")]
    [InlineData("NotStarted")]
    [InlineData("not_started")]
    [InlineData("expired")]
    [InlineData(" ACTIVE")]
    [InlineData("ACTIVE ")]
    [InlineData("\tACTIVE")]
    [InlineData("ACTIVE\n")]
    public async Task LicenseStatusRejectsAliasesAndComposites(string status)
    {
        foreach (var operationalStatus in new[] { "HEALTHY", "MAINTENANCE" })
        {
            using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Term(status, operationalStatus))));
            using var client = Client(http);
            await Assert.ThrowsAsync<KaitenProtocolException>(() => client.GetInstanceAsync("ninja-osaka-prod"));
        }
    }

    [Theory]
    [InlineData("ACTIVE", LicenseTermStatus.Active)]
    [InlineData("NOT_STARTED", LicenseTermStatus.NotStarted)]
    [InlineData("EXPIRED", LicenseTermStatus.Expired)]
    public async Task ExactKnownStatesRemainResourceInformation(string status, LicenseTermStatus expected)
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Term(status))));
        using var client = Client(http);
        var result = await client.GetInstanceAsync("ninja-osaka-prod");
        Assert.Equal(expected, result.LicenseTerm!.Status);
        Assert.Equal("HEALTHY", result.Status);
        Assert.Equal(status, JsonSerializer.SerializeToElement(result.LicenseTerm, KaitenJson.CreateOptions()).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("ACTIVE")]
    [InlineData("NOT_STARTED")]
    [InlineData("EXPIRED")]
    public async Task KnownStateStillRequiresDatabaseInstant(string status)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ClientTests.Validation)!;
        fields["licenseTerm"] = JsonSerializer.SerializeToElement(new { status });
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(ClientTests.Json(JsonSerializer.Serialize(fields)))));
        using var client = Client(http);
        await Assert.ThrowsAsync<KaitenProtocolException>(() => client.GetInstanceAsync("ninja-osaka-prod"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LicenseStatusRejectsNumericTokens(int status)
    {
        foreach (var operationalStatus in new[] { "HEALTHY", "MAINTENANCE" })
        {
            using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Term(status, operationalStatus))));
            using var client = Client(http);
            await Assert.ThrowsAsync<KaitenProtocolException>(() => client.GetInstanceAsync("ninja-osaka-prod"));
        }
    }
    private static HttpResponseMessage Term(object status, string operationalStatus = "HEALTHY")
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ClientTests.Validation)!;
        fields["status"] = JsonSerializer.SerializeToElement(operationalStatus);
        fields["licenseTerm"] = JsonSerializer.SerializeToElement(new { status, evaluatedAt = "2026-10-30T12:00:00Z" });
        return ClientTests.Json(JsonSerializer.Serialize(fields));
    }

    [Theory]
    [InlineData("unrelated-entitlement")]
    [InlineData("Delivery-tracking")]
    [InlineData("delivery-tracking ")]
    public async Task SingleUsageReadRejectsAValueForAnotherEntitlement(string returnedSlug)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Usage(returnedSlug, new { type = "boolean", value = true })));
        using var http = new HttpClient(handler);
        using var client = Client(http);
        await Assert.ThrowsAsync<KaitenProtocolException>(() => client.GetInstanceEntitlementUsageAsync("ninja-osaka-prod", "delivery-tracking"));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(UsageReportBehavior.Append)]
    [InlineData(UsageReportBehavior.Set)]
    public async Task NumericReportRejectsAnotherEntitlementWithoutReplaying(UsageReportBehavior behavior)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Usage("unrelated-entitlement", new { type = "number", value = 19 })));
        using var http = new HttpClient(handler);
        using var client = Client(http);
        await Assert.ThrowsAsync<KaitenProtocolException>(() => client.ReportNumericUsageAsync("ninja-osaka-prod", "monthly-orders", 1, behavior));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MatchingSingleUsagePreservesActualBooleanValue(bool value)
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Usage("delivery-tracking", new { type = "boolean", value }))));
        using var client = Client(http);
        var result = await client.GetInstanceEntitlementUsageAsync("ninja-osaka-prod", "delivery-tracking");
        Assert.Equal("delivery-tracking", result.EntitlementSlug);
        Assert.Equal(value, ((BooleanEntitlementValue)result.Value).Value);
    }

    [Fact]
    public async Task MatchingNumericReportPreservesActualUsage()
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Usage("monthly-orders", new { type = "number", value = 19 }))));
        using var client = Client(http);
        var result = await client.ReportNumericUsageAsync("ninja-osaka-prod", "monthly-orders", 1, UsageReportBehavior.Append);
        Assert.Equal("monthly-orders", result.EntitlementSlug);
        Assert.Equal(19, ((NumberEntitlementValue)result.Value).Value);
    }

    private static KaitenClient Client(HttpClient http) => new(http, new()
    { BaseUri = new Uri("https://test.invalid/api"), MaxRetries = 3, RetryDelay = TimeSpan.Zero });
    private static HttpResponseMessage Usage(string entitlementSlug, object value) => ClientTests.Json(JsonSerializer.Serialize(new
    {
        entitlementId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        entitlementSlug,
        licenseId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        licenseSlug = "starter",
        value
    }));
}
