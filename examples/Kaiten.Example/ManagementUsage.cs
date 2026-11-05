// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using KaitenCloud.Sdk.Management;
using KaitenCloud.Sdk.Management.Models;

// Compiled by both the source example and the fresh net10 package consumer.
// No mutation or extra service is executed by the existing read-only Program.
internal static class ManagementUsage
{
    internal static async Task ReadAsync(IKaitenManagementClient client, string instanceSlug, CancellationToken token)
    {
        var customers = await client.Customers.ListAllAsync(new ListCustomerQuery { Limit = 100 }, totalLimit: 250, cancellationToken: token);
        var validation = await client.Instances.GetAsync(instanceSlug, token);
        var catalog = await client.FeatureFlags.GetManifestAsync(token);
        _ = (customers, validation, catalog); // Flags still use official OpenFeature, never this client.
    }
    internal static UpdateLicenseFamilyRequest ExplicitPatch()
        => new() { IsPublic = false, FallbackLicenseFamilySlug = new Optional<string?>(null) };
}
