// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
namespace KaitenCloud.Sdk;

public interface IKaitenClient
{
    Task<Customer> GetCustomerAsync(string customerSlug, CancellationToken cancellationToken = default);
    Task<Instance> GetInstanceAsync(string instanceSlug, CancellationToken cancellationToken = default);
    Task<License> GetLicenseAsync(string licenseSlug, CancellationToken cancellationToken = default);
    Task<LicenseValidation> ValidateInstanceLicenseAsync(string instanceSlug, CancellationToken cancellationToken = default);
    Task<EntitlementUsage> GetInstanceEntitlementUsageAsync(string instanceSlug, string entitlementSlug, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EntitlementUsage>> GetAllInstanceEntitlementUsageAsync(string instanceSlug, CancellationToken cancellationToken = default);
    Task<EntitlementUsage> ReportNumericUsageAsync(string instanceSlug, string entitlementSlug, double value,
        UsageReportBehavior behavior, IReadOnlyDictionary<string, JsonElement>? metadata = null, CancellationToken cancellationToken = default);
    Task<FlagEvaluation> EvaluateFlagAsync(string flagSlug, IReadOnlyDictionary<string, object?> context, CancellationToken cancellationToken = default);
    Task<FlagEvaluation<T>> EvaluateFlagAsync<T>(string flagSlug, IReadOnlyDictionary<string, object?> context, CancellationToken cancellationToken = default);
    Task<BulkFlagEvaluation> EvaluateAllFlagsAsync(IReadOnlyDictionary<string, object?> context, CancellationToken cancellationToken = default);
    Task<FeatureFlag> GetFeatureFlagAsync(string flagSlug, CancellationToken cancellationToken = default);
    Task UpdateFeatureFlagAsync(string flagSlug, UpdateFeatureFlagRequest request, CancellationToken cancellationToken = default);
    Task UpdateInstanceAsync(string instanceSlug, UpdateInstanceRequest request, CancellationToken cancellationToken = default);
    Task<LicenseEntitlement> GetLicenseEntitlementAsync(string licenseSlug, string entitlementSlug, CancellationToken cancellationToken = default);
    Task<Page<LicenseEntitlement>> GetLicenseEntitlementsAsync(string licenseSlug, string? cursor = null, int limit = 100, CancellationToken cancellationToken = default);
    Task UpdateLicenseEntitlementAsync(string licenseSlug, string entitlementSlug, UpdateLicenseEntitlementRequest request, CancellationToken cancellationToken = default);
}
