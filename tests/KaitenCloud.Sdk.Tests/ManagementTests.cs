// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using KaitenCloud.Sdk;
using KaitenCloud.Sdk.Management;
using KaitenCloud.Sdk.Management.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace KaitenCloud.Sdk.Tests;

public sealed class ManagementTests
{
    private static string Customer => ManagementWireTests.Fixture("get-customer", false).GetProperty("response").GetRawText();
    [Fact]
    public async Task RequiredNullsAndReadOnlyRequestFieldsAreExact()
    {
        using var handler = new StubHandler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("description").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("metadata").ValueKind);
            Assert.False(body.RootElement.TryGetProperty("id", out _));
            Assert.False(body.RootElement.TryGetProperty("createdAt", out _));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var http = new HttpClient(handler); using var client = ManagementWireTests.NewClient(http);
        var request = JsonSerializer.Deserialize<global::KaitenCloud.Sdk.Management.Models.UpdateFeatureFlagRequest>(ManagementWireTests.Fixture("update-feature-flag", false).GetProperty("request").GetRawText(), ManagementJson.Options)!;
        await client.FeatureFlags.UpdateAsync("some-flag", request with { Metadata = null });
    }
    [Fact]
    public async Task OptionalNullFalseZeroAndOmissionRemainDistinct()
    {
        var bodies = new List<JsonElement>();
        using var http = new HttpClient(new StubHandler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); bodies.Add(body.RootElement.Clone());
            return ClientTests.Json(ManagementWireTests.Fixture("updateLicenseFamily", false).GetProperty("response").GetRawText());
        }));
        using var client = ManagementWireTests.NewClient(http);
        await client.LicenseFamilies.UpdateAsync("family-slug", new UpdateLicenseFamilyRequest());
        await client.LicenseFamilies.UpdateAsync("family-slug", new UpdateLicenseFamilyRequest { IsPublic = false, FallbackLicenseFamilySlug = new Optional<string?>(null), TrialReminderDays = 0 });
        Assert.Empty(bodies[0].EnumerateObject());
        Assert.False(bodies[1].GetProperty("isPublic").GetBoolean());
        Assert.Equal(JsonValueKind.Null, bodies[1].GetProperty("fallbackLicenseFamilySlug").ValueKind);
        Assert.Equal(0, bodies[1].GetProperty("trialReminderDays").GetInt32());
    }
    [Fact]
    public async Task PatchStatusAndLifecycleOnlyWriteTheirOwnField()
    {
        var bodies = new List<JsonElement>();
        using var http = new HttpClient(new StubHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); bodies.Add(body.RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        using var client = ManagementWireTests.NewClient(http);
        await client.Instances.UpdateStatusAsync("instance-slug", PatchInstanceRequestStatus.Healthy);
        await client.Instances.UpdateLifecycleStageAsync("instance-slug", "CHURNED");
        Assert.Equal("HEALTHY", bodies[0].GetProperty("status").GetString());
        Assert.False(bodies[0].TryGetProperty("lifecycleStage", out _));
        Assert.Equal("CHURNED", bodies[1].GetProperty("lifecycleStage").GetString());
        Assert.False(bodies[1].TryGetProperty("status", out _));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Instances.UpdateLifecycleStageAsync("instance-slug", ""));
    }
    [Theory]
    [InlineData("..")]
    [InlineData("../private")]
    [InlineData("https://other.test")]
    [InlineData("ab%2fsecret")]
    [InlineData("ab\\secret")]
    [InlineData("some-slug\n")]
    public async Task UnsafePathsNeverAcquireCredentialsOrSend(string value)
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException());
        using var http = new HttpClient(handler); using var client = ManagementWireTests.NewClient(http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.Customers.GetAsync(value));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Integrations.GetCustomerByExternalIdAsync("adapter", value));
        Assert.Equal(0, handler.Calls);
    }
    [Theory]
    [InlineData("create-customer")]
    [InlineData("updateInstance")]
    [InlineData("patchInstance")]
    [InlineData("delete-license")]
    [InlineData("publish-license")]
    public async Task EveryKindOfMutationNeverReplaysNetworkFailure(string id)
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("private-url"));
        using var http = new HttpClient(handler); using var client = ManagementWireTests.NewClient(http, 3);
        await Assert.ThrowsAsync<KaitenNetworkException>(() => ManagementWireTests.Invoke(client, ManagementWireTests.Operation(id), ManagementWireTests.Fixture(id, false)));
        Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData("{\"type\":\"boolean\"}")]
    [InlineData("{\"type\":\"BOOLEAN\",\"value\":true}")]
    [InlineData("{\"type\":\"boolean\",\"value\":null}")]
    public async Task MissingOrUnknownValueUnionIsProtocolFailure(string value)
    {
        var fixture = ManagementWireTests.Fixture("getEntitlementUsageMetrics", false).GetProperty("response");
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fixture.GetRawText())!;
        data["value"] = JsonSerializer.Deserialize<JsonElement>(value);
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(ClientTests.Json(JsonSerializer.Serialize(data)))));
        using var client = ManagementWireTests.NewClient(http);
        await Assert.ThrowsAsync<KaitenProtocolException>(() => client.Instances.GetEntitlementUsageAsync("instance-slug", "entitlement-slug"));
    }
    [Fact]
    public async Task NullableCollectionsNormalizeOnlyWhereDeclared()
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(ClientTests.Json("null"))));
        using var client = ManagementWireTests.NewClient(http);
        Assert.Empty(await client.Instances.ListEntitlementUsageAsync("instance-slug"));
        await Assert.ThrowsAsync<KaitenProtocolException>(() => client.Customers.GetAsync("customer-slug"));
    }
    [Fact]
    public async Task InvalidTimestampOrWrongMediaIsProtocolFailure()
    {
        var customer = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Customer)!;
        customer["createdAt"] = JsonSerializer.SerializeToElement("private-invalid-date");
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(ClientTests.Json(JsonSerializer.Serialize(customer)))));
        using var client = ManagementWireTests.NewClient(http);
        await Assert.ThrowsAsync<KaitenProtocolException>(() => client.Customers.GetAsync("customer-slug"));
        using var badMediaHttp = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Customer) })));
        using var other = ManagementWireTests.NewClient(badMediaHttp);
        await Assert.ThrowsAsync<KaitenProtocolException>(() => other.Customers.GetAsync("customer-slug"));
    }
    [Fact]
    public async Task NullReusedDefaultVariantIsNotAValidManagementResult()
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ManagementWireTests.Fixture("get-feature-flag", false).GetProperty("response").GetRawText())!;
        fields["default_variant"] = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"basic\",\"value\":null}");
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(ClientTests.Json(JsonSerializer.Serialize(fields)))));
        using var client = ManagementWireTests.NewClient(http);
        await Assert.ThrowsAsync<KaitenProtocolException>(() => client.FeatureFlags.GetAsync("some-flag"));
    }
    [Fact]
    public async Task AssignedTermValidationUsesTheServerDecisionAndRequestedInstance()
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ManagementWireTests.Fixture("getInstance", false).GetProperty("response").GetRawText())!;
        fields["startLicenseDate"] = JsonSerializer.SerializeToElement("2000-01-01T00:00:00Z");
        fields["endLicenseDate"] = JsonSerializer.SerializeToElement("2001-01-01T00:00:00Z");
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(ClientTests.Json(JsonSerializer.Serialize(fields)))));
        using var client = ManagementWireTests.NewClient(http);
        Assert.Equal(LicenseTermStatus.Active, (await client.Instances.GetAsync("instance-slug")).LicenseTerm!.Status); // No local clock policy.
        await Assert.ThrowsAsync<KaitenProtocolException>(() => client.Instances.GetAsync("other-instance"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("repeat")]
    [InlineData("cycle")]
    public async Task IncompleteOrCyclicPaginationCannotReturnPartialList(string kind)
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler((_, _) =>
        {
            calls++;
            var next = kind switch { "missing" => null, "repeat" => "a", _ => calls % 2 == 1 ? "a" : "b" };
            return Task.FromResult(ClientTests.Json(JsonSerializer.Serialize(new { items = new[] { JsonSerializer.Deserialize<JsonElement>(Customer) }, hasMore = true, nextCursor = next })));
        }));
        using var client = ManagementWireTests.NewClient(http);
        await Assert.ThrowsAsync<KaitenProtocolException>(() => client.Customers.ListAllAsync());
        Assert.InRange(calls, 1, 3);
    }
    [Fact]
    public async Task PaginationPreservesPageSizeAndIndependentTotalLimit()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Contains("limit=2", request.RequestUri!.Query);
            if (++calls == 2) Assert.Contains("cursor=opaque", request.RequestUri.Query);
            return Task.FromResult(ClientTests.Json(JsonSerializer.Serialize(new { items = new[] { JsonSerializer.Deserialize<JsonElement>(Customer), JsonSerializer.Deserialize<JsonElement>(Customer) }, hasMore = calls == 1, nextCursor = calls == 1 ? "opaque" : null })));
        }));
        using var client = ManagementWireTests.NewClient(http);
        var query = new ListCustomerQuery { Limit = 2 };
        var items = await client.Customers.ListAllAsync(query, totalLimit:3);
        Assert.Equal(3, items.Count); Assert.Equal(2, calls);
        Assert.False(query.Cursor.IsSpecified);
    }
    [Fact]
    public async Task OwnershipAndDiDisposalRemainExplicit()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ClientTests.Json(Customer)));
        using var http = new HttpClient(handler);
        var client = ManagementWireTests.NewClient(http); client.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.Customers.GetAsync("customer-slug"));
        await http.GetAsync("https://gateway.test/anything"); Assert.False(handler.Disposed);
        var services = new ServiceCollection();
        services.AddKaitenManagement(o => o.BaseUri = new Uri("https://gateway.test"));
        services.AddHttpClient("KaitenCloud.Sdk.Management").ConfigurePrimaryHttpMessageHandler(() => new StubHandler((_, _) => Task.FromResult(ClientTests.Json(Customer))));
        IKaitenManagementClient managed;
        using (var provider = services.BuildServiceProvider())
        {
            managed = provider.GetRequiredService<IKaitenManagementClient>();
            Assert.NotNull(await managed.Customers.GetAsync("customer-slug"));
        }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => managed.Customers.GetAsync("customer-slug"));
    }
    [Fact]
    public async Task CallerCancellationDeadlineAndCredentialFailureStayDistinct()
    {
        using var handler = new StubHandler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return ClientTests.Json(Customer); });
        using var http = new HttpClient(handler);
        using var client = new KaitenManagementClient(http, new() { BaseUri = new Uri("https://gateway.test"), RequestTimeout = TimeSpan.FromMilliseconds(30) });
        await Assert.ThrowsAsync<KaitenTimeoutException>(() => client.Customers.GetAsync("customer-slug"));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Customers.GetAsync("customer-slug", canceled.Token));
        using var unavailable = new KaitenManagementClient(http, new() { BaseUri = new Uri("https://gateway.test"), BearerToken = "must-not-fallback" }, new FailedCredentials());
        var error = await Assert.ThrowsAsync<KaitenCredentialException>(() => unavailable.Customers.GetAsync("customer-slug"));
        Assert.DoesNotContain("private-credential", error.ToString());
    }
    private sealed class FailedCredentials : IKaitenCredentialProvider
    {
        public ValueTask<string?> GetBearerTokenAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("private-credential");
    }
}
