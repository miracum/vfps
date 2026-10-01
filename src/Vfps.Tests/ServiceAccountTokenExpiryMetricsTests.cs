using System.Diagnostics.Metrics;
using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using Vfps.Data.Models;
using Vfps.Metrics;
using Vfps.Tests.ServiceTests;

namespace Vfps.Tests.ServiceAccountTokenExpiryMetricsTests;

public class ServiceAccountTokenExpiryMetricsTests : ServiceTestBase
{
    private const string GaugeName = "vfps.service_account.token.expiration.timestamp.seconds";

    private ServiceAccountTokenExpiryMetricsBackgroundService CreateSut(
        IAccessTokenRepository? tokenRepository = null,
        TimeSpan? pollInterval = null
    ) =>
        new(
            tokenRepository ?? new AccessTokenRepository(ContextFactory),
            NullLogger<ServiceAccountTokenExpiryMetricsBackgroundService>.Instance,
            pollInterval
        );

    private async Task<string> SeedServiceAccountAsync()
    {
        // Unique per test - the instrument is static, so this keeps tests from reading each other's
        // series. Lower-case, like every real service account name.
        var name = $"expiry-metrics-test-{Guid.NewGuid():N}";
        InMemoryPseudonymContext.ServiceAccounts.Add(
            new ServiceAccount
            {
                Name = name,
                CreatedBy = "admin-subject",
                CreatedAt = DateTimeOffset.UtcNow,
                LastUpdatedAt = DateTimeOffset.UtcNow,
            }
        );
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        return name;
    }

    private async Task<AccessToken> AddTokenAsync(AccessToken token)
    {
        InMemoryPseudonymContext.AccessTokens.Add(token);
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        return token;
    }

    /// <summary>
    /// Collects one observation from the gauge, keyed by its token_id tag, with every tag the
    /// measurement carried.
    /// </summary>
    private static Dictionary<string, (long Value, Dictionary<string, object?> Tags)> ObserveGauge()
    {
        var observed = new Dictionary<string, (long, Dictionary<string, object?>)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter == Program.Meter && instrument.Name == GaugeName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, measurement, tags, _) =>
            {
                var byKey = new Dictionary<string, object?>();
                foreach (var tag in tags)
                {
                    byKey[tag.Key] = tag.Value;
                }

                if (byKey.GetValueOrDefault("token_id") is string tokenId)
                {
                    observed[tokenId] = (measurement, byKey);
                }
            }
        );
        listener.Start();
        listener.RecordObservableInstruments();

        return observed;
    }

    [Fact]
    public async Task RefreshAsync_ShouldReportEachServiceAccountTokensExpiry()
    {
        var accountName = await SeedServiceAccountAsync();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(10);
        var (token, _) = Tokens.ForServiceAccount(accountName, expiresAt: expiresAt);
        token.Name = "nightly ETL run";
        await AddTokenAsync(token);

        await CreateSut().RefreshAsync(TestContext.Current.CancellationToken);

        var (value, tags) = ObserveGauge()[token.TokenId];
        value.Should().Be(expiresAt.ToUnixTimeSeconds());
        tags["service_account"].Should().Be(accountName);
        tags["token_name"].Should().Be("nightly ETL run");
    }

    [Fact]
    public async Task RefreshAsync_ForAnExpiredToken_ShouldKeepReportingItUntilItIsRevoked()
    {
        // The series has to outlive the token: if it disappeared on expiry, the alert watching it
        // would resolve at exactly the moment the credential stopped working.
        var accountName = await SeedServiceAccountAsync();
        var (expired, _) = Tokens.ForServiceAccount(
            accountName,
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1)
        );
        var (revoked, _) = Tokens.ForServiceAccount(
            accountName,
            revokedAt: DateTimeOffset.UtcNow.AddDays(-2)
        );
        await AddTokenAsync(expired);
        await AddTokenAsync(revoked);

        await CreateSut().RefreshAsync(TestContext.Current.CancellationToken);

        var observed = ObserveGauge();
        observed.Should().ContainKey(expired.TokenId);
        observed.Should().NotContainKey(revoked.TokenId);
    }

    [Fact]
    public async Task RefreshAsync_AfterATokenIsRevoked_ShouldStopReportingIt()
    {
        // Revoking the old token after rolling out its replacement is what clears the alert.
        var accountName = await SeedServiceAccountAsync();
        var (token, _) = Tokens.ForServiceAccount(accountName);
        await AddTokenAsync(token);
        var sut = CreateSut();

        await sut.RefreshAsync(TestContext.Current.CancellationToken);
        ObserveGauge().Should().ContainKey(token.TokenId);

        await new AccessTokenRepository(ContextFactory).RevokeAsync(
            token.Id,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken
        );
        await sut.RefreshAsync(TestContext.Current.CancellationToken);

        ObserveGauge().Should().NotContainKey(token.TokenId);
    }

    [Fact]
    public async Task RefreshAsync_ShouldNotReportPersonalTokens()
    {
        var (personal, _) = Tokens.Personal();
        await AddTokenAsync(personal);

        await CreateSut().RefreshAsync(TestContext.Current.CancellationToken);

        ObserveGauge().Should().NotContainKey(personal.TokenId);
    }

    [Fact]
    public async Task RefreshAsync_WhenTheTokensAreUnreadable_ShouldKeepThePreviousValues()
    {
        // Clearing on a failed read would make every series vanish on a transient database blip,
        // silently resolving any expiry alert that was firing.
        var accountName = await SeedServiceAccountAsync();
        var (token, _) = Tokens.ForServiceAccount(accountName);
        await AddTokenAsync(token);
        await CreateSut().RefreshAsync(TestContext.Current.CancellationToken);

        var failingRepository = A.Fake<IAccessTokenRepository>();
        A.CallTo(() => failingRepository.GetAllUnrevokedAsync(A<CancellationToken>._))
            .Throws(new InvalidOperationException("database is down"));

        var act = async () =>
            await CreateSut(failingRepository).RefreshAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        ObserveGauge().Should().ContainKey(token.TokenId);
    }

    [Fact]
    public async Task BackgroundService_ShouldPublishOnItsOwnTimer()
    {
        var accountName = await SeedServiceAccountAsync();
        var (token, _) = Tokens.ForServiceAccount(accountName);
        await AddTokenAsync(token);

        var sut = CreateSut(pollInterval: TimeSpan.FromMilliseconds(20));

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        ObserveGauge().Should().ContainKey(token.TokenId);
    }
}
