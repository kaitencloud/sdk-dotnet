// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using KaitenCloud.Sdk;
using Microsoft.Extensions.DependencyInjection;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Hosting;
using OpenFeature.Model;
using OpenFeature.Providers.Ofrep.DependencyInjection;

// Deliberately an explicit executable: missing prerequisites FAIL, never silently skip real coverage.
try
{
    await Integration.RunAsync();
    Console.WriteLine("PASS: real gateway SDK integration, snapshots restored, trace correlated.");
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL: {error.GetType().Name}. {error.Message}");
    Environment.ExitCode = 1;
}

internal static class Integration
{
    private static readonly JsonSerializerOptions JsonOptions = KaitenJson.CreateOptions();
    private static readonly EvaluationContext NinjaContext = Context("ninja-osaka", "ninja-osaka-prod");
    private static readonly EvaluationContext SakuraContext = Context("sakura-tokyo", "sakura-tokyo-prod");
    private const string Ninja = "ninja-osaka-prod";
    private const string Sakura = "sakura-tokyo-prod";
    private const string Tracker = "delivery-tracking-enabled";

    public static async Task RunAsync()
    {
        var baseUrl = Required("KAITEN_BASE_URL");
        var tokenDir = Required("KAITEN_TEST_TOKEN_DIR");
        var jaegerUrl = Required("KAITEN_JAEGER_URL");
        Check(Required("KAITEN_TEST_MUTATIONS") == "1", "Authorize exclusively owned temporary Core mutation probes with KAITEN_TEST_MUTATIONS=1.");
        Check(Required("KAITEN_TEST_ENVIRONMENT").StartsWith("kaiten-sdk-ofrep-", StringComparison.Ordinal), "Use a separate owned environment, never the live Demo.");
        await using var featureServices = await Features(baseUrl, new FileKaitenCredentialProvider(Path.Combine(tokenDir, "runtime-token")));
        var flags = featureServices.GetRequiredService<IFeatureClient>();
        foreach (var name in new[] { "runtime-token", "presenter-token", "under-scoped-token", "foreign-token" })
            Check(File.Exists(Path.Combine(tokenDir, name)), "Required private token files are absent.");
        var snapshotDir = Environment.GetEnvironmentVariable("KAITEN_TEST_SNAPSHOT_DIR")
            ?? Path.Combine(".local", "integration", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(snapshotDir);
        else Directory.CreateDirectory(snapshotDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var runtime = Client(baseUrl, tokenDir, "runtime-token");
        using var presenter = Client(baseUrl, tokenDir, "presenter-token");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        var ninja = await runtime.GetInstanceAsync(Ninja, token);
        var sakura = await runtime.GetInstanceAsync(Sakura, token);
        Check(ninja.CustomerSlug == "ninja-osaka" && ninja.LicenseSlug == "starter" &&
              sakura.CustomerSlug == "sakura-tokyo" && sakura.LicenseSlug == "premium-v2",
              "Integration requires the unchanged Sushi primary-instance seed; do not test unrelated or modified tenants.");
        var customer = await runtime.GetCustomerAsync("ninja-osaka", token);
        Check(customer.Id == ninja.CustomerId, "Customer/instance identity mismatch.");
        var standard = await runtime.GetLicenseAsync("standard-v2", token);
        var grants = await runtime.GetLicenseEntitlementsAsync("starter", cancellationToken: token);
        Check(grants.Items.Count > 0 && !grants.HasMore, "Seed grant page was not complete.");
        var starterGrant = await runtime.GetLicenseEntitlementAsync("starter", "delivery-tracking", token);
        var flag = await runtime.GetFeatureFlagAsync(Tracker, token);
        var usage = await runtime.GetInstanceEntitlementUsageAsync(Ninja, "monthly-orders", token);
        var originalNumber = usage.Value as NumberEntitlementValue ?? throw new InvalidOperationException("Expected numeric SUM usage.");
        var originalCount = originalNumber.EventCount ?? 1;
        Check(originalCount is >= 1 and <= 100, "Exact rollback requires a seeded SUM observation count from 1 to 100.");
        var allUsage = await runtime.GetAllInstanceEntitlementUsageAsync(Ninja, token);
        Check(allUsage.Count >= 6, "Seed runtime entitlement usage missing.");
        Check(allUsage.Any(u => u.Value is ObjectEntitlementValue), "Object entitlements did not deserialize.");
        Check(!((BooleanEntitlementValue)starterGrant.Value).Value, "Starter tracker baseline changed.");
        foreach (var key in new[] { Tracker, "analytics-dashboard-enabled", "new-ordering-flow", "random-discount" })
            Check(!Unavailable(await flags.GetBooleanDetailsAsync(key, false, NinjaContext, cancellationToken: token)), "Seed boolean flag unavailable through OpenFeature.");
        foreach (var key in new[] { "promo-banner", "discount-campaign", "loyalty-offer" })
            Check(!Unavailable(await flags.GetObjectDetailsAsync(key, new Value(new Structure(new Dictionary<string, Value>())), NinjaContext, cancellationToken: token)), "Seed object flag unavailable through OpenFeature.");
        Check(!await Tracking(flags, NinjaContext, token) && await Tracking(flags, SakuraContext, token), "Different licensed businesses were not represented.");
        Check((await runtime.GetInstanceAsync(Ninja, token)).LicenseTerm!.Status == LicenseTermStatus.Active, "Seed term must initially be active.");
        var parallelTracking = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Tracking(flags, i % 2 == 0 ? NinjaContext : SakuraContext, token)));
        Check(parallelTracking.Select((value, i) => value == (i % 2 != 0)).All(value => value), "Concurrent real tenant contexts crossed.");
        await Snapshot(snapshotDir, "instance.json", ninja.ToUpdateRequest());
        await Snapshot(snapshotDir, "flag.json", flag.ToUpdateRequest());
        await Snapshot(snapshotDir, "grant.json", starterGrant.ToUpdateRequest());
        await Snapshot(snapshotDir, "usage.json", new { usage, originalValue = originalNumber.Value, originalEventCount = originalNumber.EventCount });
        var completed = new List<string> { "typed customer/instance/license/grant/number/boolean/object reads", "official OpenFeature per-request real flags and primary contexts" };
        var instanceChanged = false; var flagChanged = false; var grantChanged = false; var usageChanged = false;
        try
        {
            Console.WriteLine("CHECK: gateway authentication and organization isolation");
            using var anonymous = KaitenClient.Create(new() { BaseUri = new Uri(baseUrl) });
            await ApiError(() => anonymous.GetInstanceAsync(Ninja, token), HttpStatusCode.Unauthorized);
            using var invalid = KaitenClient.Create(new() { BaseUri = new Uri(baseUrl), BearerToken = "ksh_not-a-valid-credential" });
            await ApiError(() => invalid.GetInstanceAsync(Ninja, token), HttpStatusCode.Unauthorized);
            using var scoped = Client(baseUrl, tokenDir, "under-scoped-token");
            await ApiError(() => scoped.GetInstanceAsync(Ninja, token), HttpStatusCode.Forbidden, "Auth.MissingScope");
            using var foreign = Client(baseUrl, tokenDir, "foreign-token");
            await ApiError(() => foreign.GetInstanceAsync(Ninja, token), HttpStatusCode.NotFound, "GetInstance.NotFound");
            await ApiError(() => runtime.GetInstanceAsync("does-not-exist", token), HttpStatusCode.NotFound, "GetInstance.NotFound");
            var missingFlag = await flags.GetBooleanDetailsAsync("does-not-exist", false, NinjaContext, cancellationToken: token);
            Check(missingFlag.ErrorType == ErrorType.FlagNotFound, "OpenFeature missing flag code lost.");
            foreach (var credential in new[] { "under-scoped-token", "foreign-token" })
            {
                await using var deniedFeatures = await Features(baseUrl, new FileKaitenCredentialProvider(Path.Combine(tokenDir, credential)));
                var denied = await deniedFeatures.GetRequiredService<IFeatureClient>().GetBooleanDetailsAsync(Tracker, false, NinjaContext, cancellationToken: token);
                Check(Unavailable(denied), "OFREP scope/foreign-organization probe unexpectedly succeeded.");
            }
            foreach (var bearer in new string?[] { null, "ksh_not-a-valid-credential" })
            {
                await using var deniedFeatures = await Features(baseUrl, null, bearer);
                var denied = await deniedFeatures.GetRequiredService<IFeatureClient>().GetBooleanDetailsAsync(Tracker, false, NinjaContext, cancellationToken: token);
                Check(denied.ErrorType == ErrorType.ProviderNotReady && Unavailable(denied), "Official provider missing/invalid authentication did not deny.");
            }
            completed.Add("Core and official OFREP missing/invalid authentication, wrong scope, foreign isolation, missing instance/flag");

            Console.WriteLine("CHECK: presenter targeting and next-request propagation");
            var on = flag.Variants!.Single(v => v.Value.ValueKind == JsonValueKind.True).Name;
            var isolatedRule = new BasicFlagTargeting { Name = "SDK isolated Sushi integration", Rule = "__kaiten.customer.slug == 'ninja-osaka'", Variant = on };
            flagChanged = true;
            await presenter.UpdateFeatureFlagAsync(Tracker, flag.ToUpdateRequest() with
            { Targetings = new[] { isolatedRule }.Concat(flag.Targetings ?? []).ToArray() }, token);
            Check(await Tracking(flags, NinjaContext, token), "Targeted flag mutation not visible on next evaluation.");
            Check(!await ProtectedTracking(runtime, flags, token), "Enabled flag bypassed a denied authoritative entitlement.");
            Check(await Tracking(flags, SakuraContext, token), "Sakura changed during Ninja targeting probe.");
            await presenter.UpdateFeatureFlagAsync(Tracker, flag.ToUpdateRequest(), token); flagChanged = false;
            Check(!await Tracking(flags, NinjaContext, token), "Flag rollback failed.");
            completed.Add("SDK full flag PUT, targeted Ninja override, Sakura unaffected, next-request propagation, rollback");

            Console.WriteLine("CHECK: authoritative active, expired and future license terms");
            var now = (await runtime.GetInstanceAsync(Ninja, token)).LicenseTerm!.EvaluatedAt;
            flagChanged = true; grantChanged = true;
            await presenter.UpdateFeatureFlagAsync(Tracker, flag.ToUpdateRequest() with
            { Targetings = new[] { isolatedRule }.Concat(flag.Targetings ?? []).ToArray() }, token);
            await presenter.UpdateLicenseEntitlementAsync("starter", "delivery-tracking", new()
            { Value = new BooleanEntitlementValue { Value = true } }, token);
            Check(await ProtectedTracking(runtime, flags, token), "Active entitled/flag-enabled baseline should allow.");
            instanceChanged = true;
            await presenter.UpdateInstanceAsync(Ninja, ninja.ToUpdateRequest() with
            { StartLicenseDate = now.AddDays(-2).ToOffset(TimeSpan.FromHours(9)), EndLicenseDate = now.AddDays(-1).ToOffset(TimeSpan.FromHours(9)) }, token);
            var expired = await runtime.GetInstanceAsync(Ninja, token);
            Check(expired.LicenseTerm!.Status == LicenseTermStatus.Expired, "Expired term did not deny.");
            Check(expired.EndLicenseDate == now.AddDays(-1), "Offset term changed its absolute instant.");
            Check(await Tracking(flags, NinjaContext, token) && !await ProtectedTracking(runtime, flags, token), "Enabled/entitled flag bypassed expired license guard.");
            await presenter.UpdateInstanceAsync(Ninja, ninja.ToUpdateRequest() with
            { StartLicenseDate = now.AddDays(1).ToOffset(TimeSpan.FromHours(-7)), EndLicenseDate = now.AddDays(2).ToOffset(TimeSpan.FromHours(-7)) }, token);
            var future = await runtime.GetInstanceAsync(Ninja, token);
            Check(future.LicenseTerm!.Status == LicenseTermStatus.NotStarted, "Future term did not deny.");
            Check(await Tracking(flags, NinjaContext, token) && !await ProtectedTracking(runtime, flags, token), "Enabled/entitled flag bypassed future license guard.");
            Check((await runtime.GetInstanceAsync(Sakura, token)).LicenseTerm!.Status == LicenseTermStatus.Active, "Sakura term was affected.");
            await presenter.UpdateInstanceAsync(Ninja, ninja.ToUpdateRequest(), token); instanceChanged = false;
            Check((await runtime.GetInstanceAsync(Ninja, token)).LicenseTerm!.Status == LicenseTermStatus.Active, "Term rollback failed.");
            Check(await ProtectedTracking(runtime, flags, token), "Active license restoration did not restore protected capability.");
            await presenter.UpdateFeatureFlagAsync(Tracker, flag.ToUpdateRequest(), token); flagChanged = false;
            await presenter.UpdateLicenseEntitlementAsync("starter", "delivery-tracking", starterGrant.ToUpdateRequest(), token); grantChanged = false;
            completed.Add("enabled flag cannot bypass denied entitlement or expired/future term; active/offset/tenant restoration");

            Console.WriteLine("CHECK: license grant change and licensed instance upgrade");
            grantChanged = true;
            await presenter.UpdateLicenseEntitlementAsync("starter", "delivery-tracking", new()
            { Value = new BooleanEntitlementValue { Value = true } }, token);
            Check(((BooleanEntitlementValue)(await runtime.GetInstanceEntitlementUsageAsync(Ninja, "delivery-tracking", token)).Value).Value, "Grant update was not authoritative.");
            Check(await Tracking(flags, NinjaContext, token), "Grant-to-flag propagation failed.");
            Check(await Tracking(flags, SakuraContext, token), "Premium tenant changed during Starter grant probe.");
            await presenter.UpdateLicenseEntitlementAsync("starter", "delivery-tracking", starterGrant.ToUpdateRequest(), token); grantChanged = false;
            instanceChanged = true;
            await presenter.UpdateInstanceAsync(Ninja, ninja.ToUpdateRequest() with { LicenseId = standard.Id }, token);
            Check((await runtime.GetInstanceAsync(Ninja, token)).LicenseSlug == "standard-v2", "Upgrade did not pin actual license.");
            Check(await Tracking(flags, NinjaContext, token), "Standard-v2 entitlement did not enable tracking.");
            Check((await runtime.GetInstanceAsync(Sakura, token)).LicenseId == sakura.LicenseId, "Sakura license changed.");
            await presenter.UpdateInstanceAsync(Ninja, ninja.ToUpdateRequest(), token); instanceChanged = false;
            Check(!await Tracking(flags, NinjaContext, token), "License upgrade rollback failed.");
            completed.Add("typed grant GET/PUT and rollback, Ninja Standard-v2 upgrade/tracking, Sakura unchanged");

            Console.WriteLine("CHECK: numeric append/set, precise quota denial and rollback");
            usageChanged = true;
            var appended = await runtime.ReportNumericUsageAsync(Ninja, "monthly-orders", 1, UsageReportBehavior.Append, cancellationToken: token);
            Check(((NumberEntitlementValue)appended.Value).Value == originalNumber.Value + 1, "SUM append contract failed.");
            await ApiError(() => runtime.ReportNumericUsageAsync(Ninja, "monthly-orders", 1000000, UsageReportBehavior.Append, cancellationToken: token),
                HttpStatusCode.Conflict, "ReportEntitlementUsageMetric.ThresholdExceeded");
            var afterDenied = (NumberEntitlementValue)(await runtime.GetInstanceEntitlementUsageAsync(Ninja, "monthly-orders", token)).Value;
            Check(afterDenied.Value == ((NumberEntitlementValue)appended.Value).Value && afterDenied.EventCount == ((NumberEntitlementValue)appended.Value).EventCount, "Rejected quota usage was written.");
            await RestoreUsage(runtime, originalNumber, originalCount); usageChanged = false;
            completed.Add("numeric append/set, exact 409 threshold code, rejected-write rollback, original SUM value/event_count restored");

            Console.WriteLine("CHECK: network failure remains unavailable, never allowed");
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            using var unavailable = KaitenClient.Create(new() { BaseUri = new Uri($"http://127.0.0.1:{port}/api"), RequestTimeout = TimeSpan.FromSeconds(1) });
            try { await unavailable.GetInstanceAsync(Ninja, token); throw new InvalidOperationException("Closed gateway unexpectedly succeeded."); }
            catch (KaitenNetworkException) { }
            await using var unavailableFeatures = await Features($"http://127.0.0.1:{port}/api", null);
            var unavailableFlag = await unavailableFeatures.GetRequiredService<IFeatureClient>().GetBooleanDetailsAsync(Tracker, false, NinjaContext, cancellationToken: token);
            Check(Unavailable(unavailableFlag), "OpenFeature outage fallback must be unavailable, not a successful false.");
            completed.Add("real Core and official-provider network unavailability fail closed");

            Console.WriteLine("CHECK: SDK traceparent, Kaiten-TraceId and Jaeger correlation");
            var traced = new List<Activity>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == KaitenClient.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = traced.Add
            };
            ActivitySource.AddActivityListener(listener);
            await runtime.GetInstanceAsync(Ninja, token);
            var activity = traced.Single();
            var traceId = activity.TraceId.ToHexString();
            Check(activity.GetTagItem("kaiten.trace_id") as string == traceId, "Core did not echo the SDK trace identity.");
            using var jaeger = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var spanCount = 0;
            for (var attempt = 0; attempt < 20 && spanCount == 0; attempt++)
            {
                using var response = await jaeger.GetAsync(jaegerUrl.TrimEnd('/') + "/api/traces/" + traceId, token);
                if (response.IsSuccessStatusCode)
                {
                    using var trace = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    if (trace.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        foreach (var item in data.EnumerateArray())
                            if (item.TryGetProperty("spans", out var spans)) spanCount += spans.GetArrayLength();
                }
                if (spanCount == 0) await Task.Delay(500, token);
            }
            Check(spanCount > 0, "No matching real Core/Envoy trace was found in Jaeger.");
            completed.Add("redacted typed Core SDK activity and matching Core/Envoy Jaeger trace");
            await Snapshot(snapshotDir, "receipt.json", new { completed, traceId, spanCount, networkUnavailable = true });
        }
        finally
        {
            // No caller cancellation for cleanup; each SDK request still has its own bounded deadline.
            // Marked BEFORE mutations so transport ambiguity also triggers restoration.
            var restorationErrors = new List<string>();
            if (flagChanged) await Restore(() => presenter.UpdateFeatureFlagAsync(Tracker, flag.ToUpdateRequest()), "flag", restorationErrors);
            if (grantChanged) await Restore(() => presenter.UpdateLicenseEntitlementAsync("starter", "delivery-tracking", starterGrant.ToUpdateRequest()), "grant", restorationErrors);
            if (instanceChanged) await Restore(() => presenter.UpdateInstanceAsync(Ninja, ninja.ToUpdateRequest()), "instance", restorationErrors);
            if (usageChanged) await Restore(() => RestoreUsage(runtime, originalNumber, originalCount), "usage", restorationErrors);
            if (restorationErrors.Count > 0) throw new InvalidOperationException("Snapshot restoration failed for " + string.Join(", ", restorationErrors) + "; restore private snapshots via SDK before continuing.");
        }
        var finalNinja = await runtime.GetInstanceAsync(Ninja, token);
        var finalSakura = await runtime.GetInstanceAsync(Sakura, token);
        Check(JsonSerializer.Serialize(finalNinja.ToUpdateRequest(), JsonOptions) == JsonSerializer.Serialize(ninja.ToUpdateRequest(), JsonOptions), "Ninja full snapshot was not restored.");
        Check(JsonSerializer.Serialize(finalSakura.ToUpdateRequest(), JsonOptions) == JsonSerializer.Serialize(sakura.ToUpdateRequest(), JsonOptions), "Sakura full snapshot changed.");
        Check(JsonSerializer.Serialize((await runtime.GetFeatureFlagAsync(Tracker, token)).ToUpdateRequest(), JsonOptions) == JsonSerializer.Serialize(flag.ToUpdateRequest(), JsonOptions), "Flag full snapshot was not restored.");
        Check(JsonSerializer.Serialize((await runtime.GetLicenseEntitlementAsync("starter", "delivery-tracking", token)).ToUpdateRequest(), JsonOptions) == JsonSerializer.Serialize(starterGrant.ToUpdateRequest(), JsonOptions), "Grant full snapshot was not restored.");
        Console.WriteLine("Artifacts: " + snapshotDir + " (private snapshots and redacted receipt, no credentials)");
    }

    private static async Task RestoreUsage(IKaitenClient runtime, NumberEntitlementValue original, int originalCount)
    {
        await runtime.ReportNumericUsageAsync(Ninja, "monthly-orders", original.Value, UsageReportBehavior.Set);
        // SUM set resets event_count to 1. Bounded zero observations restore the original count without changing the sum.
        for (var i = 1; i < originalCount; i++)
            await runtime.ReportNumericUsageAsync(Ninja, "monthly-orders", 0, UsageReportBehavior.Append);
        var restored = (NumberEntitlementValue)(await runtime.GetInstanceEntitlementUsageAsync(Ninja, "monthly-orders")).Value;
        Check(restored.Value == original.Value && restored.EventCount == original.EventCount, "SUM usage snapshot restoration failed.");
    }
    private static async Task Restore(Func<Task> action, string name, List<string> errors)
    { try { await action(); } catch (Exception) { errors.Add(name); } }
    private static async Task ApiError(Func<Task> action, HttpStatusCode status, string? code = null)
    {
        try { await action(); throw new InvalidOperationException("Expected Core denial did not occur."); }
        catch (KaitenApiException error)
        { Check(error.StatusCode == status, "Core denial HTTP status mismatch."); if (code is not null) Check(error.Code == code, "Core denial machine code mismatch."); }
    }
    private static async Task<bool> Tracking(IFeatureClient client, EvaluationContext context, CancellationToken token)
    {
        var details = await client.GetBooleanDetailsAsync(Tracker, false, context, cancellationToken: token);
        Check(!Unavailable(details), "OpenFeature unavailable; do not authorize from the fallback value.");
        return details.Value;
    }
    // Application authorization fixture: official flags plus typed authoritative Core guards.
    // Assumes valid OFREP responses: provider 0.1.7 detail guards alone can accept malformed
    // HTTP400/wrong-key HTTP200 true. Protected side effects need app-boundary protocol validation.
    private static async Task<bool> ProtectedTracking(IKaitenClient core, IFeatureClient flags, CancellationToken token)
    {
        var flag = await Tracking(flags, NinjaContext, token);
        var license = await core.GetInstanceAsync(Ninja, token);
        var entitlement = await core.GetInstanceEntitlementUsageAsync(Ninja, "delivery-tracking", token);
        return (license.LicenseTerm ?? throw new KaitenProtocolException()).Status == LicenseTermStatus.Active && ((BooleanEntitlementValue)entitlement.Value).Value && flag;
    }
    private static bool Unavailable<T>(FlagEvaluationDetails<T> details) => details.ErrorType != ErrorType.None || details.Reason == Reason.Error;
    private static async Task<ServiceProvider> Features(string baseUrl, IKaitenCredentialProvider? credentials, string? bearer = null)
    {
        var services = new ServiceCollection();
        // Official provider configuration is consumer-owned; credentials snapshot per fixture.
        if (credentials is not null) bearer = await credentials.GetBearerTokenAsync();
        services.AddHttpClient("integration-ofrep")
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddOpenFeature(builder => builder.AddOfrepProvider(options =>
        {
            options.BaseUrl = baseUrl;
            options.Timeout = TimeSpan.FromSeconds(5);
            options.HttpClientName = "integration-ofrep";
            if (bearer is not null) options.Headers["Authorization"] = "Bearer " + bearer;
        }));
        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IFeatureLifecycleManager>().EnsureInitializedAsync();
        return provider;
    }
    private static KaitenClient Client(string baseUrl, string dir, string name)
        => KaitenClient.Create(new() { BaseUri = new Uri(baseUrl) }, new FileKaitenCredentialProvider(Path.Combine(dir, name)));
    private static EvaluationContext Context(string customer, string instance) => EvaluationContext.Builder().SetTargetingKey(customer)
        .Set("kaiten", new Value(new Structure(new Dictionary<string, Value> { ["instanceSlug"] = new(instance) })))
        .Set("userId", "opaque-integration-user").Set("orderCount", 12).Build();
    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException("Required prerequisite " + name + " is absent.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Snapshot<T>(string dir, string file, T value)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(Path.Combine(dir, file), options);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
    }
}
