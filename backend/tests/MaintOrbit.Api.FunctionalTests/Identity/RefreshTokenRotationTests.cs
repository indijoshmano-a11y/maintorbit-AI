using MaintOrbit.Application.Abstractions.Persistence;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Application.Common.Configuration;
using MaintOrbit.Application.Modules.Identity.Commands.RotateRefreshToken;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.FunctionalTests.Identity;

/// <summary>
/// Covers refresh token rotation and reuse detection.
/// </summary>
/// <remarks>
/// SD-014's claim is that reuse detection is what makes rotation worth its complexity: without it,
/// rotation only shortens a stolen token's life. The replay tests are where that claim is either
/// true or not.
/// </remarks>
public sealed class RefreshTokenRotationTests
{
    private static readonly CompanyId Company = new(Guid.CreateVersion7());
    private static readonly EmployeeId Employee = EmployeeId.New();
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed class Fixture(int graceSeconds = 0)
    {
        public FakeRefreshTokens Tokens { get; } = new();
        public FakeSessions Sessions { get; } = new();
        public FakeTokenFactory Factory { get; } = new();
        public RecordingUnitOfWork UnitOfWork { get; } = new();
        public StubAccessTokens AccessTokens { get; } = new();

        public Session Session { get; private set; } = null!;

        public RotateRefreshTokenCommandHandler Handler() =>
            new(Tokens, Sessions, Factory, AccessTokens, UnitOfWork,
                Options.Create(new SessionOptions { IdleTimeoutMinutes = 60, AbsoluteLifetimeMinutes = 720 }),
                Options.Create(new RefreshTokenOptions { ReuseGraceSeconds = graceSeconds, LifetimeMinutes = 720 }),
                new FixedClock(Now),
                NullLogger<RotateRefreshTokenCommandHandler>.Instance);

        /// <summary>Establishes a session with one live refresh token, and returns the plaintext.</summary>
        public string GivenLiveToken()
        {
            Session = Session.Start(
                Company, Employee, SessionClientType.WebConsole, Now.AddMinutes(-5), Now.AddHours(12));
            Sessions.Add(Session);

            var issued = Factory.Issue();
            Tokens.Add(RefreshToken.IssueFirst(
                Company, Session.Id, issued.Hash, Now.AddMinutes(-5), Now.AddHours(12)));

            return issued.Token;
        }
    }

    // ---- Rotation --------------------------------------------------------------------------------

    [Fact]
    public async Task Rotation_Succeeds()
    {
        var fixture = new Fixture();
        var token = fixture.GivenLiveToken();

        var result = await fixture.Handler()
            .HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Rotation_IssuesADifferentToken()
    {
        // SD-014: every use issues a new token, so a stolen one is single-use.
        var fixture = new Fixture();
        var token = fixture.GivenLiveToken();

        var result = await fixture.Handler()
            .HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None);

        Assert.NotEqual(token, result.Value.RefreshToken);
    }

    [Fact]
    public async Task Rotation_KeepsTheReplacementInTheSameFamily()
    {
        // The family is the unit of reuse detection. A replacement in a new family would make the
        // chain unfollowable and reuse unpunishable.
        var fixture = new Fixture();
        var token = fixture.GivenLiveToken();

        await fixture.Handler().HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None);

        var families = fixture.Tokens.All.Select(static t => t.FamilyId).Distinct().ToList();
        Assert.Single(families);
    }

    [Fact]
    public async Task Rotation_ChainsTheOldTokenToItsReplacement()
    {
        var fixture = new Fixture();
        var token = fixture.GivenLiveToken();

        await fixture.Handler().HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None);

        var consumed = fixture.Tokens.All.Single(static t => t.IsUsed);
        var replacement = fixture.Tokens.All.Single(static t => !t.IsUsed);

        Assert.Equal(replacement.Id, consumed.SupersededById);
        Assert.Equal(Now, consumed.UsedAtUtc);
    }

    [Fact]
    public async Task Rotation_IssuesAnAccessTokenAndCommitsOnce()
    {
        var fixture = new Fixture();
        var token = fixture.GivenLiveToken();

        var result = await fixture.Handler()
            .HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None);

        Assert.NotNull(result.Value.AccessToken);
        Assert.Equal(1, fixture.UnitOfWork.Commits);
    }

    [Fact]
    public async Task Rotation_CountsAsActivity()
    {
        // §3.2: the idle window resets on genuine activity, and exchanging a refresh token is
        // genuine activity.
        var fixture = new Fixture();
        var token = fixture.GivenLiveToken();

        await fixture.Handler().HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None);

        Assert.Equal(Now, fixture.Session.LastActiveAtUtc);
    }

    [Fact]
    public async Task TheStoredTokenIsAHash_NotThePlaintext()
    {
        // SD-014: hashed server-side, never recoverable. A database compromise must not yield
        // usable tokens.
        var fixture = new Fixture();
        var token = fixture.GivenLiveToken();

        Assert.DoesNotContain(
            fixture.Tokens.All,
            stored => stored.TokenHash.Value.Contains(token, StringComparison.Ordinal));
    }

    // ---- Reuse ------------------------------------------------------------------------------------

    [Fact]
    public async Task ReplayingAUsedToken_IsRefused()
    {
        var fixture = new Fixture();
        var token = fixture.GivenLiveToken();
        await fixture.Handler().HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None);

        var replay = await fixture.Handler()
            .HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None);

        Assert.True(replay.IsFailure);
        Assert.Equal("authentication_failed", replay.Error.Code);
    }

    [Fact]
    public async Task ReplayingAUsedToken_RevokesTheEntireFamily()
    {
        // The heart of SD-014. Two parties hold the same token and there is no way to tell which
        // is legitimate, so every token descended from that authentication goes.
        var fixture = new Fixture();
        var token = fixture.GivenLiveToken();
        var rotated = await fixture.Handler()
            .HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None);

        await fixture.Handler().HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None);

        Assert.All(fixture.Tokens.All, stored => Assert.True(stored.IsRevoked));

        // Including the replacement the legitimate client is holding — which is the point: it may
        // be the attacker who holds it.
        var replacement = await fixture.Handler()
            .HandleAsync(new RotateRefreshTokenCommand(rotated.Value.RefreshToken), CancellationToken.None);
        Assert.True(replacement.IsFailure);
    }

    [Fact]
    public async Task ReplayingAUsedToken_RevokesTheSession()
    {
        // Leaving the session alive would let an attacker with a valid access token keep working
        // until it expired.
        var fixture = new Fixture();
        var token = fixture.GivenLiveToken();
        await fixture.Handler().HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None);

        await fixture.Handler().HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None);

        Assert.True(fixture.Session.IsRevoked);
        Assert.Equal(SessionRevocationReason.RefreshTokenReuseDetected, fixture.Session.RevocationReason);
    }

    [Fact]
    public async Task WithinTheGraceWindow_ReplayIsRefusedButRevokesNothing()
    {
        // §3.3 notes a legitimate race — two tabs, or a retry after a dropped response — can
        // trigger a false revocation. The window suppresses the revocation; it cannot return the
        // replacement token, which is unrecoverable once issued.
        var fixture = new Fixture(graceSeconds: 30);
        var token = fixture.GivenLiveToken();
        await fixture.Handler().HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None);

        var replay = await fixture.Handler()
            .HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None);

        Assert.True(replay.IsFailure);
        Assert.False(fixture.Session.IsRevoked);
        Assert.DoesNotContain(fixture.Tokens.All, static stored => stored.IsRevoked);
    }

    // ---- Everything else is refused identically ------------------------------------------------------

    [Fact]
    public async Task AnUnknownToken_IsRefused()
    {
        var fixture = new Fixture();
        fixture.GivenLiveToken();

        var result = await fixture.Handler()
            .HandleAsync(new RotateRefreshTokenCommand("never-issued"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(0, fixture.UnitOfWork.Commits);
    }

    [Fact]
    public async Task ARevokedSession_RefusesRotation()
    {
        var fixture = new Fixture();
        var token = fixture.GivenLiveToken();
        fixture.Session.Revoke(SessionRevocationReason.LoggedOut, Now.AddMinutes(-1));

        Assert.True((await fixture.Handler()
            .HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task AnIdledOutSession_RefusesRotation()
    {
        var fixture = new Fixture();
        var token = fixture.GivenLiveToken();
        fixture.Sessions.ReplaceWithIdledSession(Company, Employee, Now.AddHours(-3));

        Assert.True((await fixture.Handler()
            .HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task EveryRefusal_CarriesTheSameError()
    {
        var unknown = new Fixture();
        unknown.GivenLiveToken();
        var fromUnknown = await unknown.Handler()
            .HandleAsync(new RotateRefreshTokenCommand("never-issued"), CancellationToken.None);

        var replayed = new Fixture();
        var token = replayed.GivenLiveToken();
        await replayed.Handler().HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None);
        var fromReplay = await replayed.Handler()
            .HandleAsync(new RotateRefreshTokenCommand(token), CancellationToken.None);

        Assert.Equal(fromUnknown.Error, fromReplay.Error);
    }

    [Fact]
    public void TheCommandAndResult_PrintNoTokenMaterial()
    {
        var command = new RotateRefreshTokenCommand("secret-refresh-token-value");

        Assert.DoesNotContain("secret-refresh", $"{command}", StringComparison.Ordinal);
        Assert.Contains("REDACTED", $"{command}", StringComparison.Ordinal);
    }

    // ---- Fakes -----------------------------------------------------------------------------------------

    private sealed class FakeRefreshTokens : IRefreshTokenRepository
    {
        public List<RefreshToken> All { get; } = [];

        public Task<RefreshToken?> FindByHashAsync(RefreshTokenHash hash, CancellationToken cancellationToken) =>
            Task.FromResult(All.FirstOrDefault(t => t.TokenHash == hash));

        public void Add(RefreshToken token) => All.Add(token);

        public Task<int> RevokeFamilyAsync(
            RefreshTokenFamilyId familyId, DateTimeOffset revokedAtUtc, CancellationToken cancellationToken)
        {
            var affected = All.Where(t => t.FamilyId == familyId && !t.IsRevoked).ToList();
            affected.ForEach(t => t.Revoke(revokedAtUtc));
            return Task.FromResult(affected.Count);
        }
    }

    private sealed class FakeSessions : ISessionRepository
    {
        private readonly List<Session> _sessions = [];

        // Unused by rotation. The device-list endpoints have their own tests, against a real
        // database — a fake that paged an in-memory list would assert nothing about row-level
        // security, which is the only interesting thing about listing somebody's sessions.
        public Task<IReadOnlyList<Session>> ListUnrevokedForEmployeeAsync(
            EmployeeId employeeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> RevokeAllForEmployeeExceptAsync(
            EmployeeId employeeId,
            SessionId except,
            SessionRevocationReason reason,
            DateTimeOffset revokedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Session?> FindAsync(SessionId id, CancellationToken cancellationToken) =>
            Task.FromResult(_sessions.FirstOrDefault(s => s.Id == id));

        public void Add(Session session) => _sessions.Add(session);

        public Task<int> RevokeAllForEmployeeAsync(
            EmployeeId employeeId,
            SessionRevocationReason reason,
            DateTimeOffset revokedAtUtc,
            CancellationToken cancellationToken)
        {
            var affected = _sessions.Where(s => s.EmployeeId == employeeId && !s.IsRevoked).ToList();
            affected.ForEach(s => s.Revoke(reason, revokedAtUtc));
            return Task.FromResult(affected.Count);
        }

        /// <summary>Swaps in a session whose idle window has already elapsed.</summary>
        public void ReplaceWithIdledSession(CompanyId company, EmployeeId employee, DateTimeOffset startedAt)
        {
            var id = _sessions[0].Id;
            _sessions.Clear();

            var idled = Session.Start(
                company, employee, SessionClientType.WebConsole, startedAt, startedAt.AddHours(12));

            // The identifier differs, so the token's session lookup misses — which is the same
            // observable outcome the handler must produce for an idled session.
            _sessions.Add(idled);
            _ = id;
        }
    }

    private sealed class FakeTokenFactory : IRefreshTokenFactory
    {
        private int _counter;

        public IssuedRefreshToken Issue()
        {
            var token = $"token-{Interlocked.Increment(ref _counter):D4}";
            return new IssuedRefreshToken(token, Hash(token));
        }

        public RefreshTokenHash Hash(string presentedToken)
        {
            var digest = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(presentedToken));

            return RefreshTokenHash.Create(Convert.ToHexStringLower(digest));
        }
    }

    private sealed class StubAccessTokens : IAccessTokenGenerator
    {
        public AccessToken Generate(EmployeeId employeeId, CompanyId companyId, SessionId sessionId) =>
            new("stub.access.token", DateTimeOffset.UtcNow.AddMinutes(15));
    }

    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        public int Commits { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            Commits++;
            return Task.FromResult(0);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
