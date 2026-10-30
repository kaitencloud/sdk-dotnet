// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenFeature;
using OpenFeature.Providers.Ofrep.DependencyInjection;

namespace KaitenCloud.Sdk;

// Server-side transport configuration only. Evaluate through OpenFeature.IFeatureClient.
public sealed class KaitenOfrepOptions
{
    public Uri BaseUri { get; set; } = null!;
    public string? BearerToken { get; set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public int MaxResponseBytes { get; set; } = 4 * 1024 * 1024;

    internal KaitenOfrepOptions Snapshot()
    {
        var normalized = new KaitenClientOptions { BaseUri = BaseUri, RequestTimeout = RequestTimeout, MaxResponseBytes = MaxResponseBytes }.Snapshot();
        return new() { BaseUri = normalized.BaseUri, RequestTimeout = RequestTimeout, BearerToken = BearerToken, MaxResponseBytes = MaxResponseBytes };
    }
}

public static class KaitenOpenFeatureServiceCollectionExtensions
{
    public const string HttpClientName = "KaitenCloud.Sdk.Ofrep";
    public const string ActivitySourceName = "KaitenCloud.Sdk.Ofrep";
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName, "0.2.0");

    // Add once per host. Official Hosting owns initialization/shutdown; no shared tenant context.
    public static IServiceCollection AddKaitenOpenFeature(this IServiceCollection services, Action<KaitenOfrepOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new KaitenOfrepOptions();
        configure(options);
        options = options.Snapshot();
        services.AddTransient(sp => new KaitenOfrepTransportHandler(options,
            sp.GetService<IKaitenCredentialProvider>(), sp.GetRequiredService<ILogger<KaitenOfrepTransportHandler>>()));
        services.AddHttpClient(HttpClientName)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ActivityHeadersPropagator = null,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            })
            .AddHttpMessageHandler<KaitenOfrepTransportHandler>();
        // Upstream logs exception messages/flag keys. Only our redacted transport logger is safe.
        services.AddLogging(logging =>
        {
            logging.AddFilter("OpenFeature.Providers.Ofrep.Client.OfrepClient", LogLevel.None);
            logging.AddFilter("OpenFeature.OpenFeatureClient", LogLevel.None);
        });
        services.AddOpenFeature(builder => builder.AddOfrepProvider(provider =>
        {
            provider.BaseUrl = options.BaseUri.AbsoluteUri;
            provider.Timeout = options.RequestTimeout;
            provider.HttpClientName = HttpClientName;
        }));
        return services;
    }
}

// Authentication/diagnostics only. The official provider owns protocol, values and errors.
internal sealed class KaitenOfrepTransportHandler(KaitenOfrepOptions options,
    IKaitenCredentialProvider? credentials, ILogger<KaitenOfrepTransportHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var activity = KaitenOpenFeatureServiceCollectionExtensions.ActivitySource.StartActivity("kaiten.ofrep", ActivityKind.Client);
        activity?.SetTag("http.request.method", request.Method.Method);
        int? status = null;
        try
        {
            string? bearer;
            try
            {
                bearer = credentials is null ? options.BearerToken :
                    await credentials.GetBearerTokenAsync(cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
                if (bearer is not null && (string.IsNullOrWhiteSpace(bearer) || bearer.Any(c => c <= ' ' || c >= '\u007f')))
                    throw new KaitenCredentialException();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new KaitenCredentialException(); }
            // Request-local auth, not mutable HttpClient defaults: rotation/concurrency are safe.
            request.Headers.Authorization = bearer is null ? null : new AuthenticationHeaderValue("Bearer", bearer);
            var current = activity ?? Activity.Current;
            if (current is { IdFormat: ActivityIdFormat.W3C, Id: not null })
            {
                request.Headers.Remove("traceparent");
                request.Headers.TryAddWithoutValidation("traceparent", current.Id);
                request.Headers.Remove("tracestate");
                if (current.TraceStateString is not null) request.Headers.TryAddWithoutValidation("tracestate", current.TraceStateString);
            }
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            activity?.SetTag("http.response.status_code", status);
            if (!response.IsSuccessStatusCode) activity?.SetStatus(ActivityStatusCode.Error);
            if (response.Headers.TryGetValues("Kaiten-TraceId", out var ids))
            {
                var id = ids.FirstOrDefault();
                if (id is { Length: 32 } && id.All(Uri.IsHexDigit)) activity?.SetTag("kaiten.trace_id", id);
            }
            try
            {
                if (response.IsSuccessStatusCode) await ValidateResponseAsync(request, response, cancellationToken).ConfigureAwait(false);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        catch (Exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            throw; // OpenFeature returns error details/default, never an authorization grant.
        }
        finally
        {
            logger.LogDebug("Kaiten OFREP transport completed with HTTP status {Status}", status);
        }
    }

    // Upstream 0.1.7 substitutes fallback/key on missing wire fields and ignores unknown error codes.
    // Validate the envelope before the OFFICIAL provider decodes it; never interpret the value/rules here.
    private async Task ValidateResponseAsync(HttpRequestMessage request, HttpResponseMessage response, CancellationToken token)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentLength > options.MaxResponseBytes)
            throw new KaitenProtocolException();
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > options.MaxResponseBytes) throw new KaitenProtocolException();
            buffer.Write(chunk, 0, count);
        }
        var bytes = buffer.ToArray();
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var expectedKey = Uri.UnescapeDataString(request.RequestUri!.Segments[^1]);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("key", out var key) ||
                key.ValueKind != JsonValueKind.String || key.GetString() != expectedKey)
                throw new KaitenProtocolException();
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!fields.Add(property.Name)) throw new KaitenProtocolException();
            if (root.TryGetProperty("errorCode", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                // These are the codes mapped by the official provider. Unmapped codes fail closed.
                if (error.ValueKind != JsonValueKind.String || error.GetString() is not
                    ("FLAG_NOT_FOUND" or "TYPE_MISMATCH" or "PARSE_ERROR" or "PROVIDER_NOT_READY" or
                     "INVALID_CONTEXT" or "TARGETING_KEY_MISSING" or "PROVIDER_FATAL" or "GENERAL"))
                    throw new KaitenProtocolException();
            }
            else if (!root.TryGetProperty("value", out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new KaitenProtocolException();
        }
        catch (JsonException) { throw new KaitenProtocolException(); }
        var original = response.Content;
        var replay = new ByteArrayContent(bytes);
        foreach (var header in original.Headers) replay.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = replay;
        original.Dispose();
    }
}
