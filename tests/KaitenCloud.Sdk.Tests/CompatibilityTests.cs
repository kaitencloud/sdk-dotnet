// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using KaitenCloud.Sdk;
using Xunit;

namespace KaitenCloud.Sdk.Tests;

public sealed class CompatibilityTests
{
    [Fact]
    public void ExistingInterfaceImplementersStillCompileWithoutAnyNewMembers()
    {
        IKaitenClient client = new ExistingApplicationClient();
        Assert.IsType<ExistingApplicationClient>(client);
        Assert.Equal(12, typeof(IKaitenClient).GetMethods().Length);
        foreach (var method in typeof(IKaitenClient).GetMethods())
        {
            var token = method.GetParameters()[^1];
            Assert.Equal(typeof(CancellationToken), token.ParameterType);
            Assert.True(token.HasDefaultValue);
        }
    }

    // Compile fixture for the pre-parity application/mock seam. Adding an abstract
    // member to IKaitenClient intentionally fails compilation here.
    private sealed class ExistingApplicationClient : IKaitenClient
    {
        public Task<Customer> GetCustomerAsync(string customerSlug, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Instance> GetInstanceAsync(string instanceSlug, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<License> GetLicenseAsync(string licenseSlug, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EntitlementUsage> GetInstanceEntitlementUsageAsync(string instanceSlug, string entitlementSlug, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EntitlementUsage>> GetAllInstanceEntitlementUsageAsync(string instanceSlug, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EntitlementUsage> ReportNumericUsageAsync(string instanceSlug, string entitlementSlug, double value, UsageReportBehavior behavior,
            IReadOnlyDictionary<string, JsonElement>? metadata = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FeatureFlag> GetFeatureFlagAsync(string flagSlug, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateFeatureFlagAsync(string flagSlug, UpdateFeatureFlagRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateInstanceAsync(string instanceSlug, UpdateInstanceRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LicenseEntitlement> GetLicenseEntitlementAsync(string licenseSlug, string entitlementSlug, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Page<LicenseEntitlement>> GetLicenseEntitlementsAsync(string licenseSlug, string? cursor = null, int limit = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateLicenseEntitlementAsync(string licenseSlug, string entitlementSlug, UpdateLicenseEntitlementRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
