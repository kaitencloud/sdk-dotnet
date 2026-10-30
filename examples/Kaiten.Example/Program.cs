// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using KaitenCloud.Sdk;
using Microsoft.Extensions.DependencyInjection;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Hosting;
using OpenFeature.Model;

var baseUrl = Environment.GetEnvironmentVariable("KAITEN_BASE_URL")
    ?? throw new InvalidOperationException("Set KAITEN_BASE_URL to the authenticated Core gateway.");
var tokenFile = Environment.GetEnvironmentVariable("KAITEN_TOKEN_FILE")
    ?? throw new InvalidOperationException("Set KAITEN_TOKEN_FILE to a private server-side runtime token file.");
var services = new ServiceCollection();
services.AddSingleton<IKaitenCredentialProvider>(new FileKaitenCredentialProvider(tokenFile));
services.AddKaiten(options => options.BaseUri = new Uri(baseUrl));
services.AddKaitenOpenFeature(options => options.BaseUri = new Uri(baseUrl));
using var provider = services.BuildServiceProvider();
var lifecycle = provider.GetRequiredService<IFeatureLifecycleManager>();
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
try
{
    // ASP.NET/generic hosts initialize/shutdown automatically. Plain DI needs this explicitly.
    await lifecycle.EnsureInitializedAsync(deadline.Token);
    var client = provider.GetRequiredService<IKaitenClient>();
    var flags = provider.GetRequiredService<IFeatureClient>();
    // Derive identity/tenant/instance/facts from authorized server state, not browser claims.
    var context = EvaluationContext.Builder().SetTargetingKey("ninja-osaka")
        .Set("kaiten", new Value(new Structure(new Dictionary<string, Value>
        { ["instanceSlug"] = new("ninja-osaka-prod") })))
        .Set("userId", "opaque-example-user").Set("orderCount", 12).Build();
    var license = await client.ValidateInstanceLicenseAsync("ninja-osaka-prod", deadline.Token);
    var tracker = await flags.GetBooleanDetailsAsync("delivery-tracking-enabled", false, context, cancellationToken: deadline.Token);
    if (tracker.ErrorType != ErrorType.None || tracker.Reason == Reason.Error)
    {
        Console.Error.WriteLine("OpenFeature unavailable; no protected action was allowed.");
        Environment.ExitCode = 1;
    }
    else
    {
        var usage = await client.GetInstanceEntitlementUsageAsync("ninja-osaka-prod", "monthly-orders", deadline.Token);
        var entitlement = await client.GetInstanceEntitlementUsageAsync("ninja-osaka-prod", "delivery-tracking", deadline.Token);
        var allowed = license.Valid && ((BooleanEntitlementValue)entitlement.Value).Value && tracker.Value;
        Console.WriteLine($"License: {license.Status}; tracker={tracker.Value}; protected action allowed={allowed}; usage={((NumberEntitlementValue)usage.Value).Value}.");
        if (!allowed) Environment.ExitCode = 2; // Successful false is denial, not unavailability.
    }
}
catch (Exception error) when (error is KaitenException or OperationCanceledException)
{
    Console.Error.WriteLine($"Kaiten unavailable: {error.GetType().Name}. No protected action was allowed.");
    Environment.ExitCode = 1;
}
finally { await lifecycle.ShutdownAsync(); }
