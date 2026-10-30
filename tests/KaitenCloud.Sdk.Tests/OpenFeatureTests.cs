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

    public static IEnumerable<object[]> MalformedBadRequestCases()
    {
        var bodies = new[]
        {
            "",
            "{}",
            """{"key":"tracker"}""",
            """{"key":"tracker","value":true}""",
            """{"key":"tracker","value":true,"errorCode":"UNKNOWN_FUTURE_ERROR"}""",
            """{"key":"tracker","errorCode":"UNKNOWN_FUTURE_ERROR"}""",
            """{"key":"tracker","errorCode":null}""",
            """{"key":"tracker","errorCode":""}""",
            """{"key":"tracker","errorCode":"   "}""",
            """{"key":"other-flag","errorCode":"INVALID_CONTEXT"}""",
            """{"key":"","errorCode":"INVALID_CONTEXT"}""",
            """{"key":null,"errorCode":"INVALID_CONTEXT"}""",
            """{"key":"tracker","key":"tracker","errorCode":"INVALID_CONTEXT"}""",
            """{"key":"tracker","errorCode":"INVALID_CONTEXT","errorCode":"FLAG_NOT_FOUND"}""",
            """{"key":"tracker","value":true,"errorCode":"FLAG_NOT_FOUND"}""",
            """{"key":"tracker","value":null,"errorCode":"INVALID_CONTEXT"}"""
        };
        foreach (var fallback in new[] { false, true })
            foreach (var body in bodies) yield return new object[] { body, fallback };
    }

    [Theory]
    [MemberData(nameof(MalformedBadRequestCases))]
    public async Task BadRequestMustBeAStrictKnownErrorEnvelope(string json, bool fallback)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ClientTests.Json(json, HttpStatusCode.BadRequest)));
        await using var fixture = await Fixture.Create(handler);
        var details = await fixture.Flags.GetBooleanDetailsAsync("tracker", fallback);
        AssertUnavailable(details);
        Assert.Equal(fallback, details.Value); // An injected success value must not survive a malformed 400 envelope.
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("FLAG_NOT_FOUND", ErrorType.FlagNotFound, false)]
    [InlineData("FLAG_NOT_FOUND", ErrorType.FlagNotFound, true)]
    [InlineData("INVALID_CONTEXT", ErrorType.InvalidContext, false)]
    [InlineData("INVALID_CONTEXT", ErrorType.InvalidContext, true)]
    public async Task ValidBadRequestPreservesOfficialErrorType(string code, ErrorType expected, bool fallback)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ClientTests.Json(
            "{\"key\":\"tracker\",\"errorCode\":\"" + code + "\",\"errorDetails\":\"unavailable\"}", HttpStatusCode.BadRequest)));
        await using var fixture = await Fixture.Create(handler);
        var details = await fixture.Flags.GetBooleanDetailsAsync("tracker", fallback);
        Assert.Equal(expected, details.ErrorType);
        Assert.Equal(fallback, details.Value);
        Assert.Equal("unavailable", details.ErrorMessage);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BadRequestBodyLimitAppliesWithAndWithoutContentLength(bool streamed, bool fallback)
    {
        var body = "{\"key\":\"tracker\",\"errorCode\":\"INVALID_CONTEXT\",\"metadata\":{\"padding\":\"" + new string('x', 2048) + "\"}}";
        using var handler = new StubHandler((_, _) =>
        {
            var response = ClientTests.Json(body, HttpStatusCode.BadRequest);
            if (streamed) response.Content = new StreamContent(new NonSeekableBodyStream(System.Text.Encoding.UTF8.GetBytes(body)));
            return Task.FromResult(response);
        });
        await using var fixture = await Fixture.Create(handler, maxResponseBytes: 1024);
        AssertUnavailable(await fixture.Flags.GetBooleanDetailsAsync("tracker", fallback));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BadRequestDepthAndCancellationCannotBecomeSuccess(bool fallback)
    {
        var deep = "{\"key\":\"tracker\",\"value\":true,\"metadata\":{\"deep\":" + new string('[', 65) + "0" + new string(']', 65) + "}}";
        using var handler = new StubHandler((_, _) => Task.FromResult(ClientTests.Json(deep, HttpStatusCode.BadRequest)));
        await using var fixture = await Fixture.Create(handler, timeout: TimeSpan.FromMilliseconds(100));
        AssertUnavailable(await fixture.Flags.GetBooleanDetailsAsync("tracker", fallback));
        handler.Reply = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StreamContent(new NonSeekableBodyStream([], stall: true)) });
        AssertUnavailable(await fixture.Flags.GetBooleanDetailsAsync("tracker", fallback).WaitAsync(TimeSpan.FromSeconds(3)));
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        AssertUnavailable(await fixture.Flags.GetBooleanDetailsAsync("tracker", fallback, cancellationToken: canceled.Token));
        Assert.Equal(3, handler.Calls);
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

    [Fact]
    public async Task OfficialFeatureClientLoggingAndActivitiesDoNotExposeRawTransportExceptions()
    {
        var logs = new ListLoggerProvider();
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "OpenFeature" || s.Name == KaitenOpenFeatureServiceCollectionExtensions.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("private-transport-url sensitive-context server-credential"));
        await using var fixture = await Fixture.Create(handler, logs: logs);
        var logger = fixture.Services.GetRequiredService<ILoggerFactory>().CreateLogger<FeatureClient>();
        var client = fixture.Services.GetRequiredService<Api>().GetClient(logger: logger);
        var details = await client.GetBooleanDetailsAsync("private-flag", false);
        AssertUnavailable(details);
        handler.Reply = (_, _) => Task.FromResult(ClientTests.Json("{}", HttpStatusCode.NotFound));
        Assert.Equal(ErrorType.FlagNotFound, (await client.GetBooleanDetailsAsync("private-flag", false)).ErrorType);
        Assert.Contains(activities, a => a.Source.Name == "OpenFeature" && a.OperationName == "feature_flag.evaluation");
        var text = string.Join(" ", logs.Messages.Concat(activities.SelectMany(a => a.TagObjects.Select(t => t.Key + "=" + t.Value))).Append(details.ErrorMessage));
        foreach (var secret in new[] { "private-transport-url", "sensitive-context", "server-credential" }) Assert.DoesNotContain(secret, text);
        Assert.False(logger.IsEnabled(LogLevel.Error)); // Actual category is OpenFeature.FeatureClient, not its source file name.
        Assert.DoesNotContain("private-flag", string.Join(" ", logs.Messages));
    }

    [Theory]
    [InlineData("network")]
    [InlineData("body-read")]
    [InlineData("cancellation")]
    [InlineData("unexpected-handler")]
    public async Task TransportFailureKindsCannotPropagatePrivateExceptionMessages(string kind)
    {
        using var handler = new StubHandler((_, token) => throw (kind switch
        {
            "network" => new HttpRequestException("private-transport-marker"),
            "body-read" => new IOException("private-transport-marker"),
            "cancellation" => new OperationCanceledException("private-transport-marker", token),
            _ => new InvalidOperationException("private-transport-marker")
        }));
        await using var fixture = await Fixture.Create(handler);
        var details = await fixture.Flags.GetBooleanDetailsAsync("tracker", true);
        AssertUnavailable(details);
        Assert.DoesNotContain("private-transport-marker", details.ErrorMessage ?? "");
        Assert.Equal(1, handler.Calls);
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
            ListLoggerProvider? logs = null, int? maxResponseBytes = null)
        {
            var services = new ServiceCollection();
            if (credentials is not null) services.AddSingleton(credentials);
            if (timeProvider is not null) services.AddSingleton(timeProvider);
            if (logs is not null) services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            services.AddKaitenOpenFeature(o => { o.BaseUri = new Uri(uri); o.BearerToken = "server-credential"; if (timeout.HasValue) o.RequestTimeout = timeout.Value; if (maxResponseBytes.HasValue) o.MaxResponseBytes = maxResponseBytes.Value; });
            services.AddHttpClient(KaitenOpenFeatureServiceCollectionExtensions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
            var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IFeatureLifecycleManager>().EnsureInitializedAsync();
            return new(provider);
        }
        public async ValueTask DisposeAsync()
        { await Services.GetRequiredService<IFeatureLifecycleManager>().ShutdownAsync(); await Services.DisposeAsync(); }
    }
    private sealed class NonSeekableBodyStream(byte[] bytes, bool stall = false) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (stall) await Task.Delay(Timeout.Infinite, token);
            token.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
