// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using KaitenCloud.Sdk.Management;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace KaitenCloud.Sdk;

public static class KaitenManagementServiceCollectionExtensions
{
    public static IServiceCollection AddKaitenManagement(this IServiceCollection services, Action<KaitenClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new KaitenClientOptions();
        configure(options);
        options = options.Snapshot();
        services.AddHttpClient("KaitenCloud.Sdk.Management", http => http.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ActivityHeadersPropagator = null,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });
        services.AddTransient<IKaitenManagementClient>(sp => new KaitenManagementClient(new KaitenClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("KaitenCloud.Sdk.Management"), options,
            sp.GetService<IKaitenCredentialProvider>(), sp.GetService<ILogger<KaitenClient>>(), true)));
        return services;
    }
}
