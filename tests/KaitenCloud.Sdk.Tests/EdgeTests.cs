// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;
namespace KaitenCloud.Sdk.Tests;

public sealed class EdgeTests
{
    [Fact]
    public async Task DeadlineBoundsAsyncCredentialProviderEvenIfItDoesNotObserveToken()
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException());
        using var http = new HttpClient(handler);
        using var client = new KaitenClient(http, new() { BaseUri = new Uri("https://test.invalid/api"), RequestTimeout = TimeSpan.FromMilliseconds(20) }, new PendingProvider());
        await Assert.ThrowsAsync<KaitenTimeoutException>(() => client.ValidateInstanceLicenseAsync("ninja-osaka-prod").WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task RetryAfterBeyondBoundDoesNotReplayEarlierThanServerAdvice()
    {
        using var handler = new StubHandler((_, _) =>
        {
            var response = ClientTests.Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new KaitenClient(http, new() { BaseUri = new Uri("https://test.invalid/api"), MaxRetries = 3, MaxRetryDelay = TimeSpan.FromSeconds(1) });
        await Assert.ThrowsAsync<KaitenApiException>(() => client.ValidateInstanceLicenseAsync("ninja-osaka-prod"));
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task CredentialProviderTakesPrecedenceAndRotatesBetweenRetries()
    {
        var seen = new List<string?>();
        using var handler = new StubHandler((request, _) =>
        {
            seen.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(seen.Count == 1 ? ClientTests.Json("{}", HttpStatusCode.BadGateway) : ClientTests.Json(ClientTests.Validation));
        });
        using var http = new HttpClient(handler);
        using var client = new KaitenClient(http, new() { BaseUri = new Uri("https://test.invalid/api"), BearerToken = "unused-test-token", MaxRetries = 1, RetryDelay = TimeSpan.Zero }, new RotatingProvider());
        Assert.True((await client.ValidateInstanceLicenseAsync("ninja-osaka-prod")).Valid);
        Assert.Equal(new[] { "test-one", "test-two" }, seen);
    }
    [Fact]
    public async Task PrecanceledRequestNeverAcquiresCredentialsOrSends()
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException());
        using var http = new HttpClient(handler);
        using var client = new KaitenClient(http, new() { BaseUri = new Uri("https://test.invalid/api") });
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ValidateInstanceLicenseAsync("ninja-osaka-prod", canceled.Token));
        Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task OptionalBulkMetadataAndExtensionsAreNotDiscarded()
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(ClientTests.Json("""
        {"flags":[{"key":"some-flag","value":0,"future":"additive","metadata":{"custom":42}}],"metadata":{"bulk":"kept"},"futureBulk":true}
        """))));
        using var client = new KaitenClient(http, new() { BaseUri = new Uri("https://test.invalid/api") });
        var result = await client.EvaluateAllFlagsAsync(new Dictionary<string, object?>());
        Assert.Equal("kept", result.Metadata!["bulk"].GetString()); Assert.True(result.Extensions!["futureBulk"].GetBoolean());
        Assert.Equal(0, result.Flags[0].GetValue<int>()); Assert.Equal("additive", result.Flags[0].Extensions!["future"].GetString());
    }
    [Theory]
    [InlineData("""{"flags":null}""")]
    [InlineData("""{"flags":[null]}""")]
    [InlineData("""{"flags":[{"key":"some-flag","value":true},{"key":"some-flag","value":false}]}""")]
    public async Task InvalidBulkCannotInventSecurityDecisions(string body)
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(ClientTests.Json(body))));
        using var client = new KaitenClient(http, new() { BaseUri = new Uri("https://test.invalid/api") });
        await Assert.ThrowsAsync<KaitenProtocolException>(() => client.EvaluateAllFlagsAsync(new Dictionary<string, object?>()));
    }
    [Fact]
    public async Task NullEntitlementDecisionIsProtocolFailure()
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(ClientTests.Json("""
        {"entitlementId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","entitlementSlug":"delivery-tracking",
         "licenseId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","licenseSlug":"starter","value":null}
        """))));
        using var client = new KaitenClient(http, new() { BaseUri = new Uri("https://test.invalid/api") });
        await Assert.ThrowsAsync<KaitenProtocolException>(() => client.GetInstanceEntitlementUsageAsync("ninja-osaka-prod", "delivery-tracking"));
    }
    [Fact]
    public async Task NullArrayIsOnlyNormalizedForDocumentedEmptyUsageCollection()
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(ClientTests.Json("null"))));
        using var client = new KaitenClient(http, new() { BaseUri = new Uri("https://test.invalid/api") });
        Assert.Empty(await client.GetAllInstanceEntitlementUsageAsync("ninja-osaka-prod"));
        await Assert.ThrowsAsync<KaitenProtocolException>(() => client.ValidateInstanceLicenseAsync("ninja-osaka-prod"));
    }
    [Fact]
    public async Task NumericGrantWriteDropsObservationCountAndPreservesOverage()
    {
        using var handler = new StubHandler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(10, body.RootElement.GetProperty("limitCapExceededOveragePercent").GetInt32());
            Assert.False(body.RootElement.GetProperty("value").TryGetProperty("event_count", out _));
            Assert.Equal("number", body.RootElement.GetProperty("value").GetProperty("type").GetString());
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var http = new HttpClient(handler);
        using var client = new KaitenClient(http, new() { BaseUri = new Uri("https://test.invalid/api") });
        await client.UpdateLicenseEntitlementAsync("starter", "monthly-orders", new()
        { Value = new NumberEntitlementValue { Value = 20, EventCount = 3 }, LimitCapExceededOveragePercent = 10 });
    }
    [Fact]
    public async Task TrailingNewlineSlugIsNotAcceptedByRegexEndAnchor()
    {
        using var http = new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException()));
        using var client = new KaitenClient(http, new() { BaseUri = new Uri("https://test.invalid/api") });
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetInstanceAsync("ninja-osaka-prod\n"));
    }
    [Fact]
    public void DefaultHttpAuthIsRejectedWithoutMutatingCallerClient()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-default-token");
        Assert.Throws<ArgumentException>(() => new KaitenClient(http, new() { BaseUri = new Uri("https://test.invalid/api") }));
        Assert.Equal("test-default-token", http.DefaultRequestHeaders.Authorization.Parameter);
    }
    [Fact]
    public async Task RedirectIsAnErrorAndCannotTurnIntoAuthorizedFallback()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ClientTests.Json("", HttpStatusCode.Redirect)));
        using var http = new HttpClient(handler);
        using var client = new KaitenClient(http, new() { BaseUri = new Uri("https://test.invalid/api"), MaxRetries = 3 });
        var error = await Assert.ThrowsAsync<KaitenApiException>(() => client.ValidateInstanceLicenseAsync("ninja-osaka-prod"));
        Assert.Equal(HttpStatusCode.Redirect, error.StatusCode); Assert.Equal(1, handler.Calls);
    }
}
internal sealed class PendingProvider : IKaitenCredentialProvider
{
    private readonly TaskCompletionSource<string?> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask<string?> GetBearerTokenAsync(CancellationToken cancellationToken = default) => new(_pending.Task);
}
internal sealed class RotatingProvider : IKaitenCredentialProvider
{
    private int _count;
    public ValueTask<string?> GetBearerTokenAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<string?>(++_count == 1 ? "test-one" : "test-two");
}
