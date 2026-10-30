// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using KaitenCloud.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Hosting;
using OpenFeature.Model;
using OpenFeature.Providers.Ofrep;
using Xunit;

namespace KaitenCloud.Sdk.Tests;

public sealed class OpenFeatureTests
{
    [Fact]
    public void PublicSdkHasNoEvaluationMethodsModelsOrBespokeProvider()
    {
        var types = typeof(IKaitenClient).Assembly.GetExportedTypes();
        Assert.DoesNotContain(types, t => t.Name.Contains("Evaluation", StringComparison.Ordinal));
        Assert.DoesNotContain(types, t => typeof(FeatureProvider).IsAssignableFrom(t));
        foreach (var type in types)
            Assert.DoesNotContain(type.GetMethods(), m => !m.IsSpecialName && (m.Name.Contains("Evaluate", StringComparison.Ordinal) ||
                m.Name.StartsWith("Resolve", StringComparison.Ordinal) || m.Name.StartsWith("GetBoolean", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("https://gateway.test", "/api/ofrep/v1/evaluate/flags/tracker")]
    [InlineData("https://gateway.test/edge/kaiten/", "/edge/kaiten/api/ofrep/v1/evaluate/flags/tracker")]
    [InlineData("https://gateway.test/edge/api", "/edge/api/ofrep/v1/evaluate/flags/tracker")]
    public async Task OfficialProviderUsesApiPrefixNestedContextAndNoTenantCache(string uri, string path)
    {
        using var handler = new StubHandler(async (request, ct) =>
        {
            Assert.Equal(path, request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("server-credential", request.Headers.Authorization.Parameter);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var context = document.RootElement.GetProperty("context");
            var customer = context.GetProperty("targetingKey").GetString();
            Assert.Equal(customer + "-prod", context.GetProperty("kaiten").GetProperty("instanceSlug").GetString());
            Assert.Equal("opaque-user", context.GetProperty("userId").GetString());
            Assert.Equal(12, context.GetProperty("orderCount").GetInt32());
            return Success(customer == "sakura-tokyo");
        });
        await using var fixture = await Fixture.Create(handler, uri);
        Assert.IsType<OfrepProvider>(fixture.Services.GetRequiredService<FeatureProvider>());
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => fixture.Flags.GetBooleanDetailsAsync(
            "tracker", false, Context(i % 2 == 0 ? "ninja-osaka" : "sakura-tokyo"))));
        Assert.Equal(20, handler.Calls);
        for (var i = 0; i < results.Length; i++)
        {
            Assert.Equal(ErrorType.None, results[i].ErrorType);
            Assert.Equal(i % 2 != 0, results[i].Value);
        }
        Assert.False((await fixture.Flags.GetBooleanDetailsAsync("tracker", true, Context("ninja-osaka"))).Value);
        Assert.Equal(21, handler.Calls); // No cached previous true/default or shared tenant context.
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"key":"tracker"}""")]
    [InlineData("""{"key":"tracker","value":null}""")]
    [InlineData("""{"key":"another-flag","value":true}""")]
    [InlineData("""{"key":null,"value":true}""")]
    [InlineData("""{"key":"tracker","value":"not-a-boolean"}""")]
    [InlineData("""{"key":"tracker","value":true,"errorCode":"UNKNOWN_FUTURE_ERROR"}""")]
    [InlineData("""{"key":"tracker","errorCode":"GENERAL_ERROR"}""")]
    [InlineData("""{"key":"tracker","value":true,"value":false}""")]
    public async Task MalformedSuccessNeverBecomesDefaultOrAuthorization(string json)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ClientTests.Json(json)));
        await using var fixture = await Fixture.Create(handler);
        var details = await fixture.Flags.GetBooleanDetailsAsync("tracker", true);
        AssertUnavailable(details);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidServerDefaultRemainsAvailable(bool value)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ClientTests.Json(
            "{\"key\":\"tracker\",\"value\":" + (value ? "true" : "false") + ",\"reason\":\"DEFAULT\"}")));
        await using var fixture = await Fixture.Create(handler);
        var details = await fixture.Flags.GetBooleanDetailsAsync("tracker", !value);
        Assert.Equal(ErrorType.None, details.ErrorType); Assert.Equal(Reason.Default, details.Reason); Assert.Equal(value, details.Value);
    }

    [Fact]
    public async Task FileCredentialsRotateOnEachOfficialRequestAndOverrideStaticToken()
    {
        var path = Path.Combine(Path.GetTempPath(), "kaiten-ofrep-" + Guid.NewGuid());
        try
        {
            await File.WriteAllTextAsync(path, "first-private-token\n");
            var tokens = new List<string?>();
            using var handler = new StubHandler((request, _) =>
            { tokens.Add(request.Headers.Authorization?.Parameter); return Task.FromResult(Success(true)); });
            await using var fixture = await Fixture.Create(handler, credentials: new FileKaitenCredentialProvider(path));
            Assert.Equal(ErrorType.None, (await fixture.Flags.GetBooleanDetailsAsync("tracker", false)).ErrorType);
            var replacement = path + ".new";
            await File.WriteAllTextAsync(replacement, "second-private-token\n");
            File.Move(replacement, path, true);
            Assert.Equal(ErrorType.None, (await fixture.Flags.GetBooleanDetailsAsync("tracker", false)).ErrorType);
            Assert.Equal(new[] { "first-private-token", "second-private-token" }, tokens);
            File.Delete(path);
            AssertUnavailable(await fixture.Flags.GetBooleanDetailsAsync("tracker", true));
            Assert.Equal(2, handler.Calls); // No anonymous fallback when the credential source fails.
        }
        finally { File.Delete(path); File.Delete(path + ".new"); }
    }

    [Theory]
    [InlineData(401, ErrorType.ProviderNotReady)]
    [InlineData(403, ErrorType.ProviderNotReady)]
    [InlineData(404, ErrorType.FlagNotFound)]
    // Native 0.1.7 general_error mapping bug: ErrorType.None, but Reason.ERROR.
    [InlineData(500, ErrorType.None)]
    [InlineData(503, ErrorType.None)]
    [InlineData(302, ErrorType.None)]
    public async Task ErrorsRemainUnavailableEvenWithTrueFallbackAndNeverRetry(int status, ErrorType expected)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ClientTests.Json("{}", (HttpStatusCode)status)));
        await using var fixture = await Fixture.Create(handler);
        var details = await fixture.Flags.GetBooleanDetailsAsync("tracker", true);
        Assert.Equal(expected, details.ErrorType);
        AssertUnavailable(details);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ServerChangeAppearsOnNextRequestAndDisabledTrueIsNotHardcodedFalse()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Success(false)));
        await using var fixture = await Fixture.Create(handler);
        var first = await fixture.Flags.GetBooleanDetailsAsync("tracker", true);
        Assert.Equal(ErrorType.None, first.ErrorType); Assert.False(first.Value);
        handler.Reply = (_, _) => Task.FromResult(ClientTests.Json("""{"key":"tracker","value":true,"reason":"DISABLED"}"""));
        var next = await fixture.Flags.GetBooleanDetailsAsync("tracker", false);
        Assert.Equal(ErrorType.None, next.ErrorType); Assert.True(next.Value); Assert.Equal(Reason.Disabled, next.Reason);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task OfficialRateLimitCooldownIsProviderWideNotFlagCachingOrRetry()
    {
        var clock = new ManualTimeProvider();
        using var handler = new StubHandler((_, _) =>
        {
            var response = ClientTests.Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(5));
            return Task.FromResult(response);
        });
        await using var fixture = await Fixture.Create(handler, timeProvider: clock);
        AssertUnavailable(await fixture.Flags.GetBooleanDetailsAsync("tracker", true, Context("ninja-osaka")));
        handler.Reply = (_, _) => Task.FromResult(Success(true));
        AssertUnavailable(await fixture.Flags.GetBooleanDetailsAsync("another-flag", true, Context("sakura-tokyo")));
        Assert.Equal(1, handler.Calls);
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.Equal(ErrorType.None, (await fixture.Flags.GetBooleanDetailsAsync("tracker", false)).ErrorType);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ProviderDeadlineBoundsUncooperativeCredentialsAndCallerCancellationDoesNotAllow()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Success(true)));
        await using var fixture = await Fixture.Create(handler, timeout: TimeSpan.FromMilliseconds(100), credentials: new PendingProvider());
        var details = await fixture.Flags.GetBooleanDetailsAsync("tracker", true).WaitAsync(TimeSpan.FromSeconds(3));
        AssertUnavailable(details); Assert.Equal(0, handler.Calls);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        AssertUnavailable(await fixture.Flags.GetBooleanDetailsAsync("tracker", true, cancellationToken: canceled.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task HttpTimeoutAndNetworkFailureProduceErrorDetailsWithoutReplay()
    {
        using var handler = new StubHandler(async (_, token) =>
        { await Task.Delay(Timeout.Infinite, token); return Success(true); });
        await using var fixture = await Fixture.Create(handler, timeout: TimeSpan.FromMilliseconds(100));
        AssertUnavailable(await fixture.Flags.GetBooleanDetailsAsync("tracker", true));
        Assert.Equal(1, handler.Calls);
        handler.Reply = (_, _) => throw new HttpRequestException("private URL and context");
        AssertUnavailable(await fixture.Flags.GetBooleanDetailsAsync("tracker", true));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SafeTransportTraceparentAndLogsDoNotExposeUpstreamErrorsOrRequestData()
    {
        var logs = new ListLoggerProvider(); var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == KaitenOpenFeatureServiceCollectionExtensions.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        using var handler = new StubHandler((request, _) =>
        {
            var response = ClientTests.Json("""{"key":"private-flag","value":true,"reason":"STATIC"}""");
            response.Headers.Add("Kaiten-TraceId", ActivityTraceId.CreateFromString(request.Headers.GetValues("traceparent").Single().AsSpan(3, 32)).ToHexString());
            return Task.FromResult(response);
        });
        await using var fixture = await Fixture.Create(handler, logs: logs);
        Assert.Equal(ErrorType.None, (await fixture.Flags.GetBooleanDetailsAsync("private-flag", false, Context("ninja-osaka"))).ErrorType);
        handler.Reply = (_, _) => throw new HttpRequestException("sensitive-error-body server-credential private-url");
        await fixture.Flags.GetBooleanDetailsAsync("private-flag", false);
        var successful = activities.First();
        Assert.Equal(successful.TraceId.ToHexString(), successful.GetTagItem("kaiten.trace_id"));
        var text = string.Join(" ", logs.Messages.Concat(activities.SelectMany(a => a.TagObjects.Select(t => t.Key + "=" + t.Value))));
        Assert.Contains("Kaiten OFREP transport", text);
        foreach (var secret in new[] { "server-credential", "private-flag", "opaque-user", "sensitive-error-body", "private-url", "gateway.test", "ninja-osaka" })
            Assert.DoesNotContain(secret, text);
    }

    internal static EvaluationContext Context(string customer) => EvaluationContext.Builder().SetTargetingKey(customer)
        .Set("kaiten", new Value(new Structure(new Dictionary<string, Value> { ["instanceSlug"] = new(customer + "-prod") })))
        .Set("userId", "opaque-user").Set("orderCount", 12).Build();
    private static HttpResponseMessage Success(bool value) => ClientTests.Json(value
        ? """{"key":"tracker","value":true,"reason":"STATIC"}""" : """{"key":"tracker","value":false,"reason":"STATIC"}""");
    private static void AssertUnavailable(FlagEvaluationDetails<bool> details)
    { Assert.True(details.ErrorType != ErrorType.None || details.Reason == Reason.Error); Assert.Equal(Reason.Error, details.Reason); }

    private sealed class Fixture(ServiceProvider services) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;
        public IFeatureClient Flags => Services.GetRequiredService<IFeatureClient>();
        public static async Task<Fixture> Create(StubHandler handler, string uri = "https://gateway.test/api",
            IKaitenCredentialProvider? credentials = null, TimeSpan? timeout = null, TimeProvider? timeProvider = null,
            ListLoggerProvider? logs = null)
        {
            var services = new ServiceCollection();
            if (credentials is not null) services.AddSingleton(credentials);
            if (timeProvider is not null) services.AddSingleton(timeProvider);
            if (logs is not null) services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            services.AddKaitenOpenFeature(o => { o.BaseUri = new Uri(uri); o.BearerToken = "server-credential"; if (timeout.HasValue) o.RequestTimeout = timeout.Value; });
            services.AddHttpClient(KaitenOpenFeatureServiceCollectionExtensions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
            var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IFeatureLifecycleManager>().EnsureInitializedAsync();
            return new(provider);
        }
        public async ValueTask DisposeAsync()
        { await Services.GetRequiredService<IFeatureLifecycleManager>().ShutdownAsync(); await Services.DisposeAsync(); }
    }
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class ListLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new ListLogger(Messages);
        public void Dispose() { }
        private sealed class ListLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, exception) + exception);
        }
    }
}
