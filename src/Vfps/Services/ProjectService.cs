using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vfps.AppServices;
using Vfps.Authorization;
using Vfps.Protos;

namespace Vfps.Services;

/// <inheritdoc/>
public class ProjectService(IProjectAppService projectAppService)
    : Protos.ProjectService.ProjectServiceBase
{
    /// <inheritdoc/>
    public override async Task<ProjectServiceCreateResponse> Create(
        ProjectServiceCreateRequest request,
        ServerCallContext context
    )
    {
        var projectToCreate = new Data.Models.Project
        {
            Name = request.Name,
            Description = request.HasDescription ? request.Description : null,
        };

        Data.Models.Project created;
        try
        {
            created = await projectAppService.CreateAsync(
                projectToCreate,
                context.GetUser(),
                context.CancellationToken
            );
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (ProjectAlreadyExistsException ex)
        {
            throw new RpcException(
                new Status(StatusCode.AlreadyExists, ex.Message),
                new Metadata { { "Project", ex.ProjectName } }
            );
        }
        catch (ForbiddenException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }

        return new ProjectServiceCreateResponse { Project = ToProto(created) };
    }

    /// <inheritdoc/>
    public override async Task<ProjectServiceGetResponse> Get(
        ProjectServiceGetRequest request,
        ServerCallContext context
    )
    {
        try
        {
            var project = await projectAppService.GetAsync(
                request.Name,
                context.GetUser(),
                context.CancellationToken
            );

            return new ProjectServiceGetResponse { Project = ToProto(project) };
        }
        catch (Exception ex) when (TryMap(ex, out var rpcException))
        {
            throw rpcException;
        }
    }

    /// <inheritdoc/>
    public override async Task<ProjectServiceGetAllResponse> GetAll(
        ProjectServiceGetAllRequest request,
        ServerCallContext context
    )
    {
        var projects = await projectAppService.GetAllAsync(
            context.GetUser(),
            context.CancellationToken
        );

        var response = new ProjectServiceGetAllResponse();
        response.Projects.AddRange(projects.Select(ToProto));

        return response;
    }

    /// <inheritdoc/>
    public override async Task<ProjectServiceUpdateResponse> Update(
        ProjectServiceUpdateRequest request,
        ServerCallContext context
    )
    {
        try
        {
            var updated = await projectAppService.UpdateAsync(
                request.Name,
                request.HasDescription ? request.Description : null,
                context.GetUser(),
                context.CancellationToken
            );

            return new ProjectServiceUpdateResponse { Project = ToProto(updated) };
        }
        catch (Exception ex) when (TryMap(ex, out var rpcException))
        {
            throw rpcException;
        }
    }

    /// <inheritdoc/>
    public override async Task<ProjectServiceDeleteResponse> Delete(
        ProjectServiceDeleteRequest request,
        ServerCallContext context
    )
    {
        try
        {
            await projectAppService.DeleteAsync(
                request.Name,
                context.GetUser(),
                context.CancellationToken
            );
        }
        catch (Exception ex) when (TryMap(ex, out var rpcException))
        {
            throw rpcException;
        }

        return new ProjectServiceDeleteResponse();
    }

    /// <inheritdoc/>
    public override async Task<ProjectServiceAddNamespaceResponse> AddNamespace(
        ProjectServiceAddNamespaceRequest request,
        ServerCallContext context
    )
    {
        try
        {
            await projectAppService.AddNamespaceAsync(
                request.Name,
                request.NamespaceName,
                context.GetUser(),
                context.CancellationToken
            );
        }
        catch (Exception ex) when (TryMap(ex, out var rpcException))
        {
            throw rpcException;
        }

        return new ProjectServiceAddNamespaceResponse();
    }

    /// <inheritdoc/>
    public override async Task<ProjectServiceRemoveNamespaceResponse> RemoveNamespace(
        ProjectServiceRemoveNamespaceRequest request,
        ServerCallContext context
    )
    {
        try
        {
            await projectAppService.RemoveNamespaceAsync(
                request.Name,
                request.NamespaceName,
                context.GetUser(),
                context.CancellationToken
            );
        }
        catch (Exception ex) when (TryMap(ex, out var rpcException))
        {
            throw rpcException;
        }

        return new ProjectServiceRemoveNamespaceResponse();
    }

    /// <summary>
    /// The errors every project call can end in, mapped to the status the namespace service uses
    /// for the same condition.
    /// </summary>
    private static bool TryMap(Exception exception, out RpcException rpcException)
    {
        rpcException = exception switch
        {
            ProjectNotFoundException ex => new RpcException(
                new Status(StatusCode.NotFound, ex.Message),
                new Metadata { { "Project", ex.ProjectName } }
            ),
            NamespaceNotFoundException ex => new RpcException(
                new Status(StatusCode.NotFound, ex.Message),
                new Metadata { { "Namespace", ex.NamespaceName } }
            ),
            ForbiddenException ex => new RpcException(
                new Status(StatusCode.PermissionDenied, ex.Message)
            ),
            _ => null!,
        };

        return rpcException is not null;
    }

    private static Project ToProto(Data.Models.Project project)
    {
        var proto = new Project
        {
            Name = project.Name,
            Meta = new Meta
            {
                CreatedAt = Timestamp.FromDateTimeOffset(project.CreatedAt),
                LastUpdatedAt = Timestamp.FromDateTimeOffset(project.LastUpdatedAt),
            },
        };

        // Absent rather than empty for a NULL column, and never assigned null: the generated
        // setter rejects it - see NamespaceService.ToProto.
        if (project.Description is not null)
        {
            proto.Description = project.Description;
        }

        proto.NamespaceNames.AddRange(
            project.Memberships.Select(m => m.NamespaceName).Order(StringComparer.Ordinal)
        );

        return proto;
    }
}
