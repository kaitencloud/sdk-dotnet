// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text;
using System.Text.Json;
using KaitenCloud.Sdk;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.Kiota.Serialization.Json;
using Xunit;
using Core = KaitenCloud.Spike.Core;
using Models = KaitenCloud.Spike.Core.Models;
using Platform = KaitenCloud.Spike.Platform;

namespace KaitenCloud.Spike.Tests;

// Characterization of an INTERNAL experiment, not a claim of supported SDK parity.
public sealed class SpikeTests
{
    private const string CustomerJson = """
    {"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","slug":"ninja-osaka","name":"Ninja",
     "createdAt":"2026-10-01T00:00:00Z","updatedAt":"2026-10-02T00:00:00Z",
     "createdBy":{"id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"},"updatedBy":{"id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"}}
    """;
    private const string FlagJson = """
    {"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","slug":"some-flag","type":"boolean","name":"Flag",
     "description":null,"variants":[],"targetings":[],"default_variant":{"type":"basic","value":"off"},
     "metadata":null,"enabled":false,"event_name":"flag.event"}
    """;
    private const string UsageJson = """
    {"entitlementId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","entitlementSlug":"monthly-orders",
     "licenseId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","licenseSlug":"starter",
     "transactionId":"order-42","source":null,"provenance":null,"value":{"type":"number","value":19,"event_count":2}}
    """;

    private static T Parse<T>(string json, ParsableFactory<T> factory) where T : IParsable
    {
        using var document = JsonDocument.Parse(json);
        return new JsonParseNode(document.RootElement).GetObjectValue(factory)!;
    }
    private static JsonElement Serialize(IParsable model)
    {
        using var writer = new JsonSerializationWriter();
        writer.WriteObjectValue(null, model);
        using var stream = writer.GetSerializedContent();
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Customer201UsesTypedResponseAndDoesNotWriteReadOnlyFields()
    {
        using var fixture = new Fixture("core", "Authorization", "org-token", (_, _) => Json(CustomerJson, HttpStatusCode.Created));
        var result = await fixture.Core.Customers.PostAsync(Parse(CustomerJson, Models.Customer.CreateFromDiscriminatorValue));
        Assert.Equal("ninja-osaka", result!.Slug);
        using var body = JsonDocument.Parse(fixture.Body!);
        Assert.Equal("Ninja", body.RootElement.GetProperty("name").GetString());
        foreach (var field in new[] { "id", "createdAt", "updatedAt", "createdBy", "updatedBy" })
            Assert.False(body.RootElement.TryGetProperty(field, out _));
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task Instance204PreservesAbsoluteTermAndOmitsReadOnlyIdentity()
    {
        using var fixture = new Fixture("core", "Authorization", "org-token", (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        var instance = Parse("""
        {"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","name":"Ninja","description":"","customerId":"customer-id","licenseId":"license-id",
         "startLicenseDate":"2026-10-01T05:30:00+05:30","endLicenseDate":"2027-10-01T05:30:00+05:30","customerSlug":"ninja-osaka","licenseSlug":"starter"}
        """, Models.Instance.CreateFromDiscriminatorValue);
        await fixture.Core.Instances["ninja-osaka-prod"].PutAsync(instance);
        using var body = JsonDocument.Parse(fixture.Body!);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), body.RootElement.GetProperty("startLicenseDate").GetDateTimeOffset());
        Assert.False(body.RootElement.TryGetProperty("id", out _));
        Assert.False(body.RootElement.TryGetProperty("customerSlug", out _));
        Assert.False(body.RootElement.TryGetProperty("licenseSlug", out _));
    }

    [Fact]
    public async Task Grant204WritesLowercaseNumberAndNeverEventCount()
    {
        using var fixture = new Fixture("core", "Authorization", "org-token", (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        var grant = Parse("""
        {"licenseId":"license-id","entitlementSlug":"monthly-orders","licenseSlug":"starter","value":{"type":"number","value":20,"event_count":8},"limitCapExceededOveragePercent":10}
        """, Models.LicenseEntitlement.CreateFromDiscriminatorValue);
        await fixture.Core.Licenses["starter"].Entitlements["monthly-orders"].PutAsync(grant);
        using var body = JsonDocument.Parse(fixture.Body!);
        Assert.Equal(10, body.RootElement.GetProperty("limitCapExceededOveragePercent").GetInt32());
        var value = body.RootElement.GetProperty("value");
        Assert.Equal("number", value.GetProperty("type").GetString());
        Assert.Equal(20, value.GetProperty("value").GetDouble());
        Assert.False(value.TryGetProperty("event_count", out _));
        Assert.False(body.RootElement.TryGetProperty("licenseId", out _));
    }

    [Fact]
    public async Task RichUsageKeyIsExplicitAndResponseTransactionIsAvailable()
    {
        using var fixture = new Fixture("core", "Authorization", "org-token", (request, _) =>
        {
            Assert.Equal("order-42", request.Headers.GetValues("Idempotency-Key").Single());
            var response = Json(UsageJson);
            response.Headers.Add("Idempotent-Replayed", "true");
            response.Headers.Add("Kaiten-Metadata-Dropped", "too_large");
            return response;
        });
        var report = Parse("""{"behavior":"append","value":{"type":"number","value":1}}""", Models.ReportEntitlementUsageBody.CreateFromDiscriminatorValue);
        var result = await fixture.Core.Instances["ninja-osaka-prod"].Entitlements["monthly-orders"].Usage.PostAsync(report,
            request => request.Headers.Add("Idempotency-Key", "order-42"));
        Assert.Equal("order-42", result!.TransactionId);
        Assert.Equal(19, result.Value!.NumberEntitlementValue!.Value);
        using var body = JsonDocument.Parse(fixture.Body!);
        Assert.False(body.RootElement.TryGetProperty("transactionId", out _));
        Assert.Equal("append", body.RootElement.GetProperty("behavior").GetString());
        Assert.Null(result.Provenance);
        Assert.Null(result.Source);
        // Headers are not exposed by the generated result: a future facade must preserve them.
        Assert.Null(typeof(Models.ReportedEntitlementUsage).GetProperty("Replayed"));
        Assert.Null(typeof(Models.ReportedEntitlementUsage).GetProperty("MetadataDropped"));
    }

    [Theory]
    [InlineData("text/csv", "invoice,total\n1,199\n")]
    [InlineData("application/x-ndjson", "{\"total\":199}\n")]
    public async Task InvoiceExportKeepsMediaBytesAndCallerDisposesStream(string media, string expected)
    {
        using var fixture = new Fixture("core", "Authorization", "org-token", (_, _) => new(HttpStatusCode.OK)
        { Content = new StringContent(expected, Encoding.UTF8, media) });
        await using var stream = await fixture.Core.Invoices.Export.GetAsync();
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        Assert.Equal(expected, await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData("catalog", "X-Kaiten-Publishable-Key", "public-key")]
    [InlineData("session", "Authorization", "customer-session")]
    [InlineData("checkout", "Authorization", "anonymous-checkout")]
    [InlineData("platform", "Authorization", "platform-token")]
    public async Task CredentialBoundRequestsUseActualSpecHeaders(string mode, string header, string credential)
    {
        using var fixture = new Fixture(mode, header, credential, (_, _) => Json("{}"));
        switch (mode)
        {
            case "catalog": await fixture.Core.Public.Catalog.GetAsync(); break;
            case "session": await fixture.Core.Public.Session.Portal.GetAsync(); break;
            case "checkout": await fixture.Core.Public.CheckoutSessions["session-42"].Confirm.PostAsync(); break;
            default: await fixture.Platform.Platform.Me.GetAsync(); break;
        }
        Assert.Equal(header == "Authorization" ? "Bearer " + credential : credential, fixture.LastHeaders[header]);
        Assert.DoesNotContain("org-token", string.Join(" ", fixture.LastHeaders.Values));
        if (mode == "catalog") Assert.False(fixture.LastHeaders.ContainsKey("Authorization"));
        else Assert.False(fixture.LastHeaders.ContainsKey("X-Kaiten-Publishable-Key"));
    }

    [Fact]
    public async Task CredentialsAreResolvedForEachRequestInsteadOfCachedByAdapter()
    {
        using var fixture = new Fixture("core", "Authorization", "unused-static", (_, _) => Json(CustomerJson));
        var calls = 0;
        fixture.Auth.ResolveCredential = () => "rotated-" + ++calls;
        await fixture.Core.Customers["ninja-osaka"].GetAsync();
        Assert.Equal("Bearer rotated-1", fixture.LastHeaders["Authorization"]);
        await fixture.Core.Customers["ninja-osaka"].GetAsync();
        Assert.Equal("Bearer rotated-2", fixture.LastHeaders["Authorization"]);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PlatformEnsureAndVendorSessionIssuanceAreDifferentTypedOperations()
    {
        using var platform = new Fixture("platform", "Authorization", "platform-token", (_, _) => Json("{\"id\":\"org-42\"}"));
        var org = await platform.Platform.Platform.Organizations.PostAsync(new Platform.Models.EnsureOrganizationBody { ExternalId = "org-42", Name = "Org" });
        Assert.Equal("org-42", org!.Id);
        using var vendor = new Fixture("core", "Authorization", "org-token", (_, _) => Json("{\"token\":\"session-secret\"}", HttpStatusCode.Created));
        await vendor.Core.CustomerSessions.PostAsync(new Models.CustomerSessionDraft { CustomerSlug = "ninja-osaka", InstanceSlug = "ninja-osaka-prod", TtlSeconds = 300 });
        using var body = JsonDocument.Parse(vendor.Body!);
        Assert.Equal(300, body.RootElement.GetProperty("ttlSeconds").GetInt32());
        Assert.Equal(1, platform.Calls);
        Assert.Equal(1, vendor.Calls);
    }

    [Fact]
    public async Task CallerCancellationReachesGeneratedAdapterAndHttp()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using var fixture = new Fixture("core", "Authorization", "org-token", (_, token) => { token.ThrowIfCancellationRequested(); return Json(CustomerJson); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Core.Customers.PostAsync(new Models.Customer { Name = "Ninja" }, cancellationToken: canceled.Token));
        Assert.Equal(0, fixture.Calls);
    }

    [Fact]
    public async Task AmbiguousMutationIsNotReplayedWithExplicitMiddlewareFreeHttpClient()
    {
        using var fixture = new Fixture("core", "Authorization", "org-token", (_, _) => throw new HttpRequestException("network"));
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Core.Customers.PostAsync(new Models.Customer { Name = "Ninja" }));
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task ProtectedFixtureRejectsRawCrossOriginOrOfrepBeforeCredentialsOrHttp()
    {
        using var fixture = new Fixture("core", "Authorization", "org-token", (_, _) => Json(CustomerJson));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Core.Customers.WithUrl("https://foreign.test/api/customers").GetAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Core.Customers.WithUrl("https://gateway.test/api/ofrep/v1/evaluate/flags").GetAsync());
        Assert.Equal(0, fixture.Calls);
        Assert.Equal(0, fixture.Auth.Calls);
        // This guard is fixture-owned; generated WithUrl remains unsuitable as a public API.
    }

    [Fact]
    public void CharacterizationRequiredNullFlagFieldsAreOmittedByKiotaWriter()
    {
        var body = Serialize(Parse(FlagJson, Models.FeatureFlag.CreateFromDiscriminatorValue));
        Assert.False(body.TryGetProperty("description", out _));
        Assert.False(body.TryGetProperty("metadata", out _));
        // Core requires BOTH fields, even when null. This is an acceptance blocker, not a valid write.
        var existing = JsonSerializer.SerializeToElement(new UpdateFeatureFlagRequest
        { Type = "boolean", Name = "Flag", Description = null, Metadata = null, Enabled = false, EventName = "flag.event",
          Variants = [], Targetings = [], DefaultVariant = new BasicDefaultFlagVariant { Value = "off" } }, KaitenJson.CreateOptions());
        Assert.Equal(JsonValueKind.Null, existing.GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Null, existing.GetProperty("metadata").ValueKind);
    }

    [Fact]
    public void CharacterizationMissingRequiredUsageValueIsAcceptedAsNullable()
    {
        var missing = Parse("{\"type\":\"boolean\"}", Models.BooleanEntitlementValue.CreateFromDiscriminatorValue);
        Assert.Null(missing.Value); // The existing SDK rejects the same envelope as a protocol failure.
        var wire = Encoding.UTF8.GetBytes("{\"type\":\"boolean\"}");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EntitlementValue>(wire, KaitenJson.CreateOptions()));
    }

    [Fact]
    public void CharacterizationUnionDiscriminatorIsCaseInsensitiveUnlikeCoreContract()
    {
        var uppercase = Parse("{\"value\":{\"type\":\"BOOLEAN\",\"value\":false}}", Models.ReportEntitlementUsageBody.CreateFromDiscriminatorValue);
        Assert.NotNull(uppercase.Value!.BooleanEntitlementValue);
        var invalid = Encoding.UTF8.GetBytes("{\"type\":\"BOOLEAN\",\"value\":false}");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EntitlementValue>(invalid, KaitenJson.CreateOptions()));
    }

    [Fact]
    public async Task CharacterizationGeneratedCustomerCallAcceptsUnspecified200InsteadOf201()
    {
        using var fixture = new Fixture("core", "Authorization", "org-token", (_, _) => Json(CustomerJson));
        var result = await fixture.Core.Customers.PostAsync(new Models.Customer { Name = "Ninja" });
        Assert.NotNull(result); // Core specifies201; the adapter accepts any successful HTTP status.
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public void GeneratedTypesAreInternalAndSdkPublicSurfaceIsFrozen()
    {
        Assert.DoesNotContain(typeof(Core.CoreSpikeClient).Assembly.GetExportedTypes(), type => type.Namespace?.StartsWith("KaitenCloud.Spike.Core", StringComparison.Ordinal) == true || type.Namespace?.StartsWith("KaitenCloud.Spike.Platform", StringComparison.Ordinal) == true);
        Assert.Equal(12, typeof(IKaitenClient).GetMethods().Length);
        Assert.DoesNotContain(typeof(IKaitenClient).Assembly.GetExportedTypes(), type => type.Name.Contains("Evaluation", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(IKaitenClient).GetMethods(), method => method.Name.Contains("Evaluate", StringComparison.Ordinal));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient http;
        private readonly HttpClientRequestAdapter adapter;
        public BoundaryAuthentication Auth { get; }
        public Core.CoreSpikeClient Core { get; }
        public Platform.PlatformSpikeClient Platform { get; }
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        public Dictionary<string, string> LastHeaders { get; } = [];
        public Fixture(string mode, string header, string credential, Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send)
        {
            Auth = new BoundaryAuthentication(mode, header, credential);
            http = new HttpClient(new Handler(async (request, token) =>
            {
                token.ThrowIfCancellationRequested(); Calls++;
                foreach (var field in request.Headers) LastHeaders[field.Key] = string.Join(" ", field.Value);
                Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
                return send(request, token);
            })) { Timeout = TimeSpan.FromSeconds(5) };
            // No Kiota default RetryHandler, RedirectHandler, logging middleware or global client.
            adapter = new HttpClientRequestAdapter(Auth, new JsonParseNodeFactory(), new JsonSerializationWriterFactory(), http)
            { BaseUrl = "https://gateway.test/api" };
            Core = new Core.CoreSpikeClient(adapter);
            Platform = new Platform.PlatformSpikeClient(adapter);
        }
        public void Dispose() { adapter.Dispose(); http.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class BoundaryAuthentication(string mode, string header, string credential) : IAuthenticationProvider
    {
        public int Calls { get; private set; }
        public Func<string>? ResolveCredential { get; set; }
        public Task AuthenticateRequestAsync(RequestInformation request, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.URI;
            if (uri.Scheme != "https" || uri.Host != "gateway.test" || uri.Port != 443 || uri.AbsolutePath.Contains("/ofrep/", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected request boundary.");
            var permitted = mode switch
            {
                "catalog" => uri.AbsolutePath == "/api/public/catalog",
                "session" => uri.AbsolutePath == "/api/public/session/portal",
                "checkout" => uri.AbsolutePath == "/api/public/checkout-sessions/session-42/confirm",
                "platform" => uri.AbsolutePath.StartsWith("/api/platform/", StringComparison.Ordinal),
                _ => uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal) && !uri.AbsolutePath.StartsWith("/api/public/", StringComparison.Ordinal) && !uri.AbsolutePath.StartsWith("/api/platform/", StringComparison.Ordinal)
            };
            if (!permitted) throw new InvalidOperationException("Unexpected credential boundary.");
            Calls++;
            request.Headers.Remove("Authorization"); request.Headers.Remove("X-Kaiten-Publishable-Key");
            var current = ResolveCredential is null ? credential : ResolveCredential();
            request.Headers.Add(header, header == "Authorization" ? "Bearer " + current : current);
            return Task.CompletedTask;
        }
    }
}
