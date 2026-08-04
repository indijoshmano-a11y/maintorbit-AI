using MaintOrbit.Application.Abstractions.Persistence;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Application.Modules.Identity.Commands.AcceptInvitation;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Api.FunctionalTests.Identity;

/// <summary>
/// Covers the invitation acceptance use case.
/// </summary>
/// <remarks>
/// Driven through in-memory fakes rather than a database. What is being asserted is the handler's
/// decisions — the order it checks things in, what it refuses, and that it commits exactly once —
/// none of which a database makes more true. The repositories' own behaviour against PostgreSQL is
/// covered separately.
/// </remarks>
public sealed class AcceptInvitationTests
{
    private static readonly CompanyId Company = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    private const string Password = "correct horse battery staple";

    private static InvitationToken Token =>
        InvitationToken.Create("hVJ8kQ2mNpR4tS7wZ1xC3vB5nM6aD9fG");

    private sealed class Fixture
    {
        public FakeEmployeeRepository Employees { get; } = new();
        public FakeCredentialRepository Credentials { get; } = new();
        public RecordingUnitOfWork UnitOfWork { get; } = new();
        public CountingPasswordHasher Hasher { get; } = new();

        public AcceptInvitationCommandHandler Handler() =>
            new(Employees, Credentials, Hasher, new FixedAuthenticationPolicy(), UnitOfWork,
                new FakeTimeProvider(Now));

        public Employee GivenInvitedEmployee()
        {
            var employee = Employee.Invite(Company, Email.Create("ada@example.test"), Now);
            Employees.Add(employee);
            return employee;
        }
    }

    // ---- The happy path -----------------------------------------------------------------------

    [Fact]
    public async Task Activation_Succeeds()
    {
        var fixture = new Fixture();
        var employee = fixture.GivenInvitedEmployee();

        var result = await fixture.Handler().HandleAsync(
            new AcceptInvitationCommand(employee.Id, Token, Password), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Activation_MakesTheEmployeeActive()
    {
        var fixture = new Fixture();
        var employee = fixture.GivenInvitedEmployee();

        await fixture.Handler().HandleAsync(
            new AcceptInvitationCommand(employee.Id, Token, Password), CancellationToken.None);

        Assert.Equal(EmployeeStatus.Active, employee.Status);
    }

    [Fact]
    public async Task Activation_VerifiesTheEmailAddress()
    {
        // FR-AUTH-013 requires verification before an account becomes active. Completing the
        // invitation is that proof — the token was delivered to the address and came back.
        var fixture = new Fixture();
        var employee = fixture.GivenInvitedEmployee();

        await fixture.Handler().HandleAsync(
            new AcceptInvitationCommand(employee.Id, Token, Password), CancellationToken.None);

        Assert.Equal(Now, employee.EmailVerifiedAtUtc);
    }

    [Fact]
    public async Task Activation_CreatesACredentialForTheSameCompanyAndEmployee()
    {
        var fixture = new Fixture();
        var employee = fixture.GivenInvitedEmployee();

        await fixture.Handler().HandleAsync(
            new AcceptInvitationCommand(employee.Id, Token, Password), CancellationToken.None);

        var credential = Assert.Single(fixture.Credentials.Added);
        Assert.Equal(employee.Id, credential.EmployeeId);
        Assert.Equal(employee.CompanyId, credential.CompanyId);
    }

    [Fact]
    public async Task Activation_HashesThePasswordExactlyOnce()
    {
        // Hashing is the expensive step. Doing it twice would double the cost of every
        // activation, and doing it zero times would mean a credential that is not a hash.
        var fixture = new Fixture();
        var employee = fixture.GivenInvitedEmployee();

        await fixture.Handler().HandleAsync(
            new AcceptInvitationCommand(employee.Id, Token, Password), CancellationToken.None);

        Assert.Equal(1, fixture.Hasher.HashCalls);
    }

    [Fact]
    public async Task Activation_StoresNoPlaintext()
    {
        var fixture = new Fixture();
        var employee = fixture.GivenInvitedEmployee();

        await fixture.Handler().HandleAsync(
            new AcceptInvitationCommand(employee.Id, Token, Password), CancellationToken.None);

        var credential = Assert.Single(fixture.Credentials.Added);
        Assert.DoesNotContain("correct", credential.PasswordHash.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Activation_RecordsTheHashersOwnVersionAndParameters()
    {
        // §4.2 stores hash_parameters per row so a parameter review does not invalidate existing
        // hashes. The handler must record what the hasher actually used, not a value of its own.
        var fixture = new Fixture();
        var employee = fixture.GivenInvitedEmployee();

        await fixture.Handler().HandleAsync(
            new AcceptInvitationCommand(employee.Id, Token, Password), CancellationToken.None);

        var credential = Assert.Single(fixture.Credentials.Added);
        Assert.Equal(fixture.Hasher.CurrentVersion.Value, credential.PasswordVersion);
        Assert.Equal(fixture.Hasher.CurrentParameters, credential.HashParameters);
    }

    [Fact]
    public async Task Activation_CommitsExactlyOnce()
    {
        // One command, one transaction, one commit (§3.6). Two commits would make the Employee's
        // activation durable separately from the credential — a window in which an active
        // Employee has no way to authenticate.
        var fixture = new Fixture();
        var employee = fixture.GivenInvitedEmployee();

        await fixture.Handler().HandleAsync(
            new AcceptInvitationCommand(employee.Id, Token, Password), CancellationToken.None);

        Assert.Equal(1, fixture.UnitOfWork.Commits);
    }

    // ---- Refusals -----------------------------------------------------------------------------

    [Fact]
    public async Task MissingEmployee_IsNotFound()
    {
        var fixture = new Fixture();

        var result = await fixture.Handler().HandleAsync(
            new AcceptInvitationCommand(EmployeeId.New(), Token, Password), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("not_found", result.Error.Code);
    }

    [Fact]
    public async Task AlreadyActiveEmployee_IsAConflict()
    {
        var fixture = new Fixture();
        var employee = fixture.GivenInvitedEmployee();
        employee.Activate(Now);

        var result = await fixture.Handler().HandleAsync(
            new AcceptInvitationCommand(employee.Id, Token, Password), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("conflict", result.Error.Code);
    }

    [Fact]
    public async Task ExistingCredential_IsAConflict()
    {
        var fixture = new Fixture();
        var employee = fixture.GivenInvitedEmployee();
        fixture.Credentials.MarkExisting(employee.Id);

        var result = await fixture.Handler().HandleAsync(
            new AcceptInvitationCommand(employee.Id, Token, Password), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("conflict", result.Error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task MissingPassword_IsAValidationFailure(string? password)
    {
        var fixture = new Fixture();
        var employee = fixture.GivenInvitedEmployee();

        var result = await fixture.Handler().HandleAsync(
            new AcceptInvitationCommand(employee.Id, Token, password!), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("validation_failed", result.Error.Code);
    }

    // ---- What a refusal must not do ------------------------------------------------------------

    [Fact]
    public async Task ARefusedRequest_CommitsNothing()
    {
        var fixture = new Fixture();
        var employee = fixture.GivenInvitedEmployee();
        employee.Activate(Now);

        await fixture.Handler().HandleAsync(
            new AcceptInvitationCommand(employee.Id, Token, Password), CancellationToken.None);

        Assert.Equal(0, fixture.UnitOfWork.Commits);
        Assert.Empty(fixture.Credentials.Added);
    }

    [Fact]
    public async Task ARequestForACompletedInvitation_DoesNotHash()
    {
        // Argon2id at production parameters costs real memory and CPU. Hashing before checking
        // would let an attacker spend the server's resources by replaying a completed invitation
        // (T-5 — the cost is a denial-of-service consideration).
        var fixture = new Fixture();
        var employee = fixture.GivenInvitedEmployee();
        employee.Activate(Now);

        await fixture.Handler().HandleAsync(
            new AcceptInvitationCommand(employee.Id, Token, Password), CancellationToken.None);

        Assert.Equal(0, fixture.Hasher.HashCalls);
    }

    [Fact]
    public async Task ARequestForAMissingEmployee_DoesNotHash()
    {
        var fixture = new Fixture();

        await fixture.Handler().HandleAsync(
            new AcceptInvitationCommand(EmployeeId.New(), Token, Password), CancellationToken.None);

        Assert.Equal(0, fixture.Hasher.HashCalls);
    }

    // ---- Secrets --------------------------------------------------------------------------------

    [Fact]
    public void TheCommand_PrintsNeitherThePasswordNorTheToken()
    {
        // A command is exactly what gets logged when something goes wrong, and a record prints
        // every property by default.
        var command = new AcceptInvitationCommand(EmployeeId.New(), Token, Password);

        var printed = $"{command}";

        Assert.DoesNotContain("correct", printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hVJ8kQ", printed, StringComparison.Ordinal);
        Assert.Contains("REDACTED", printed, StringComparison.Ordinal);
    }

    // ---- Fakes ----------------------------------------------------------------------------------

    private sealed class FakeEmployeeRepository : IEmployeeRepository
    {
        private readonly Dictionary<EmployeeId, Employee> _employees = [];

        public Task<Employee?> FindAsync(EmployeeId id, CancellationToken cancellationToken) =>
            Task.FromResult(_employees.GetValueOrDefault(id));

        public Task<Employee?> FindByEmailAsync(Email email, CancellationToken cancellationToken) =>
            Task.FromResult(_employees.Values.FirstOrDefault(e => e.Email == email));

        public void Add(Employee employee) => _employees[employee.Id] = employee;

        // Unused by these tests: the directory endpoints have their own, against a real database.
        // A fake that paged an in-memory list would assert nothing about row-level security, which
        // is the only interesting thing about listing Employees.
        public Task<IReadOnlyList<Employee>> ListAsync(
            int skip, int take, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> CountAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCredentialRepository : IEmployeeCredentialRepository
    {
        private readonly HashSet<EmployeeId> _existing = [];

        public List<EmployeeCredential> Added { get; } = [];

        public void MarkExisting(EmployeeId employeeId) => _existing.Add(employeeId);

        public Task<bool> ExistsForAsync(EmployeeId employeeId, CancellationToken cancellationToken) =>
            Task.FromResult(_existing.Contains(employeeId));

        public Task<EmployeeCredential?> FindForAsync(
            EmployeeId employeeId, CancellationToken cancellationToken) =>
            Task.FromResult(Added.FirstOrDefault(c => c.EmployeeId == employeeId));

        public void Add(EmployeeCredential credential) => Added.Add(credential);
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

    /// <summary>A hasher that counts calls without paying Argon2id's cost.</summary>
    private sealed class CountingPasswordHasher : IPasswordHasher
    {
        public int HashCalls { get; private set; }

        public PasswordHashVersion CurrentVersion => new(7);

        public string CurrentParameters => "m=19456,t=2,p=1";

        public PasswordHash Hash(ReadOnlySpan<char> password)
        {
            HashCalls++;
            return PasswordHash.Create("$argon2id$v=19$m=19456,t=2,p=1$c2FsdHNhbHRzYWx0$aGFzaGhhc2g");
        }

        public PasswordVerificationResult Verify(PasswordHash hash, ReadOnlySpan<char> password) =>
            throw new NotSupportedException("Verification is not part of this use case.");

        public bool NeedsRehash(PasswordHash hash) => false;
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
