// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using KaitenCloud.Sdk;
using KaitenCloud.Sdk.Management;
using Xunit;
namespace KaitenCloud.Sdk.Tests;

public sealed class LicenseTermInstantTests
{
    [Theory]
    [InlineData(false, "2026-11-01")]
    [InlineData(true, "2026-11-01")]
    [InlineData(false, "2026-11-01T12:00:00")]
    [InlineData(true, "2026-11-01T12:00:00")]
    public async Task DateOnlyOrMissingTimezoneIsProtocolFailureForBothGetters(bool management, string value)
    {
        using var http = Http(value);
        var error = await Assert.ThrowsAsync<KaitenProtocolException>(() => Get(http, management));
        Assert.DoesNotContain(value, error.ToString());
        Assert.Null(error.InnerException);
    }
    [Theory]
    [InlineData(false, "2026-11-01T12:00:00Z")]
    [InlineData(true, "2026-11-01T12:00:00Z")]
    [InlineData(false, "2026-11-01T21:00:00+09:00")]
    [InlineData(true, "2026-11-01T21:00:00+09:00")]
    [InlineData(false, "2026-11-01T05:00:00-07:00")]
    [InlineData(true, "2026-11-01T05:00:00-07:00")]
    public async Task ExplicitTimezonePreservesTheSameAbsoluteInstant(bool management, string value)
    {
        using var http = Http(value);
        var term = await Get(http, management);
        Assert.Equal(LicenseTermStatus.Active, term.Status);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), term.EvaluatedAt);
    }
    [Fact]
    public void UnrelatedDateFieldsDoNotUseInstantPropertyConverters()
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(RestResourceMigrationTests.InstanceJson)!;
        fields["createdAt"] = JsonSerializer.SerializeToElement("2026-11-01");
        fields["updatedAt"] = JsonSerializer.SerializeToElement("2026-11-01T12:00:00");
        var instance = JsonSerializer.Deserialize<Instance>(JsonSerializer.Serialize(fields), KaitenJson.CreateOptions());
        Assert.NotNull(instance); // Timestamp fields unrelated to term evaluation/assignment are intentionally unchanged.
        Assert.Null(instance!.LicenseTerm);
    }
    private static HttpClient Http(string value)
        => new(new StubHandler((_, _) => Task.FromResult(ClientTests.Json(RestResourceMigrationTests.Detail(
            JsonSerializer.Serialize(new { status="ACTIVE", evaluatedAt=value }))))));
    private static async Task<LicenseTerm> Get(HttpClient http, bool management)
    {
        var options = new KaitenClientOptions { BaseUri=new Uri("https://gateway.test") };
        if (management)
        {
            using var client = new KaitenManagementClient(http, options);
            return (await client.Instances.GetAsync("ninja-osaka-prod")).LicenseTerm!;
        }
        using var legacy = new KaitenClient(http, options);
        return (await legacy.GetInstanceAsync("ninja-osaka-prod")).LicenseTerm!;
    }
}
