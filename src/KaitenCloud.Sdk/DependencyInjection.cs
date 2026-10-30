// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace KaitenCloud.Sdk;

public static class KaitenServiceCollectionExtensions
{
    public static IServiceCollection AddKaiten(this IServiceCollection services, Action<KaitenClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<KaitenClientOptions>().Configure(configure).Validate(options =>
        {
            try { _ = options.Snapshot(); return true; }
            catch (ArgumentException) { return false; }
        }, "Invalid Kaiten client options.");
        services.AddHttpClient("KaitenCloud.Sdk", http => http.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ActivityHeadersPropagator = null,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });
        services.AddTransient<IKaitenClient>(sp => new KaitenClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("KaitenCloud.Sdk"),
            sp.GetRequiredService<IOptions<KaitenClientOptions>>().Value,
            sp.GetService<IKaitenCredentialProvider>(), sp.GetService<ILogger<KaitenClient>>(), true));
        return services;
    }
}
