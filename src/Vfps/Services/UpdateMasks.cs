using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Vfps.Services;

/// <summary>
/// Resolves an AIP-134 (https://google.aip.dev/134) update mask to the fields an Update call
/// changes - shared by every gRPC Update so they all read a mask the same way.
/// </summary>
internal static class UpdateMasks
{
    /// <summary>
    /// The fields of <paramref name="resource"/> an update with <paramref name="mask"/> changes.
    /// <list type="bullet">
    /// <item>No mask, or an empty one: every updatable field that is set in the request.</item>
    /// <item><c>*</c>: every updatable field, set or not - a full replacement of what can change.</item>
    /// <item>Otherwise: exactly the named fields. One that isn't set in the request is cleared.</item>
    /// </list>
    /// <paramref name="updatable"/> holds the snake_case names of the fields that may change.
    /// Each must have explicit presence (<c>optional</c>), since "set in the request" is what
    /// decides the maskless case.
    /// </summary>
    /// <exception cref="RpcException">
    /// INVALID_ARGUMENT when the mask names a field that isn't updatable, or combines <c>*</c>
    /// with anything else.
    /// </exception>
    public static IReadOnlySet<string> Resolve(
        FieldMask? mask,
        IMessage resource,
        IReadOnlyCollection<string> updatable
    )
    {
        if (mask is null || mask.Paths.Count == 0)
        {
            return updatable
                .Where(path =>
                    resource.Descriptor.FindFieldByName(path).Accessor.HasValue(resource)
                )
                .ToHashSet(StringComparer.Ordinal);
        }

        // Paths are snake_case in proto, but the REST API's ?updateMask=... arrives exactly as
        // typed - and in the JSON mapping a FieldMask is lowerCamelCase, which is what a REST
        // caller writes. JSON transcoding doesn't convert a query parameter the way it would a
        // body, so accept either spelling.
        var paths = mask.Paths.Select(ToSnakeCase).ToList();

        if (paths.Contains("*"))
        {
            if (paths.Count > 1)
            {
                throw InvalidArgument("update_mask can't combine \"*\" with other paths.");
            }

            return updatable.ToHashSet(StringComparer.Ordinal);
        }

        // Rejected rather than ignored: silently skipping a field the caller explicitly asked to
        // change would report a success that didn't happen.
        var notUpdatable = paths.Where(path => !updatable.Contains(path)).ToList();
        if (notUpdatable.Count > 0)
        {
            throw InvalidArgument(
                $"update_mask names fields of {resource.Descriptor.Name} that can't be updated: "
                    + $"{string.Join(", ", notUpdatable)}. Updatable: {string.Join(", ", updatable)}."
            );
        }

        return paths.ToHashSet(StringComparer.Ordinal);
    }

    private static string ToSnakeCase(string path)
    {
        var snake = new System.Text.StringBuilder(path.Length + 4);
        foreach (var c in path)
        {
            if (char.IsAsciiLetterUpper(c))
            {
                snake.Append('_').Append(char.ToLowerInvariant(c));
            }
            else
            {
                snake.Append(c);
            }
        }

        return snake.ToString();
    }

    private static RpcException InvalidArgument(string message) =>
        new(new Status(StatusCode.InvalidArgument, message));
}
