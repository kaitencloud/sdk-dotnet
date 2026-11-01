// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text.Json;
using KaitenCloud.Sdk;
using KaitenCloud.Sdk.Management;
using Xunit;
namespace KaitenCloud.Sdk.Tests;

public sealed class RestResourceMigrationTests
{
    internal const string InstanceJson = """
    {"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","slug":"ninja-osaka-prod","name":"Ninja","description":"",
     "customerId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","customerSlug":"ninja-osaka","licenseId":"cccccccc-cccc-cccc-cccc-cccccccccccc",
     "licenseSlug":"starter","startLicenseDate":"2026-01-01T00:00:00Z","endLicenseDate":"2027-01-01T00:00:00Z","status":"HEALTHY",
     "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z",
     "createdBy":{"id":"dddddddd-dddd-dddd-dddd-dddddddddddd"},"updatedBy":{"id":"dddddddd-dddd-dddd-dddd-dddddddddddd"}}
    """;
    internal static string Detail(string term)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(InstanceJson)!;
        fields["licenseTerm"] = JsonSerializer.Deserialize<JsonElement>(term);
        return JsonSerializer.Serialize(fields);
    }
    [Fact]
    public void DraftValidationApiAndBooleanDtoAreRemoved()
    {
        Assert.DoesNotContain(typeof(IKaitenClient).GetMethods(), m => m.Name=="ValidateInstanceLicenseAsync");
        Assert.DoesNotContain(typeof(KaitenClient).Assembly.GetExportedTypes(), t => t.Name is "LicenseValidation" or "LicenseValidationStatus");
        Assert.Equal(12, typeof(IKaitenClient).GetMethods().Length);
    }
    [Theory]
    [InlineData("ACTIVE")]
    [InlineData("NOT_STARTED")]
    [InlineData("EXPIRED")]
    public async Task ExistingDetailGetRetrievesResourceWithTypedTermAndNoValid(string state)
    {
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/edge/api/instances/ninja-osaka-prod", request.RequestUri!.AbsolutePath);
            var response = ClientTests.Json(Detail(JsonSerializer.Serialize(new { status=state, evaluatedAt="2026-11-01T12:00:00Z" })));
            response.Headers.CacheControl = new() { NoStore=true };
            return Task.FromResult(response);
        }));
        using var client = new KaitenClient(http, new() { BaseUri=new Uri("https://gateway.test/edge") });
        var instance = await client.GetInstanceAsync("ninja-osaka-prod");
        Assert.Equal("HEALTHY", instance.Status);
        Assert.Equal("starter", instance.LicenseSlug);
        using var management = new KaitenManagementClient(http, new() { BaseUri=new Uri("https://gateway.test/edge") });
        var managed = await management.Instances.GetAsync("ninja-osaka-prod");
        Assert.Equal(state, JsonSerializer.SerializeToElement(managed.LicenseTerm, KaitenJson.CreateOptions()).GetProperty("status").GetString());
        var property = typeof(Instance).GetProperty("LicenseTerm");
        Assert.NotNull(property);
        var term = property!.GetValue(instance);
        Assert.NotNull(term);
        var wire = JsonSerializer.SerializeToElement(term, term!.GetType(), KaitenJson.CreateOptions());
        Assert.Equal(state, wire.GetProperty("status").GetString());
        Assert.False(wire.TryGetProperty("valid", out _));
        Assert.False(JsonSerializer.SerializeToElement(instance.ToUpdateRequest(), KaitenJson.CreateOptions()).TryGetProperty("licenseTerm", out _));
    }
    [Fact]
    public async Task DetailIdentityMustMatchRequestedInstance()
    {
        var body = Detail("{\"status\":\"ACTIVE\",\"evaluatedAt\":\"2026-11-01T12:00:00Z\"}");
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(ClientTests.Json(body))));
        using var legacy = new KaitenClient(http, new() { BaseUri=new Uri("https://gateway.test") });
        using var management = new KaitenManagementClient(http, new() { BaseUri=new Uri("https://gateway.test") });
        await Assert.ThrowsAsync<KaitenProtocolException>(() => legacy.GetInstanceAsync("other-instance"));
        await Assert.ThrowsAsync<KaitenProtocolException>(() => management.Instances.GetAsync("other-instance"));
    }

    [Fact]
    public async Task SharedRepresentationsOmitTermButDetailDoesNotAllowOmission()
    {
        var instance = JsonSerializer.Deserialize<Instance>(InstanceJson, KaitenJson.CreateOptions())!;
        Assert.Null(instance.LicenseTerm);
        var rows = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(InstanceJson)!;
        rows["slug"] = JsonSerializer.SerializeToElement("instance-slug");
        var shared = JsonSerializer.Serialize(rows);
        using var http = new HttpClient(new StubHandler((request, _) => Task.FromResult(request.Method == HttpMethod.Post
            ? ClientTests.Json(shared, HttpStatusCode.Created)
            : request.RequestUri!.AbsolutePath.EndsWith("/instances", StringComparison.Ordinal)
                ? ClientTests.Json(JsonSerializer.Serialize(new { items = new[] { JsonSerializer.Deserialize<JsonElement>(shared) }, hasMore=false }))
                : ClientTests.Json(shared))));
        using var management = ManagementWireTests.NewClient(http);
        Assert.Null((await management.Instances.ListAsync()).Items![0].LicenseTerm);
        var create = JsonSerializer.Deserialize<KaitenCloud.Sdk.Management.Models.CreateInstanceRequest>(ManagementWireTests.Fixture("createInstance", false).GetProperty("request").GetRawText(), ManagementJson.Options)!;
        Assert.Null((await management.Instances.CreateAsync(create)).LicenseTerm);
        await Assert.ThrowsAsync<KaitenProtocolException>(() => management.Instances.GetAsync("instance-slug"));
        Assert.Null(typeof(KaitenCloud.Sdk.Management.Models.CreateInstanceRequest).GetProperty("LicenseTerm"));
        Assert.Null(typeof(KaitenCloud.Sdk.Management.Models.UpdateInstanceRequest).GetProperty("LicenseTerm"));
    }
    [Fact]
    public async Task FreshDetailReadsDoNotCacheTermInformation()
    {
        var count=0;
        using var handler = new StubHandler((_, _) =>
        {
            var status = ++count == 1 ? "ACTIVE" : "EXPIRED";
            var response = ClientTests.Json(Detail(JsonSerializer.Serialize(new { status, evaluatedAt="2026-11-01T12:00:00Z" })));
            response.Headers.CacheControl = new() { NoStore=true };
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler); using var client = new KaitenClient(http, new() { BaseUri=new Uri("https://gateway.test") });
        Assert.Equal(LicenseTermStatus.Active, (await client.GetInstanceAsync("ninja-osaka-prod")).LicenseTerm!.Status);
        Assert.Equal(LicenseTermStatus.Expired, (await client.GetInstanceAsync("ninja-osaka-prod")).LicenseTerm!.Status);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"status\":\"ACTIVE\"}")]
    [InlineData("{\"status\":null,\"evaluatedAt\":\"2026-11-01T12:00:00Z\"}")]
    [InlineData("{\"status\":0,\"evaluatedAt\":\"2026-11-01T12:00:00Z\"}")]
    [InlineData("{\"status\":\"active\",\"evaluatedAt\":\"2026-11-01T12:00:00Z\"}")]
    [InlineData("{\"status\":\"UNKNOWN\",\"evaluatedAt\":\"2026-11-01T12:00:00Z\"}")]
    [InlineData("{\"status\":\"ACTIVE\",\"evaluatedAt\":null}")]
    [InlineData("{\"status\":\"ACTIVE\",\"evaluatedAt\":\"private-invalid-date\"}")]
    public async Task MissingOrMalformedDetailTermIsProtocolFailure(string? term)
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(ClientTests.Json(term is null?InstanceJson:Detail(term)))));
        using var client = new KaitenClient(http, new() { BaseUri=new Uri("https://gateway.test") });
        var error = await Assert.ThrowsAsync<KaitenProtocolException>(() => client.GetInstanceAsync("ninja-osaka-prod"));
        Assert.DoesNotContain("private-invalid-date", error.ToString());
        using var management = new KaitenManagementClient(http, new() { BaseUri=new Uri("https://gateway.test") });
        await Assert.ThrowsAsync<KaitenProtocolException>(() => management.Instances.GetAsync("ninja-osaka-prod"));
    }
}
