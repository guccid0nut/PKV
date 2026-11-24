// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Grpc.Core;

namespace Grpc.Challenger.Tests;

public sealed class AdversarialServerCallContext(CancellationToken cancellationToken = default) : ServerCallContext
{
    protected override string MethodCore => "AdversarialTestMethod";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "127.0.0.1";
    protected override DateTime DeadlineCore => TimeProvider.System.GetUtcNow().UtcDateTime.AddMinutes(5);
    protected override Metadata RequestHeadersCore { get; } = [];

    protected override CancellationToken CancellationTokenCore { get; } = cancellationToken;

    protected override Metadata ResponseTrailersCore { get; } = [];

    protected override Status StatusCore { get; set; }

    protected override WriteOptions? WriteOptionsCore { get; set; }

    protected override AuthContext AuthContextCore => null!;

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
    {
        return Task.CompletedTask;
    }

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
    {
        return null!;
    }
}
