using System.Security.Claims;
using Amazon.S3;
using Amazon.S3.Model;
using FakeItEasy;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Options;
using Vfps.Config;
using Vfps.CsvProcessing;
using Vfps.Data.Models;

namespace Vfps.Tests.ServiceTests;

public class PseudonymizationJobAppServiceTests : ServiceTestBase
{
    private const string Bucket = "test-bucket";

    private static ClaimsPrincipal UserWithRoles(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim("roles", r))));

    // "sub", not ClaimTypes.NameIdentifier - Program.cs sets MapInboundClaims = false on both
    // the OIDC and JWT bearer handlers, so a real token's short "sub" claim name survives
    // unmapped, matching what GetSubject()/ClaimsPrincipalExtensions actually reads.
    private static ClaimsPrincipal UserWithSubject(string subject, params string[] roles) =>
        new(
            new ClaimsIdentity(
                roles.Select(r => new Claim("roles", r)).Append(new Claim("sub", subject))
            )
        );

    private (
        PseudonymizationJobAppService Sut,
        PseudonymizationJobRepository Repository,
        IAmazonS3 S3,
        IBackgroundJobClient BackgroundJobClient
    ) CreateSut(AuthorizationConfig? config = null)
    {
        var repository = new PseudonymizationJobRepository(InMemoryPseudonymContext);
        var s3 = A.Fake<IAmazonS3>();
        A.CallTo(() => s3.GetPreSignedURLAsync(A<GetPreSignedUrlRequest>._))
            .Returns("https://example.invalid/presigned");
        A.CallTo(() => s3.GetObjectMetadataAsync(Bucket, A<string>._, A<CancellationToken>._))
            .Returns(new GetObjectMetadataResponse { ContentLength = 1234 });

        var backgroundJobClient = A.Fake<IBackgroundJobClient>();
        A.CallTo(() => backgroundJobClient.Create(A<Job>._, A<IState>._))
            .Returns("hangfire-job-id");

        var sut = new PseudonymizationJobAppService(
            repository,
            CreatePermissionChecker(config),
            s3,
            backgroundJobClient,
            Options.Create(new S3Config { Bucket = Bucket })
        );

        return (sut, repository, s3, backgroundJobClient);
    }

    [Fact]
    public async Task CreateJobAsync_WithoutWriteAccessToNamespace_ShouldThrowForbidden()
    {
        var (sut, _, _, _) = CreateSut(new AuthorizationConfig { IsEnabled = true });

        var request = new CreateCsvJobRequest(
            "utf-8",
            ",",
            true,
            [new ColumnMapping { SourceColumn = "col1", Namespace = "existingNamespace" }]
        );

        var act = () => sut.CreateJobAsync(request, UserWithRoles(), CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task CreateJobAsync_WithWriteAccess_ShouldCreateJobAndReturnPresignedUrl()
    {
        var (sut, _, _, _) = CreateSut(
            new AuthorizationConfig
            {
                IsEnabled = true,
                NamespaceRules =
                [
                    new NamespaceRule
                    {
                        Namespace = "existingNamespace",
                        WriteRoles = ["can-write"],
                    },
                ],
            }
        );

        var request = new CreateCsvJobRequest(
            "utf-8",
            ",",
            true,
            [new ColumnMapping { SourceColumn = "col1", Namespace = "existingNamespace" }]
        );

        var (job, uploadUrl) = await sut.CreateJobAsync(
            request,
            UserWithRoles("can-write"),
            CancellationToken.None
        );

        job.Status.Should().Be(PseudonymizationJobStatus.AwaitingUpload);
        job.Direction.Should().Be(PseudonymizationJobDirection.Pseudonymize);
        job.InputObjectKey.Should().Contain(job.Id.ToString());
        uploadUrl.Should().Be("https://example.invalid/presigned");
    }

    [Fact]
    public async Task CreateJobAsync_WithOriginalFileName_ShouldStoreItOnTheJob()
    {
        var (sut, _, _, _) = CreateSut();

        var request = new CreateCsvJobRequest(
            "utf-8",
            ",",
            true,
            [new ColumnMapping { SourceColumn = "col1", Namespace = "existingNamespace" }],
            OriginalFileName: "patients_2026.csv"
        );

        var (job, _) = await sut.CreateJobAsync(
            request,
            UserWithSubject("alice"),
            CancellationToken.None
        );

        job.OriginalFileName.Should().Be("patients_2026.csv");
    }

    [Fact]
    public async Task CreateJobAsync_WithoutOriginalFileName_ShouldStoreNull()
    {
        var (sut, _, _, _) = CreateSut();

        var request = new CreateCsvJobRequest(
            "utf-8",
            ",",
            true,
            [new ColumnMapping { SourceColumn = "col1", Namespace = "existingNamespace" }]
        );

        var (job, _) = await sut.CreateJobAsync(
            request,
            UserWithSubject("alice"),
            CancellationToken.None
        );

        job.OriginalFileName.Should().BeNull();
    }

    [Fact]
    public async Task CreateJobAsync_DepseudonymizeWithWriteAccessButNotReverseLookupAccess_ShouldThrowForbidden()
    {
        // Depseudonymize reveals original values, so it must be gated on reverse-lookup access,
        // not write access - even though write access alone is enough for a Pseudonymize job.
        var (sut, _, _, _) = CreateSut(
            new AuthorizationConfig
            {
                IsEnabled = true,
                NamespaceRules =
                [
                    new NamespaceRule
                    {
                        Namespace = "existingNamespace",
                        WriteRoles = ["can-write"],
                    },
                ],
            }
        );

        var request = new CreateCsvJobRequest(
            "utf-8",
            ",",
            true,
            [new ColumnMapping { SourceColumn = "col1", Namespace = "existingNamespace" }],
            PseudonymizationJobDirection.Depseudonymize
        );

        var act = () =>
            sut.CreateJobAsync(request, UserWithRoles("can-write"), CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task CreateJobAsync_DepseudonymizeWithReverseLookupAccess_ShouldCreateJob()
    {
        var (sut, _, _, _) = CreateSut(
            new AuthorizationConfig
            {
                IsEnabled = true,
                NamespaceRules =
                [
                    new NamespaceRule
                    {
                        Namespace = "existingNamespace",
                        ReverseLookupRoles = ["can-reverse-lookup"],
                    },
                ],
            }
        );

        var request = new CreateCsvJobRequest(
            "utf-8",
            ",",
            true,
            [new ColumnMapping { SourceColumn = "col1", Namespace = "existingNamespace" }],
            PseudonymizationJobDirection.Depseudonymize
        );

        var (job, _) = await sut.CreateJobAsync(
            request,
            UserWithRoles("can-reverse-lookup"),
            CancellationToken.None
        );

        job.Direction.Should().Be(PseudonymizationJobDirection.Depseudonymize);
    }

    [Fact]
    public async Task GetAsync_AsNonCreatorNonAdmin_ShouldThrowForbidden()
    {
        var (sut, _, _, _) = CreateSut(
            new AuthorizationConfig
            {
                IsEnabled = true,
                NamespaceRules =
                [
                    new NamespaceRule
                    {
                        Namespace = "existingNamespace",
                        WriteRoles = ["can-write"],
                    },
                ],
            }
        );

        var request = new CreateCsvJobRequest(
            "utf-8",
            ",",
            true,
            [new ColumnMapping { SourceColumn = "col1", Namespace = "existingNamespace" }]
        );
        var (job, _) = await sut.CreateJobAsync(
            request,
            UserWithSubject("alice", "can-write"),
            CancellationToken.None
        );

        var act = () =>
            sut.GetAsync(job.Id, UserWithSubject("bob", "can-write"), CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ListAsync_AsNonAdmin_ShouldOnlyReturnOwnJobs()
    {
        var (sut, _, _, _) = CreateSut(
            new AuthorizationConfig
            {
                IsEnabled = true,
                NamespaceRules =
                [
                    new NamespaceRule
                    {
                        Namespace = "existingNamespace",
                        WriteRoles = ["can-write"],
                    },
                ],
            }
        );

        var request = new CreateCsvJobRequest(
            "utf-8",
            ",",
            true,
            [new ColumnMapping { SourceColumn = "col1", Namespace = "existingNamespace" }]
        );
        await sut.CreateJobAsync(
            request,
            UserWithSubject("alice", "can-write"),
            CancellationToken.None
        );
        await sut.CreateJobAsync(
            request,
            UserWithSubject("bob", "can-write"),
            CancellationToken.None
        );

        var result = await sut.ListAsync(
            UserWithSubject("alice", "can-write"),
            CancellationToken.None
        );

        result.Should().ContainSingle();
        result.Should().OnlyContain(j => j.CreatedBy == "alice");
    }

    [Fact]
    public async Task CancelAsync_OnAwaitingUploadJob_ShouldSetCancelledStatus()
    {
        var (sut, repository, _, _) = CreateSut();

        var request = new CreateCsvJobRequest(
            "utf-8",
            ",",
            true,
            [new ColumnMapping { SourceColumn = "col1", Namespace = "existingNamespace" }]
        );
        var (job, _) = await sut.CreateJobAsync(
            request,
            UserWithSubject("alice"),
            CancellationToken.None
        );

        await sut.CancelAsync(job.Id, UserWithSubject("alice"), CancellationToken.None);

        var updated = await repository.FindAsync(job.Id, CancellationToken.None);
        updated!.Status.Should().Be(PseudonymizationJobStatus.Cancelled);
    }

    [Fact]
    public async Task ClearFinishedAsync_AsNonAdmin_ShouldOnlyDeleteOwnFinishedJobs()
    {
        var (sut, repository, _, _) = CreateSut(
            new AuthorizationConfig
            {
                IsEnabled = true,
                NamespaceRules =
                [
                    new NamespaceRule
                    {
                        Namespace = "existingNamespace",
                        WriteRoles = ["can-write"],
                    },
                ],
            }
        );

        var request = new CreateCsvJobRequest(
            "utf-8",
            ",",
            true,
            [new ColumnMapping { SourceColumn = "col1", Namespace = "existingNamespace" }]
        );
        var (aliceJob, _) = await sut.CreateJobAsync(
            request,
            UserWithSubject("alice", "can-write"),
            CancellationToken.None
        );
        var (bobJob, _) = await sut.CreateJobAsync(
            request,
            UserWithSubject("bob", "can-write"),
            CancellationToken.None
        );
        await sut.CancelAsync(
            aliceJob.Id,
            UserWithSubject("alice", "can-write"),
            CancellationToken.None
        );
        await sut.CancelAsync(
            bobJob.Id,
            UserWithSubject("bob", "can-write"),
            CancellationToken.None
        );

        var deletedCount = await sut.ClearFinishedAsync(
            UserWithSubject("alice", "can-write"),
            CancellationToken.None
        );

        deletedCount.Should().Be(1);
        (await repository.FindAsync(aliceJob.Id, CancellationToken.None)).Should().BeNull();
        (await repository.FindAsync(bobJob.Id, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task ClearFinishedAsync_AsAdmin_ShouldDeleteEveryonesFinishedJobs()
    {
        var (sut, repository, _, _) = CreateSut(
            new AuthorizationConfig
            {
                IsEnabled = true,
                AdminRoles = ["admin"],
                NamespaceRules =
                [
                    new NamespaceRule
                    {
                        Namespace = "existingNamespace",
                        WriteRoles = ["can-write"],
                    },
                ],
            }
        );

        var request = new CreateCsvJobRequest(
            "utf-8",
            ",",
            true,
            [new ColumnMapping { SourceColumn = "col1", Namespace = "existingNamespace" }]
        );
        var (aliceJob, _) = await sut.CreateJobAsync(
            request,
            UserWithSubject("alice", "can-write"),
            CancellationToken.None
        );
        var (bobJob, _) = await sut.CreateJobAsync(
            request,
            UserWithSubject("bob", "can-write"),
            CancellationToken.None
        );
        await sut.CancelAsync(
            aliceJob.Id,
            UserWithSubject("alice", "can-write"),
            CancellationToken.None
        );
        await sut.CancelAsync(
            bobJob.Id,
            UserWithSubject("bob", "can-write"),
            CancellationToken.None
        );

        var deletedCount = await sut.ClearFinishedAsync(
            UserWithSubject("admin-user", "admin"),
            CancellationToken.None
        );

        deletedCount.Should().Be(2);
        (await repository.FindAsync(aliceJob.Id, CancellationToken.None)).Should().BeNull();
        (await repository.FindAsync(bobJob.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ClearFinishedAsync_ShouldNotDeleteJobsStillInProgress()
    {
        var (sut, repository, _, _) = CreateSut();

        var request = new CreateCsvJobRequest(
            "utf-8",
            ",",
            true,
            [new ColumnMapping { SourceColumn = "col1", Namespace = "existingNamespace" }]
        );
        var (job, _) = await sut.CreateJobAsync(
            request,
            UserWithSubject("alice"),
            CancellationToken.None
        );

        var deletedCount = await sut.ClearFinishedAsync(
            UserWithSubject("alice"),
            CancellationToken.None
        );

        deletedCount.Should().Be(0);
        (await repository.FindAsync(job.Id, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task GetDownloadUrlAsync_OnNonCompletedJob_ShouldThrow()
    {
        var (sut, _, _, _) = CreateSut();

        var request = new CreateCsvJobRequest(
            "utf-8",
            ",",
            true,
            [new ColumnMapping { SourceColumn = "col1", Namespace = "existingNamespace" }]
        );
        var (job, _) = await sut.CreateJobAsync(
            request,
            UserWithSubject("alice"),
            CancellationToken.None
        );

        var act = () =>
            sut.GetDownloadUrlAsync(job.Id, UserWithSubject("alice"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
