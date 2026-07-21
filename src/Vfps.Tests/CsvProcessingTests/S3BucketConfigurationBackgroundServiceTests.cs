using Amazon.S3;
using Amazon.S3.Model;
using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vfps.AppServices;
using Vfps.Config;
using Vfps.CsvProcessing;

namespace Vfps.Tests.CsvProcessingTests;

public class S3BucketConfigurationBackgroundServiceTests
{
    private const string Bucket = "test-bucket";

    private static S3BucketConfigurationBackgroundService CreateSut(
        IAmazonS3 s3,
        int objectRetentionDays = 0,
        List<string>? allowedOrigins = null
    ) =>
        new(
            s3,
            Options.Create(
                new S3Config
                {
                    Bucket = Bucket,
                    ObjectRetentionDays = objectRetentionDays,
                    AllowedOrigins = allowedOrigins ?? [],
                }
            ),
            NullLogger<S3BucketConfigurationBackgroundService>.Instance
        );

    [Fact]
    public async Task ExecuteAsync_WithPositiveRetentionDays_ShouldApplyLifecycleRuleForCsvJobsPrefix()
    {
        var s3 = A.Fake<IAmazonS3>();
        var sut = CreateSut(s3, objectRetentionDays: 30);

        await sut.StartAsync(CancellationToken.None);
        await sut.ExecuteTask!;
        await sut.StopAsync(CancellationToken.None);

        A.CallTo(() =>
                s3.PutLifecycleConfigurationAsync(
                    A<PutLifecycleConfigurationRequest>.That.Matches(r =>
                        r.BucketName == Bucket
                        && r.Configuration.Rules.Count == 1
                        && r.Configuration.Rules[0].Status == LifecycleRuleStatus.Enabled
                        && r.Configuration.Rules[0].Expiration.Days == 30
                        && (
                            (LifecyclePrefixPredicate)
                                r.Configuration.Rules[0].Filter.LifecycleFilterPredicate
                        ).Prefix == PseudonymizationJobAppService.S3ObjectKeyPrefix
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExecuteAsync_WhenPutLifecycleConfigurationFails_ShouldNotThrow()
    {
        // An unhandled BackgroundService exception stops the entire host, not just this
        // feature - a bucket permissions issue or a transient endpoint error (e.g. the
        // real-world MinIO "Missing required header for this request: Content-Md5" case this
        // guards against) must not be allowed to take the whole app down.
        var s3 = A.Fake<IAmazonS3>();
        A.CallTo(() =>
                s3.PutLifecycleConfigurationAsync(
                    A<PutLifecycleConfigurationRequest>._,
                    A<CancellationToken>._
                )
            )
            .Throws(
                new AmazonS3Exception("Missing required header for this request: Content-Md5.")
            );
        var sut = CreateSut(s3, objectRetentionDays: 30);

        await sut.StartAsync(CancellationToken.None);
        var act = async () => await sut.ExecuteTask!;

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExecuteAsync_WithZeroRetentionDays_ShouldNotTouchLifecycleConfiguration()
    {
        var s3 = A.Fake<IAmazonS3>();
        var sut = CreateSut(s3, objectRetentionDays: 0);

        await sut.StartAsync(CancellationToken.None);
        await sut.ExecuteTask!;
        await sut.StopAsync(CancellationToken.None);

        A.CallTo(() =>
                s3.PutLifecycleConfigurationAsync(
                    A<PutLifecycleConfigurationRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_WithAllowedOrigins_ShouldApplyCorsRuleForBucket()
    {
        var s3 = A.Fake<IAmazonS3>();
        var sut = CreateSut(s3, allowedOrigins: ["https://vfps.example.org"]);

        await sut.StartAsync(CancellationToken.None);
        await sut.ExecuteTask!;
        await sut.StopAsync(CancellationToken.None);

        A.CallTo(() =>
                s3.PutCORSConfigurationAsync(
                    A<PutCORSConfigurationRequest>.That.Matches(r =>
                        r.BucketName == Bucket
                        && r.Configuration.Rules.Count == 1
                        && r.Configuration.Rules[0]
                            .AllowedOrigins.SequenceEqual(new[] { "https://vfps.example.org" })
                        && r.Configuration.Rules[0]
                            .AllowedMethods.SequenceEqual(new[] { "GET", "PUT" })
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExecuteAsync_WhenPutCorsConfigurationFails_ShouldNotThrow()
    {
        var s3 = A.Fake<IAmazonS3>();
        A.CallTo(() =>
                s3.PutCORSConfigurationAsync(
                    A<PutCORSConfigurationRequest>._,
                    A<CancellationToken>._
                )
            )
            .Throws(new AmazonS3Exception("access denied"));
        var sut = CreateSut(s3, allowedOrigins: ["https://vfps.example.org"]);

        await sut.StartAsync(CancellationToken.None);
        var act = async () => await sut.ExecuteTask!;

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExecuteAsync_WithNoAllowedOrigins_ShouldNotTouchCorsConfiguration()
    {
        var s3 = A.Fake<IAmazonS3>();
        var sut = CreateSut(s3);

        await sut.StartAsync(CancellationToken.None);
        await sut.ExecuteTask!;
        await sut.StopAsync(CancellationToken.None);

        A.CallTo(() =>
                s3.PutCORSConfigurationAsync(
                    A<PutCORSConfigurationRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_WhenLifecycleConfigurationFails_ShouldStillApplyCorsConfiguration()
    {
        // The two are independent bucket configuration steps - a failure applying one (e.g. a
        // permissions issue scoped only to lifecycle rules) shouldn't skip the other.
        var s3 = A.Fake<IAmazonS3>();
        A.CallTo(() =>
                s3.PutLifecycleConfigurationAsync(
                    A<PutLifecycleConfigurationRequest>._,
                    A<CancellationToken>._
                )
            )
            .Throws(new AmazonS3Exception("access denied"));
        var sut = CreateSut(
            s3,
            objectRetentionDays: 30,
            allowedOrigins: ["https://vfps.example.org"]
        );

        await sut.StartAsync(CancellationToken.None);
        await sut.ExecuteTask!;
        await sut.StopAsync(CancellationToken.None);

        A.CallTo(() =>
                s3.PutCORSConfigurationAsync(
                    A<PutCORSConfigurationRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }
}
