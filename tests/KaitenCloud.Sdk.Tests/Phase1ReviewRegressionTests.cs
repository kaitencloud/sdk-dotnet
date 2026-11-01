// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text.Json;
using KaitenCloud.Sdk;
using KaitenCloud.Sdk.Management;
using KaitenCloud.Sdk.Management.Models;
using Xunit;
namespace KaitenCloud.Sdk.Tests;

public sealed class Phase1ReviewRegressionTests
{
    [Fact]
    public async Task RequiredGroupedUsageQueryCannotBeNullBeforeAuthOrHttp()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ClientTests.Json("[]")));
        using var http = new HttpClient(handler); var credentials = new Credentials();
        using var client = new KaitenManagementClient(http, new() { BaseUri = new Uri("https://gateway.test") }, credentials);
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.EntitlementGroups.GetUsageAsync("group-slug", null!));
        Assert.Equal(0, credentials.Calls); Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public void GroupedUsageQueryAndFieldHaveRequiredPromises()
    {
        var parameter = typeof(EntitlementGroupsResource).GetMethod("GetUsageAsync")!.GetParameters()[1];
        Assert.False(parameter.HasDefaultValue);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GetUsageEntitlementGroupQuery>("{}", ManagementJson.Options));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task NullOrEmptyRequiredInstanceNeverReachesAuthOrHttp(string? instance)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ClientTests.Json("[]")));
        using var http = new HttpClient(handler); var credentials = new Credentials();
        using var client = new KaitenManagementClient(http, new() { BaseUri = new Uri("https://gateway.test") }, credentials);
        await Assert.ThrowsAsync<ArgumentException>(() => client.EntitlementGroups.GetUsageAsync("group-slug", new GetUsageEntitlementGroupQuery { Instance = instance! }));
        Assert.Equal(0, credentials.Calls); Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task RequiredDeclaredStringQueryIsEscapedNotInjected()
    {
        // The query schema is a required string (no slug pattern); escaping is independent of server lookup.
        const string instance = "instance-slug&other=value";
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal("?instance=" + Uri.EscapeDataString(instance), request.RequestUri!.Query);
            return Task.FromResult(ClientTests.Json("[]"));
        }));
        using var client = ManagementWireTests.NewClient(http);
        Assert.Empty(await client.EntitlementGroups.GetUsageAsync("group-slug", new GetUsageEntitlementGroupQuery { Instance = instance }));
    }
    [Fact]
    public async Task NullRootTypedListItemIsProtocolFailure()
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(ClientTests.Json("[null]"))));
        using var client = ManagementWireTests.NewClient(http);
        await Assert.ThrowsAsync<KaitenProtocolException>(() => client.Instances.ListEntitlementUsageAsync("instance-slug"));
    }
    [Fact]
    public async Task NullTypedRequestDictionaryMemberFailsBeforeAuthOrHttp()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ClientTests.Json(ManagementWireTests.Fixture("create-customer", false).GetProperty("response").GetRawText(), HttpStatusCode.Created)));
        using var http = new HttpClient(handler); var credentials = new Credentials();
        using var client = new KaitenManagementClient(http, new() { BaseUri = new Uri("https://gateway.test") }, credentials);
        var request = new CreateCustomerRequest { Name = "Example", Integrations = new Dictionary<string, CustomerIntegrationInput> { ["adapter"] = null! } };
        await Assert.ThrowsAsync<ArgumentException>(() => client.Customers.CreateAsync(request));
        Assert.Equal(0, credentials.Calls); Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public void NullTypedDictionaryMemberIsRejectedOnRequestDeserialization()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateCustomerRequest>("""{"name":"Example","integrations":{"adapter":null}}""", ManagementJson.Options));
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task RootNullAndEmptyUsageCollectionCompatibilityRemains(string json)
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(ClientTests.Json(json))));
        using var client = ManagementWireTests.NewClient(http);
        Assert.Empty(await client.Instances.ListEntitlementUsageAsync("instance-slug"));
    }
    [Fact]
    public async Task TypedIntegrationAndArbitraryMetadataNullsRemainValid()
    {
        using var http = new HttpClient(new StubHandler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("integrations").GetProperty("adapter").GetProperty("metadata").GetProperty("custom").ValueKind);
            return ClientTests.Json(ManagementWireTests.Fixture("create-customer", false).GetProperty("response").GetRawText(), HttpStatusCode.Created);
        }));
        using var client = ManagementWireTests.NewClient(http);
        await client.Customers.CreateAsync(new CreateCustomerRequest
        {
            Name = "Example",
            Integrations = new Dictionary<string, CustomerIntegrationInput>
            {
                ["adapter"] = new() { ExternalId = "external-id", Metadata = new Dictionary<string, JsonElement?> { ["custom"] = null } }
            }
        });
    }
    [Fact]
    public async Task NullTypedResponseDictionaryMemberIsProtocolFailure()
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ManagementWireTests.Fixture("get-customer", false).GetProperty("response").GetRawText())!;
        data["integrations"] = JsonSerializer.Deserialize<JsonElement>("""{"adapter":null}""");
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(ClientTests.Json(JsonSerializer.Serialize(data)))));
        using var client = ManagementWireTests.NewClient(http);
        await Assert.ThrowsAsync<KaitenProtocolException>(() => client.Customers.GetAsync("customer-slug"));
    }
    private sealed class Credentials : IKaitenCredentialProvider
    {
        public int Calls { get; private set; }
        public ValueTask<string?> GetBearerTokenAsync(CancellationToken cancellationToken = default) { Calls++; return ValueTask.FromResult<string?>("fake-token"); }
    }
}
