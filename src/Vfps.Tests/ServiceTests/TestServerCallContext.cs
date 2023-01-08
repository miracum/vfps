// via https://github.com/dotnet/AspNetCore.Docs/blob/0799d9bda43805ef4cea766d8567fb963fd8744d/aspnetcore/grpc/test-services/sample/Tests/Server/UnitTests/Helpers/TestServerCallContext.cs

#region Copyright notice and license

// Copyright 2019 The gRPC Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#endregion

using Grpc.Core;
using System.Diagnostics.CodeAnalysis;

namespace Vfps.Tests.ServiceTests;

[ExcludeFromCodeCoverage]
public sealed class TestServerCallContext : ServerCallContext
{
    private readonly Dictionary<object, object> _userState;

    public Metadata? ResponseHeaders { get; private set; }

    private TestServerCallContext(Metadata requestHeaders, CancellationToken cancellationToken)
    {
        RequestHeadersCore = requestHeaders;
        CancellationTokenCore = cancellationToken;
        ResponseTrailersCore = new Metadata();
        AuthContextCore = new AuthContext(
            string.Empty,
            new Dictionary<string, List<AuthProperty>>()
        );
        _userState = new Dictionary<object, object>();
    }

    protected override string MethodCore => "MethodName";
    protected override string HostCore => "HostName";
    protected override string PeerCore => "PeerName";
    protected override DateTime DeadlineCore { get; }
    protected override Metadata RequestHeadersCore { get; }
    protected override CancellationToken CancellationTokenCore { get; }
    protected override Metadata ResponseTrailersCore { get; }
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore { get; }

    protected override ContextPropagationToken CreatePropagationTokenCore(
        ContextPropagationOptions? options
    )
    {
        throw new NotImplementedException();
    }

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
    {
        if (ResponseHeaders != null)
        {
            throw new InvalidOperationException("Response headers have already been written.");
        }

        ResponseHeaders = responseHeaders;
        return Task.CompletedTask;
    }

    protected override IDictionary<object, object> UserStateCore => _userState;

    public static TestServerCallContext Create(
        Metadata? requestHeaders = null,
        CancellationToken cancellationToken = default
    )
    {
        return new TestServerCallContext(requestHeaders ?? new Metadata(), cancellationToken);
    }
}
