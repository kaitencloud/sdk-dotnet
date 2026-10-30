// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
namespace KaitenCloud.Sdk;

public interface IKaitenCredentialProvider
{
    ValueTask<string?> GetBearerTokenAsync(CancellationToken cancellationToken = default);
}

// Rereads on every request/attempt. Keep the file private (0600); replace it atomically to rotate.
public sealed class FileKaitenCredentialProvider(string path) : IKaitenCredentialProvider
{
    public async ValueTask<string?> GetBearerTokenAsync(CancellationToken cancellationToken = default)
        => (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
}
