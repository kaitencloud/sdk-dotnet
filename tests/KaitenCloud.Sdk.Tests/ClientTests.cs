// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using KaitenCloud.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace KaitenCloud.Sdk.Tests;

public sealed class ClientTests
{
    internal const string Validation = """
    {"valid":true,"status":"ACTIVE","evaluatedAt":"2026-10-30T12:00:00Z","instanceSlug":"ninja-osaka-prod",
     "licenseId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","licenseSlug":"starter",
     "startLicenseDate":"2026-10-01T00:00:00Z","endLicenseDate":"2027-10-01T00:00:00Z"}
    """;
    private static KaitenClientOptions Options(int retries = 0) => new()
    { BaseUri = new Uri("https://gateway.test/proxy/api"), MaxRetries = retries, RetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero };
    internal static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("https://host.test", "/api/instances/ninja-osaka-prod/license-validation")]
    [InlineData("https://host.test/edge/kaiten/", "/edge/kaiten/api/instances/ninja-osaka-prod/license-validation")]
    [InlineData("https://host.test/edge/api/", "/edge/api/instances/ninja-osaka-prod/license-validation")]
    public async Task BaseUrlPreservesProxyPrefix(string baseUrl, string path)
    {
        using var handler = new StubHandler((request, _) => { Assert.Equal(path, request.RequestUri!.AbsolutePath); return Task.FromResult(Json(Validation)); });
        using var http = new HttpClient(handler);
        using var client = new KaitenClient(http, new() { BaseUri = new Uri(baseUrl) });
        Assert.True((await client.ValidateInstanceLicenseAsync("ninja-osaka-prod")).Valid);
    }
    [Theory]
    [InlineData("valid")]
    [InlineData("status")]
    [InlineData("evaluatedAt")]
    [InlineData("instanceSlug")]
    [InlineData("licenseId")]
    [InlineData("licenseSlug")]
    [InlineData("startLicenseDate")]
    [InlineData("endLicenseDate")]
    public async Task EveryLicenseDecisionFieldIsRequired(string field)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Validation)!;
        fields.Remove(field);
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(fields)))));
        using var client = new KaitenClient(http, Options());
        await Assert.ThrowsAsync<KaitenProtocolException>(() => client.ValidateInstanceLicenseAsync("ninja-osaka-prod"));
    }
    [Theory]
    [InlineData("\"valid\":true", "\"valid\":null")]
    [InlineData("\"status\":\"ACTIVE\"", "\"status\":\"SUSPENDED\"")]
    [InlineData("\"status\":\"ACTIVE\"", "\"status\":1")]
    [InlineData("\"valid\":true", "\"valid\":false")]
    [InlineData("\"instanceSlug\":\"ninja-osaka-prod\"", "\"instanceSlug\":null")]
    public async Task MalformedOrContradictoryDecisionCannotAllow(string oldValue, string newValue)
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Json(Validation.Replace(oldValue, newValue)))));
        using var client = new KaitenClient(http, Options());
        await Assert.ThrowsAsync<KaitenProtocolException>(() => client.ValidateInstanceLicenseAsync("ninja-osaka-prod"));
    }
    [Fact]
    public async Task Http200InvalidTermIsARealDeniedDecision()
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Json(Validation.Replace("true", "false").Replace("ACTIVE", "EXPIRED")))));
        using var client = new KaitenClient(http, Options());
        var result = await client.ValidateInstanceLicenseAsync("ninja-osaka-prod");
        Assert.False(result.Valid); Assert.Equal(LicenseValidationStatus.Expired, result.Status);
    }
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    [InlineData(500)]
    public async Task ProblemCodesAndErrorIdsArePreservedWithoutLoggingDetails(int status)
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Json("""
        {"type":"about:blank","title":"not allowed","status":409,"code":"Specific.Code","errorId":"diagnostic-id","detail":"private-body","extra":42}
        """, (HttpStatusCode)status))));
        using var client = new KaitenClient(http, Options());
        var error = await Assert.ThrowsAsync<KaitenApiException>(() => client.ValidateInstanceLicenseAsync("ninja-osaka-prod"));
        Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.Equal("Specific.Code", error.Code);
        Assert.Equal("diagnostic-id", error.ErrorId); Assert.Equal(42, error.Problem!.Extensions!["extra"].GetInt32());
        Assert.DoesNotContain("private-body", error.ToString());
    }
    [Fact]
    public async Task PlainEnvoy401IsAnApiErrorNotAProtocolError()
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Json("Jwt is missing", HttpStatusCode.Unauthorized))));
        using var client = new KaitenClient(http, Options(3));
        var error = await Assert.ThrowsAsync<KaitenApiException>(() => client.ValidateInstanceLicenseAsync("ninja-osaka-prod"));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode); Assert.Null(error.Code);
    }
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(422)]
    public async Task AuthAndBusinessFailuresAreNeverRetried(int status)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json("{}", (HttpStatusCode)status)));
        using var http = new HttpClient(handler); using var client = new KaitenClient(http, Options(3));
        await Assert.ThrowsAsync<KaitenApiException>(() => client.ValidateInstanceLicenseAsync("ninja-osaka-prod"));
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task SafeGetRetriesAreBoundedAndOptIn()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable)));
        using var http = new HttpClient(handler); using var client = new KaitenClient(http, Options(2));
        await Assert.ThrowsAsync<KaitenApiException>(() => client.ValidateInstanceLicenseAsync("ninja-osaka-prod"));
        Assert.Equal(3, handler.Calls);
    }
    [Fact]
    public async Task UsageAndPutNeverReplay()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("private-route"));
        using var http = new HttpClient(handler); using var client = new KaitenClient(http, Options(2));
        await Assert.ThrowsAsync<KaitenNetworkException>(() => client.ReportNumericUsageAsync("ninja-osaka-prod", "monthly-orders", 1, UsageReportBehavior.Append));
        Assert.Equal(1, handler.Calls);
        var instance = new UpdateInstanceRequest { Name = "Name", Description = "", CustomerId = Guid.NewGuid(), LicenseId = Guid.NewGuid(), StartLicenseDate = DateTimeOffset.UtcNow, EndLicenseDate = DateTimeOffset.UtcNow.AddDays(1) };
        await Assert.ThrowsAsync<KaitenNetworkException>(() => client.UpdateInstanceAsync("ninja-osaka-prod", instance));
        Assert.Equal(2, handler.Calls);
    }
    [Fact]
    public async Task CallerCancellationAndDeadlineAreDifferentAndDoNotRetry()
    {
        using var handler = new StubHandler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json("{}"); });
        using var http = new HttpClient(handler);
        var options = Options(3); options.RequestTimeout = TimeSpan.FromMilliseconds(30);
        using var client = new KaitenClient(http, options);
        await Assert.ThrowsAsync<KaitenTimeoutException>(() => client.ValidateInstanceLicenseAsync("ninja-osaka-prod"));
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ValidateInstanceLicenseAsync("ninja-osaka-prod", caller.Token));
        Assert.Equal(2, handler.Calls);
    }
    [Fact]
    public async Task FileCredentialsRotatePerRequestAndCallerHttpClientStaysOwnedByCaller()
    {
        var file = Path.GetTempFileName();
        try
        {
            var tokens = new List<string?>();
            using var handler = new StubHandler((request, _) => { tokens.Add(request.Headers.Authorization?.Parameter); return Task.FromResult(Json(Validation)); });
            using var http = new HttpClient(handler);
            using (var client = new KaitenClient(http, Options(), new FileKaitenCredentialProvider(file)))
            {
                await File.WriteAllTextAsync(file, "test-token-one\n"); await client.ValidateInstanceLicenseAsync("ninja-osaka-prod");
                await File.WriteAllTextAsync(file, "test-token-two\n"); await client.ValidateInstanceLicenseAsync("ninja-osaka-prod");
            }
            Assert.Equal(new[] { "test-token-one", "test-token-two" }, tokens);
            await http.GetAsync("https://test.invalid"); Assert.False(handler.Disposed);
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public async Task ProviderFailureAndHeaderInjectionAreCredentialErrors()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(Validation)));
        using var http = new HttpClient(handler);
        var options = Options(); options.BearerToken = "secret\r\nheader: injected";
        using var client = new KaitenClient(http, options);
        await Assert.ThrowsAsync<KaitenCredentialException>(() => client.ValidateInstanceLicenseAsync("ninja-osaka-prod"));
        using var absent = new KaitenClient(http, Options(), new FileKaitenCredentialProvider(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));
        await Assert.ThrowsAsync<KaitenCredentialException>(() => absent.ValidateInstanceLicenseAsync("ninja-osaka-prod"));
        Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task DiCreatesTypedClientAndManagedWrapperIsDisposed()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(Validation)));
        var services = new ServiceCollection();
        services.AddKaiten(o => o.BaseUri = new Uri("https://gateway.test/api"));
        services.AddHttpClient("KaitenCloud.Sdk").ConfigurePrimaryHttpMessageHandler(() => handler);
        IKaitenClient client;
        using (var provider = services.BuildServiceProvider())
        {
            client = provider.GetRequiredService<IKaitenClient>();
            Assert.True((await client.ValidateInstanceLicenseAsync("ninja-osaka-prod")).Valid);
        }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ValidateInstanceLicenseAsync("ninja-osaka-prod"));
    }
    [Fact]
    public async Task RedactedDiagnosticsCorrelateTraceWithoutTokensRoutesOrUserData()
    {
        var logs = new CaptureLogger(); var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == KaitenClient.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        using var handler = new StubHandler((request, _) =>
        {
            Assert.NotNull(request.Headers.GetValues("traceparent").Single());
            var response = Json(Validation);
            response.Headers.Add("Kaiten-TraceId", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler); var options = Options(); options.BearerToken = "secret-bearer";
        using var client = new KaitenClient(http, options, logger: logs);
        await client.ValidateInstanceLicenseAsync("ninja-osaka-prod");
        var text = string.Join(" ", logs.Messages.Concat(activities.SelectMany(a => a.TagObjects.Select(t => t.Key + "=" + t.Value))));
        Assert.Contains("kaiten.trace_id", text);
        foreach (var privateValue in new[] { "ninja-osaka-prod", "secret-bearer", "gateway.test" }) Assert.DoesNotContain(privateValue, text);
    }
    [Fact]
    public async Task ResponseSizeAndMalformedJsonAreProtocolFailures()
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Json(new string('x', 2048)))));
        var options = Options(); options.MaxResponseBytes = 1024;
        using var client = new KaitenClient(http, options);
        await Assert.ThrowsAsync<KaitenProtocolException>(() => client.ValidateInstanceLicenseAsync("ninja-osaka-prod"));
    }
    [Theory]
    [InlineData("../secret")]
    [InlineData("ninja/osaka")]
    [InlineData("ab?token=secret")]
    [InlineData("a")]
    public async Task UnsafeSlugsAreRejectedBeforeSending(string slug)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(Validation)));
        using var http = new HttpClient(handler); using var client = new KaitenClient(http, Options());
        await Assert.ThrowsAsync<ArgumentException>(() => client.ValidateInstanceLicenseAsync(slug)); Assert.Equal(0, handler.Calls);
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    private int _calls;
    public int Calls => _calls;
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply { get; set; } = handler;
    public bool Disposed { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Interlocked.Increment(ref _calls); return Reply(request, cancellationToken); }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
internal sealed class CaptureLogger : ILogger<KaitenClient>
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, exception));
}
