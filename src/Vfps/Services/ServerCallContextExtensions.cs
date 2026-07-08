using System.Security.Claims;
using Grpc.Core;

namespace Vfps.Services;

internal static class ServerCallContextExtensions
{
    /// <summary>
    /// The caller's <see cref="ClaimsPrincipal"/>, or an empty (anonymous) one if there's no
    /// HTTP context - e.g. in unit tests that construct a <see cref="ServerCallContext"/> directly
    /// without going through the real ASP.NET Core gRPC pipeline (where GetHttpContext() throws
    /// rather than returning null).
    /// </summary>
    public static ClaimsPrincipal GetUser(this ServerCallContext context)
    {
        try
        {
            return context.GetHttpContext()?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        }
        catch (InvalidOperationException)
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }
    }
}
