// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using KaitenCloud.Sdk;
using Microsoft.Extensions.DependencyInjection;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Providers.Ofrep;
using OpenFeature.Providers.Ofrep.Configuration;
using OpenFeature.Model;

var baseUrl = Environment.GetEnvironmentVariable("KAITEN_BASE_URL")
    ?? throw new InvalidOperationException("Set KAITEN_BASE_URL to the authenticated Core gateway.");
var tokenFile = Environment.GetEnvironmentVariable("KAITEN_TOKEN_FILE")
    ?? throw new InvalidOperationException("Set KAITEN_TOKEN_FILE to a private server-side runtime token file.");
var gateway = new Uri(baseUrl);
if (!gateway.AbsolutePath.TrimEnd('/').EndsWith("/api", StringComparison.Ordinal) ||
    !string.IsNullOrEmpty(gateway.Query) || !string.IsNullOrEmpty(gateway.Fragment))
    throw new InvalidOperationException("KAITEN_BASE_URL must include the authenticated /api gateway prefix.");
var services = new ServiceCollection();
services.AddSingleton<IKaitenCredentialProvider>(new FileKaitenCredentialProvider(tokenFile));
services.AddKaiten(options => options.BaseUri = new Uri(baseUrl));
using var provider = services.BuildServiceProvider();
// The consumer owns the official provider, its credential snapshot and lifecycle.
var config = new OfrepOptions(baseUrl)
{
    Timeout = TimeSpan.FromSeconds(10),
    Headers = new Dictionary<string, string>
    { ["Authorization"] = "Bearer " + (await File.ReadAllTextAsync(tokenFile)).Trim() }
};
using var ofrep = new OfrepProvider(config);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
try
{
    await Api.Instance.SetProviderAsync(ofrep, deadline.Token);
    var client = provider.GetRequiredService<IKaitenClient>();
    var flags = Api.Instance.GetClient();
    // Derive identity/tenant/instance/facts from authorized server state, not browser claims.
    var context = EvaluationContext.Builder().SetTargetingKey("ninja-osaka")
        .Set("kaiten", new Value(new Structure(new Dictionary<string, Value>
        { ["instanceSlug"] = new("ninja-osaka-prod") })))
        .Set("userId", "opaque-example-user").Set("orderCount", 12).Build();
    var instance = await client.GetInstanceAsync("ninja-osaka-prod", deadline.Token);
    var term = instance.LicenseTerm ?? throw new KaitenProtocolException();
    // Assumes trustworthy, valid OFREP responses. Provider 0.1.7 can accept malformed
    // HTTP400/missing errorCode or wrong-key HTTP200 true despite false fallback/details guards.
    // Apps with protected side effects must validate protocol at their HTTP boundary (docs/openfeature.md).
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
        var allowed = term.Status == LicenseTermStatus.Active && ((BooleanEntitlementValue)entitlement.Value).Value && tracker.Value;
        Console.WriteLine($"License term: {term.Status}; tracker={tracker.Value}; protected action allowed={allowed}; usage={((NumberEntitlementValue)usage.Value).Value}.");
        if (!allowed) Environment.ExitCode = 2; // Successful false is denial, not unavailability.
    }
}
catch (Exception error) when (error is KaitenException or OperationCanceledException)
{
    Console.Error.WriteLine($"Kaiten unavailable: {error.GetType().Name}. No protected action was allowed.");
    Environment.ExitCode = 1;
}
finally { await Api.Instance.ShutdownAsync(); }
