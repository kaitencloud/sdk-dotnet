// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Hosting;
using OpenFeature.Model;
using OpenFeature.Providers.Ofrep;
using OpenFeature.Providers.Ofrep.DependencyInjection;
using Xunit;

namespace KaitenCloud.Sdk.Tests;

// Consumer integration tests. No SDK-owned provider, transport or evaluation API.
public sealed class OpenFeatureTests
{
    [Fact]
    public void SdkHasNoOpenFeatureDependencyOrEvaluationWrapper()
    {
        var assembly = typeof(IKaitenClient).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name => name.Name!.StartsWith("OpenFeature", StringComparison.Ordinal));
        Assert.DoesNotContain(assembly.GetTypes(), type => type.Name.Contains("Ofrep", StringComparison.OrdinalIgnoreCase) || type.Name.Contains("OpenFeature", StringComparison.Ordinal));
        foreach (var type in assembly.GetExportedTypes())
        {
            Assert.False(typeof(FeatureProvider).IsAssignableFrom(type));
            Assert.DoesNotContain(type.GetMethods(), method => !method.IsSpecialName && (method.Name.Contains("Evaluate", StringComparison.Ordinal) || method.Name.StartsWith("Resolve", StringComparison.Ordinal) || method.Name.StartsWith("GetBoolean", StringComparison.Ordinal)));
        }
    }

    [Theory]
    [InlineData("https://gateway.test/api", "/api/ofrep/v1/evaluate/flags/tracker")]
    [InlineData("https://gateway.test/edge/kaiten/api/", "/edge/kaiten/api/ofrep/v1/evaluate/flags/tracker")]
    public async Task ExplicitGatewayBearerAndPerRequestTrustedContextArePreserved(string endpoint, string path)
    {
        using var handler = new StubHandler(async (request, token) =>
        {
            Assert.Equal(path, request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("server-credential", request.Headers.Authorization.Parameter);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var context = document.RootElement.GetProperty("context");
            var customer = context.GetProperty("targetingKey").GetString()!;
            Assert.Equal(customer + "-prod", context.GetProperty("kaiten").GetProperty("instanceSlug").GetString());
            return Success(customer == "premium");
        });
        await using var fixture = await Fixture.Create(handler, endpoint);
        Assert.IsType<OfrepProvider>(fixture.Services.GetRequiredService<FeatureProvider>());
        var values = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => fixture.Flags.GetBooleanDetailsAsync("tracker", false, Context(i % 2 == 0 ? "starter" : "premium"))));
        Assert.All(values.Select((value, index) => (value, index)), pair =>
        {
            Assert.Equal(ErrorType.None, pair.value.ErrorType);
            Assert.NotEqual(Reason.Error, pair.value.Reason);
            Assert.Equal(pair.index % 2 != 0, pair.value.Value);
        });
        Assert.Equal(20, handler.Calls);
    }

    [Theory]
    [InlineData(401, ErrorType.ProviderNotReady)]
    [InlineData(403, ErrorType.ProviderNotReady)]
    [InlineData(404, ErrorType.FlagNotFound)]
    [InlineData(500, ErrorType.None)]
    [InlineData(503, ErrorType.None)]
    [InlineData(302, ErrorType.None)]
    public async Task ErrorsCannotAuthorizeEvenWhenFallbackIsTrueAndDoNotReplay(int status, ErrorType error)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        await using var fixture = await Fixture.Create(handler);
        var details = await fixture.Flags.GetBooleanDetailsAsync("tracker", true);
        Assert.Equal(error, details.ErrorType);
        AssertUnavailable(details);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("FLAG_NOT_FOUND", ErrorType.FlagNotFound)]
    [InlineData("INVALID_CONTEXT", ErrorType.InvalidContext)]
    public async Task OfficialBadRequestCodesRemainTyped(string code, ErrorType expected)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ClientTests.Json(JsonSerializer.Serialize(new { key = "tracker", errorCode = code }), HttpStatusCode.BadRequest)));
        await using var fixture = await Fixture.Create(handler);
        var details = await fixture.Flags.GetBooleanDetailsAsync("tracker", true);
        Assert.Equal(expected, details.ErrorType); AssertUnavailable(details);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"key":"tracker","value":"not-a-boolean"}""")]
    public async Task NativeParseAndTypeMismatchRemainUnavailable(string body)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ClientTests.Json(body)));
        await using var fixture = await Fixture.Create(handler);
        AssertUnavailable(await fixture.Flags.GetBooleanDetailsAsync("tracker", true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegitimateDefaultsAndDisabledDecisionsAreNotUnavailable(bool value)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ClientTests.Json(JsonSerializer.Serialize(new { key = "tracker", value, reason = "DEFAULT" }))));
        await using var fixture = await Fixture.Create(handler);
        var details = await fixture.Flags.GetBooleanDetailsAsync("tracker", !value);
        Assert.Equal(ErrorType.None, details.ErrorType); Assert.NotEqual(Reason.Error, details.Reason); Assert.Equal(value, details.Value);
    }

    [Fact]
    public async Task CallerCancellationTimeoutAndNetworkFailureCannotAuthorize()
    {
        using var handler = new StubHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Success(true); });
        await using var fixture = await Fixture.Create(handler, timeout: TimeSpan.FromMilliseconds(100));
        AssertUnavailable(await fixture.Flags.GetBooleanDetailsAsync("tracker", true));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        AssertUnavailable(await fixture.Flags.GetBooleanDetailsAsync("tracker", true, cancellationToken: canceled.Token));
        using var network = new StubHandler((_, _) => throw new HttpRequestException("network failure"));
        await using var failed = await Fixture.Create(network);
        AssertUnavailable(await failed.Flags.GetBooleanDetailsAsync("tracker", true));
        Assert.Equal(1, network.Calls);
    }

    [Fact]
    public async Task ProviderWideCooldownIsNotTenantCachingOrRetry()
    {
        var time = new ManualTimeProvider();
        using var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return Task.FromResult(response);
        });
        await using var fixture = await Fixture.Create(handler, timeProvider: time);
        AssertUnavailable(await fixture.Flags.GetBooleanDetailsAsync("tracker", true, Context("starter")));
        AssertUnavailable(await fixture.Flags.GetBooleanDetailsAsync("other", true, Context("premium")));
        Assert.Equal(1, handler.Calls);
        time.Now = time.Now.AddSeconds(6);
        AssertUnavailable(await fixture.Flags.GetBooleanDetailsAsync("tracker", true)); Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ConsumerNativeFactoryCanPreventRedirectingSensitiveContext()
    {
        var reservation = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/"); server.Start();
        var requests = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serving = Task.Run(async () =>
        {
            try
            {
                while (!deadline.IsCancellationRequested)
                {
                    var request = await server.GetContextAsync().WaitAsync(deadline.Token);
                    Interlocked.Increment(ref requests);
                    request.Response.StatusCode = 302;
                    request.Response.RedirectLocation = $"http://127.0.0.1:{port}/foreign-tenant";
                    request.Response.Close();
                }
            }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        });
        try
        {
            using var transport = new SocketsHttpHandler { AllowAutoRedirect = false };
            await using var fixture = await Fixture.Create(transport, $"http://127.0.0.1:{port}/api/");
            var details = await fixture.Flags.GetBooleanDetailsAsync("tracker", true, Context("starter"), cancellationToken: deadline.Token);
            AssertUnavailable(details);
            Assert.Equal(1, Volatile.Read(ref requests)); // Context/body was never sent to the redirect target.
        }
        finally { deadline.Cancel(); server.Stop(); await serving; }
    }

    private static EvaluationContext Context(string customer) => EvaluationContext.Builder().SetTargetingKey(customer)
        .Set("kaiten", new Value(new Structure(new Dictionary<string, Value> { ["instanceSlug"] = new(customer + "-prod") }))).Build();
    private static HttpResponseMessage Success(bool value) => ClientTests.Json(JsonSerializer.Serialize(new { key = "tracker", value, reason = "STATIC" }));
    private static void AssertUnavailable(FlagEvaluationDetails<bool> details) => Assert.True(details.ErrorType != ErrorType.None || details.Reason == Reason.Error);

    private sealed class Fixture(ServiceProvider services) : IAsyncDisposable
    {
        internal ServiceProvider Services => services;
        internal IFeatureClient Flags => services.GetRequiredService<IFeatureClient>();
        internal static async Task<Fixture> Create(HttpMessageHandler handler, string endpoint = "https://gateway.test/api/", TimeSpan? timeout = null, TimeProvider? timeProvider = null)
        {
            var services = new ServiceCollection();
            if (timeProvider is not null) services.AddSingleton(timeProvider);
            services.AddHttpClient("official-test").RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => handler);
            services.AddOpenFeature(builder => builder.AddOfrepProvider(options =>
            {
                options.BaseUrl = endpoint; options.Timeout = timeout ?? TimeSpan.FromSeconds(5); options.HttpClientName = "official-test";
                options.Headers["Authorization"] = "Bearer server-credential";
            }));
            var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IFeatureLifecycleManager>().EnsureInitializedAsync(); return new(provider);
        }
        public async ValueTask DisposeAsync() { await services.GetRequiredService<IFeatureLifecycleManager>().ShutdownAsync(); await services.DisposeAsync(); }
    }
    private sealed class ManualTimeProvider : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
