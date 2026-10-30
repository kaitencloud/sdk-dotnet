// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
namespace KaitenCloud.Sdk;

public sealed class KaitenClientOptions
{
    public Uri BaseUri { get; set; } = null!;
    public string? BearerToken { get; set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxRetries { get; set; }
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(2);
    public int MaxResponseBytes { get; set; } = 4 * 1024 * 1024;

    internal KaitenClientOptions Snapshot()
    {
        if (BaseUri is null || !BaseUri.IsAbsoluteUri ||
            (BaseUri.Scheme != "http" && BaseUri.Scheme != "https") ||
            BaseUri.UserInfo.Length != 0 || BaseUri.Query.Length != 0 || BaseUri.Fragment.Length != 0)
            throw new ArgumentException("BaseUri must be an absolute HTTP(S) gateway URL without credentials, query or fragment.");
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (MaxRetries is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(MaxRetries));
        if (RetryDelay < TimeSpan.Zero || MaxRetryDelay < RetryDelay || MaxRetryDelay > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(RetryDelay));
        if (MaxResponseBytes is < 1024 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes));
        var path = BaseUri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/api", StringComparison.Ordinal)) path += "/api";
        var uri = new UriBuilder(BaseUri) { Path = path + "/" }.Uri;
        return new KaitenClientOptions
        {
            BaseUri = uri,
            BearerToken = BearerToken,
            RequestTimeout = RequestTimeout,
            MaxRetries = MaxRetries,
            RetryDelay = RetryDelay,
            MaxRetryDelay = MaxRetryDelay,
            MaxResponseBytes = MaxResponseBytes
        };
    }
}
