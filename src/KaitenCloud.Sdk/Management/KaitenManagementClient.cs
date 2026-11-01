// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using KaitenCloud.Sdk.Management.Models;
namespace KaitenCloud.Sdk.Management;

public interface IKaitenManagementClient
{
    CustomersResource Customers { get; }
    InstancesResource Instances { get; }
    LicensesResource Licenses { get; }
    LicenseFamiliesResource LicenseFamilies { get; }
    EntitlementsResource Entitlements { get; }
    EntitlementGroupsResource EntitlementGroups { get; }
    FeatureFlagsResource FeatureFlags { get; }
    IntegrationsResource Integrations { get; }
}

/// <summary>Typed Core management. Flag decisions remain official OpenFeature calls.</summary>
public sealed class KaitenManagementClient : IKaitenManagementClient, IDisposable
{
    private readonly KaitenClient transport;
    private readonly TimeSpan timeout;
    public CustomersResource Customers { get; }
    public InstancesResource Instances { get; }
    public LicensesResource Licenses { get; }
    public LicenseFamiliesResource LicenseFamilies { get; }
    public EntitlementsResource Entitlements { get; }
    public EntitlementGroupsResource EntitlementGroups { get; }
    public FeatureFlagsResource FeatureFlags { get; }
    public IntegrationsResource Integrations { get; }

    public KaitenManagementClient(HttpClient httpClient, KaitenClientOptions options,
        IKaitenCredentialProvider? credentialProvider = null, ILogger<KaitenClient>? logger = null)
        : this(new KaitenClient(httpClient, options, credentialProvider, logger)) { }
    internal KaitenManagementClient(KaitenClient transport)
    {
        this.transport = transport; timeout = transport.RequestTimeout;
        Customers = new(this); Instances = new(this); Licenses = new(this); LicenseFamilies = new(this);
        Entitlements = new(this); EntitlementGroups = new(this); FeatureFlags = new(this); Integrations = new(this);
    }
    public static KaitenManagementClient Create(KaitenClientOptions options,
        IKaitenCredentialProvider? credentialProvider = null, ILogger<KaitenClient>? logger = null)
        => new(KaitenClient.Create(options, credentialProvider, logger));
    internal Task<T> SendAsync<T>(ManagementOperation operation, string path, object? request, CancellationToken token)
        => transport.SendManagementAsync<T>(operation, path, request, token);
    internal async IAsyncEnumerable<T> EnumerateAsync<T>(string? cursor, Func<string?, CancellationToken, Task<ManagementPage<T>>> next,
        int? totalLimit, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (totalLimit < 1) throw new ArgumentOutOfRangeException(nameof(totalLimit));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (cursor is not null) seen.Add(cursor);
        var count = 0;
        while (true)
        {
            ManagementPage<T> page;
            try { page = await next(cursor, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new KaitenTimeoutException(); }
            if (page.HasMore && (string.IsNullOrEmpty(page.NextCursor) || !seen.Add(page.NextCursor))) throw new KaitenProtocolException();
            foreach (var item in page.Items ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested) throw new KaitenTimeoutException();
                yield return item;
                if (++count == totalLimit) yield break;
            }
            if (!page.HasMore) yield break;
            cursor = page.NextCursor;
        }
    }
    public void Dispose() => transport.Dispose();
}
internal static class ManagementPagination
{
    internal static async Task<IReadOnlyList<T>> CollectAsync<T>(IAsyncEnumerable<T> source, CancellationToken token)
    {
        var result = new List<T>();
        await foreach (var item in source.WithCancellation(token).ConfigureAwait(false)) result.Add(item);
        return result;
    }
}
