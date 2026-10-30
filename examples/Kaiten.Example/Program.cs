// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using KaitenCloud.Sdk;
using Microsoft.Extensions.DependencyInjection;

var baseUrl = Environment.GetEnvironmentVariable("KAITEN_BASE_URL")
    ?? throw new InvalidOperationException("Set KAITEN_BASE_URL to the authenticated Core gateway including /api.");
var tokenFile = Environment.GetEnvironmentVariable("KAITEN_TOKEN_FILE")
    ?? throw new InvalidOperationException("Set KAITEN_TOKEN_FILE to a private server-side runtime token file.");
var services = new ServiceCollection();
services.AddSingleton<IKaitenCredentialProvider>(new FileKaitenCredentialProvider(tokenFile));
services.AddKaiten(options => options.BaseUri = new Uri(baseUrl));
using var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<IKaitenClient>();
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
try
{
    var license = await client.ValidateInstanceLicenseAsync("ninja-osaka-prod", deadline.Token);
    var tracker = await client.EvaluateFlagAsync<bool>("delivery-tracking-enabled", new Dictionary<string, object?>
    {
        ["targetingKey"] = "ninja-osaka",
        ["kaiten"] = new { instanceSlug = "ninja-osaka-prod" },
        ["userId"] = "opaque-example-user",
        ["orderCount"] = 12 // In an application derive this from authorized server-side state.
    }, deadline.Token);
    var usage = await client.GetInstanceEntitlementUsageAsync("ninja-osaka-prod", "monthly-orders", deadline.Token);
    var trackerEntitlement = await client.GetInstanceEntitlementUsageAsync("ninja-osaka-prod", "delivery-tracking", deadline.Token);
    Console.WriteLine($"Authoritative license: {license.Status}, valid={license.Valid}; tracker={tracker.Value}; entitled={((BooleanEntitlementValue)trackerEntitlement.Value).Value}; usage={((NumberEntitlementValue)usage.Value).Value}.");
    if (!license.Valid) Environment.ExitCode = 2; // Never grant a protected action on an invalid term.
}
catch (Exception error) when (error is KaitenException or OperationCanceledException)
{
    Console.Error.WriteLine($"Kaiten unavailable or evaluation denied: {error.GetType().Name}. No protected action was allowed.");
    Environment.ExitCode = 1;
}
