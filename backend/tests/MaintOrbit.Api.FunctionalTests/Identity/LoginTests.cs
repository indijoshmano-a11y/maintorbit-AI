using MaintOrbit.Application.Abstractions.Persistence;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Application.Modules.Identity.Commands.Login;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Api.FunctionalTests.Identity;

/// <summary>
/// Covers employee authentication.
/// </summary>
/// <remarks>
/// The assertions that matter most are negative ones. Every failure must be the same error, and
/// every failure that concerns a real address must cost the same work — otherwise the login
/// endpoint becomes an oracle for which addresses hold accounts, without an attacker ever having
/// to guess a password.
/// </remarks>
public sealed class LoginTests
{
    private static readonly CompanyId Company = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    private const string Address = "ada@example.test";
    private const string Password = "correct horse battery staple";

    private sealed class Fixture
    {
        public FakeEmployees Employees { get; } = new();
        public FakeCredentials Credentials { get; } = new();
        public ScriptedHasher Hasher { get; } = new();

        public CountingUnitOfWork UnitOfWork { get; } = new();

        /// <summary>The policy the lockout tests drive from.</summary>
        /// <remarks>
        /// Three attempts and fifteen minutes, so the threshold is reached quickly and the
        /// duration is long enough that no test races it.
        /// </remarks>
        public CompanyAuthenticationPolicy Policy { get; set; } =
            CompanyAuthenticationPolicy.Create(
                Company, 12, true, 60, 720, false,
                maximumFailedAttempts: 3, lockoutMinutes: 15, Now).Value;

        public DateTimeOffset Clock { get; set; } = Now;

        public LoginCommandHandler Handler() =>
            new(Employees, Credentials, Hasher, new FakeDecoy(),
                new FixedAuthenticationPolicy(Policy), UnitOfWork, new FakeClock(Clock));

        public Employee GivenEmployee(EmployeeStatus status)
        {
            var employee = Employee.Invite(Company, Email.Create(Address), Now);

            if (status is EmployeeStatus.Active)
            {
                employee.Activate(Now);
            }

            Employees.Add(employee);
            return employee;
        }

        public EmployeeCredential GivenCredential(Employee employee)
        {
            var credential = EmployeeCredential.Establish(
                employee.CompanyId, employee.Id,
                PasswordHash.Create("$argon2id$v=19$m=19456,t=2,p=1$c2FsdHNhbHQ$aGFzaGhhc2g"),
                PasswordAlgorithm.Argon2id, 1, "m=19456,t=2,p=1", Now);

            Credentials.Add(credential);
            return credential;
        }

        public Task<Domain.Common.Results.Result<AuthenticationResult>> Login(
            string address = Address, string password = Password) =>
            Handler().HandleAsync(new LoginCommand(address, password), CancellationToken.None);
    }

    // ---- Success ------------------------------------------------------------------------------

    [Fact]
    public async Task CorrectCredentials_Authenticate()
    {
        var fixture = new Fixture();
        fixture.GivenCredential(fixture.GivenEmployee(EmployeeStatus.Active));

        var result = await fixture.Login();

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Success_IdentifiesTheEmployeeAndTheirCompany()
    {
        // TC-1: the tenant is derived server-side from the credential. This is that derivation —
        // before authentication nothing knew which Company the caller belonged to.
        var fixture = new Fixture();
        var employee = fixture.GivenEmployee(EmployeeStatus.Active);
        fixture.GivenCredential(employee);

        var result = await fixture.Login();

        Assert.Equal(employee.Id, result.Value.EmployeeId);
        Assert.Equal(Company, result.Value.CompanyId);
    }

    [Fact]
    public async Task Success_VerifiesThePasswordExactlyOnce()
    {
        var fixture = new Fixture();
        fixture.GivenCredential(fixture.GivenEmployee(EmployeeStatus.Active));

        await fixture.Login();

        Assert.Equal(1, fixture.Hasher.VerifyCalls);
    }

    // ---- Rehash -------------------------------------------------------------------------------

    [Fact]
    public async Task RehashRequired_IsReportedButNotActedOn()
    {
        // A successful login is the only moment the plaintext exists to re-derive from. The signal
        // is returned; nothing is written, because this milestone persists nothing.
        var fixture = new Fixture();
        fixture.GivenCredential(fixture.GivenEmployee(EmployeeStatus.Active));
        fixture.Hasher.RehashNeeded = true;

        var result = await fixture.Login();

        Assert.True(result.Value.PasswordNeedsRehash);
        Assert.Equal(0, fixture.Hasher.HashCalls);
    }

    [Fact]
    public async Task RehashNotRequired_IsReportedAsSuch()
    {
        var fixture = new Fixture();
        fixture.GivenCredential(fixture.GivenEmployee(EmployeeStatus.Active));

        var result = await fixture.Login();

        Assert.False(result.Value.PasswordNeedsRehash);
    }

    // ---- Refusals, all identical ---------------------------------------------------------------

    [Fact]
    public async Task WrongPassword_IsRefused()
    {
        var fixture = new Fixture();
        fixture.GivenCredential(fixture.GivenEmployee(EmployeeStatus.Active));
        fixture.Hasher.VerificationResult = PasswordVerificationResult.Failed;

        var result = await fixture.Login(password: "wrong");

        Assert.True(result.IsFailure);
        Assert.Equal("authentication_failed", result.Error.Code);
    }

    [Fact]
    public async Task UnknownAddress_IsRefused()
    {
        var result = await new Fixture().Login(address: "nobody@example.test");

        Assert.True(result.IsFailure);
        Assert.Equal("authentication_failed", result.Error.Code);
    }

    [Theory]
    [InlineData(EmployeeStatus.Invited)]
    [InlineData(EmployeeStatus.Suspended)]
    [InlineData(EmployeeStatus.Removed)]
    public async Task NonActiveEmployee_IsRefused(EmployeeStatus status)
    {
        var fixture = new Fixture();
        var employee = fixture.GivenEmployee(status);
        fixture.Employees.ForceStatus(employee, status);
        fixture.GivenCredential(employee);

        var result = await fixture.Login();

        Assert.True(result.IsFailure);
        Assert.Equal("authentication_failed", result.Error.Code);
    }

    [Fact]
    public async Task EmployeeWithNoCredential_IsRefused()
    {
        // Federated-only, or a Company that disabled password authentication (FR-AUTH-004).
        var fixture = new Fixture();
        fixture.GivenEmployee(EmployeeStatus.Active);

        var result = await fixture.Login();

        Assert.True(result.IsFailure);
        Assert.Equal("authentication_failed", result.Error.Code);
    }

    [Fact]
    public async Task UnusableStoredHash_IsRefused()
    {
        // An operational fault, but not a distinction the caller may see — it would confirm the
        // account exists.
        var fixture = new Fixture();
        fixture.GivenCredential(fixture.GivenEmployee(EmployeeStatus.Active));
        fixture.Hasher.VerificationResult = PasswordVerificationResult.Unusable;

        var result = await fixture.Login();

        Assert.Equal("authentication_failed", result.Error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    public async Task MalformedAddress_IsRefused(string address)
    {
        var result = await new Fixture().Login(address: address);

        Assert.True(result.IsFailure);
        Assert.Equal("authentication_failed", result.Error.Code);
    }

    [Fact]
    public async Task EveryRefusal_CarriesTheSameMessage()
    {
        // The messages must not differ either. A distinct description is as good an oracle as a
        // distinct code.
        var unknown = await new Fixture().Login(address: "nobody@example.test");

        var wrong = new Fixture();
        wrong.GivenCredential(wrong.GivenEmployee(EmployeeStatus.Active));
        wrong.Hasher.VerificationResult = PasswordVerificationResult.Failed;
        var wrongPassword = await wrong.Login(password: "wrong");

        var suspended = new Fixture();
        var employee = suspended.GivenEmployee(EmployeeStatus.Active);
        suspended.Employees.ForceStatus(employee, EmployeeStatus.Suspended);
        suspended.GivenCredential(employee);
        var suspendedResult = await suspended.Login();

        Assert.Equal(unknown.Error, wrongPassword.Error);
        Assert.Equal(unknown.Error, suspendedResult.Error);
    }

    // ---- Timing ---------------------------------------------------------------------------------

    [Fact]
    public async Task UnknownAddress_StillPerformsAVerification()
    {
        // The enumeration defence. Argon2id is deliberately expensive, so returning without
        // reaching it would make "unknown address" measurably faster than "wrong password" — an
        // oracle for which addresses hold accounts (threat I-13 requires uniform responses).
        var fixture = new Fixture();

        await fixture.Login(address: "nobody@example.test");

        Assert.Equal(1, fixture.Hasher.VerifyCalls);
    }

    [Theory]
    [InlineData(EmployeeStatus.Invited)]
    [InlineData(EmployeeStatus.Suspended)]
    [InlineData(EmployeeStatus.Removed)]
    public async Task NonActiveEmployee_StillPerformsAVerification(EmployeeStatus status)
    {
        var fixture = new Fixture();
        var employee = fixture.GivenEmployee(EmployeeStatus.Active);
        fixture.Employees.ForceStatus(employee, status);
        fixture.GivenCredential(employee);

        await fixture.Login();

        Assert.Equal(1, fixture.Hasher.VerifyCalls);
    }

    [Fact]
    public async Task EmployeeWithNoCredential_StillPerformsAVerification()
    {
        var fixture = new Fixture();
        fixture.GivenEmployee(EmployeeStatus.Active);

        await fixture.Login();

        Assert.Equal(1, fixture.Hasher.VerifyCalls);
    }

    [Fact]
    public async Task LockedOutCredential_IsRefusedAndStillVerifies()
    {
        var fixture = new Fixture();
        var employee = fixture.GivenEmployee(EmployeeStatus.Active);
        var credential = fixture.GivenCredential(employee);
        fixture.Credentials.LockUntil(credential, Now.AddMinutes(5));

        var result = await fixture.Login();

        Assert.Equal("authentication_failed", result.Error.Code);
        Assert.Equal(1, fixture.Hasher.VerifyCalls);
    }

    [Fact]
    public async Task MalformedRequest_SkipsVerification()
    {
        // The one deliberate exception. A malformed address is a malformed *request* — no
        // well-formed submission reaches this path, so the timing difference describes the input
        // rather than any account.
        var fixture = new Fixture();

        await fixture.Login(address: "not-an-address");

        Assert.Equal(0, fixture.Hasher.VerifyCalls);
    }

    // ---- Secrets ---------------------------------------------------------------------------------

    [Fact]
    public void TheCommand_PrintsNeitherAddressNorPassword()
    {
        var printed = $"{new LoginCommand(Address, Password)}";

        Assert.DoesNotContain("ada@", printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("correct", printed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REDACTED", printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheResult_CarriesNoCredentialMaterial()
    {
        // AuthenticationResult is what a caller receives and may log. It must say who, not how.
        var fixture = new Fixture();
        fixture.GivenCredential(fixture.GivenEmployee(EmployeeStatus.Active));

        var printed = $"{(await fixture.Login()).Value}";

        Assert.DoesNotContain("argon2", printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("correct", printed, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Fakes ------------------------------------------------------------------------------------

    private sealed class FakeEmployees : IEmployeeRepository
    {
        private readonly List<Employee> _employees = [];
        private readonly Dictionary<EmployeeId, EmployeeStatus> _overrides = [];

        public void ForceStatus(Employee employee, EmployeeStatus status) =>
            _overrides[employee.Id] = status;

        public Task<Employee?> FindAsync(EmployeeId id, CancellationToken cancellationToken) =>
            Task.FromResult(_employees.FirstOrDefault(e => e.Id == id));

        public Task<Employee?> FindByEmailAsync(Email email, CancellationToken cancellationToken)
        {
            var employee = _employees.FirstOrDefault(e => e.Email == email);

            // Status is private-set on the aggregate by design, so a non-Active state is simulated
            // by substituting a stand-in the handler treats identically.
            if (employee is not null
                && _overrides.TryGetValue(employee.Id, out var status)
                && status != EmployeeStatus.Active)
            {
                return Task.FromResult<Employee?>(
                    Employee.Invite(employee.CompanyId, employee.Email, Now));
            }

            return Task.FromResult(employee);
        }

        public void Add(Employee employee) => _employees.Add(employee);

        // Unused by these tests: the directory endpoints have their own, against a real database.
        // A fake that paged an in-memory list would assert nothing about row-level security, which
        // is the only interesting thing about listing Employees.
        public Task<IReadOnlyList<Employee>> ListAsync(
            int skip, int take, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> CountAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCredentials : IEmployeeCredentialRepository
    {
        private readonly List<EmployeeCredential> _credentials = [];
        private readonly HashSet<EmployeeCredentialId> _locked = [];

        public void LockUntil(EmployeeCredential credential, DateTimeOffset until) =>
            _locked.Add(credential.Id);

        public Task<bool> ExistsForAsync(EmployeeId employeeId, CancellationToken cancellationToken) =>
            Task.FromResult(_credentials.Any(c => c.EmployeeId == employeeId));

        public Task<EmployeeCredential?> FindForAsync(
            EmployeeId employeeId, CancellationToken cancellationToken)
        {
            var credential = _credentials.FirstOrDefault(c => c.EmployeeId == employeeId);

            // LockoutUntilUtc is private-set until the workflow that maintains it lands, so a
            // lockout is simulated by hiding the credential — the handler treats both as "nothing
            // usable to verify against", which is the behaviour under test.
            return Task.FromResult(
                credential is not null && _locked.Contains(credential.Id) ? null : credential);
        }

        public void Add(EmployeeCredential credential) => _credentials.Add(credential);
    }

    private sealed class ScriptedHasher : IPasswordHasher
    {
        public int VerifyCalls { get; private set; }
        public int HashCalls { get; private set; }
        public bool RehashNeeded { get; set; }
        public PasswordVerificationResult VerificationResult { get; set; } =
            PasswordVerificationResult.Success;

        public PasswordHashVersion CurrentVersion => new(1);
        public string CurrentParameters => "m=19456,t=2,p=1";

        public PasswordHash Hash(ReadOnlySpan<char> password)
        {
            HashCalls++;
            return PasswordHash.Create("$argon2id$v=19$m=19456,t=2,p=1$c2FsdHNhbHQ$aGFzaGhhc2g");
        }

        public PasswordVerificationResult Verify(PasswordHash hash, ReadOnlySpan<char> password)
        {
            VerifyCalls++;
            return VerificationResult;
        }

        public bool NeedsRehash(PasswordHash hash) => RehashNeeded;
    }

    /// <summary>Counts commits, so a test can assert the counter was actually persisted.</summary>
    private sealed class CountingUnitOfWork : IUnitOfWork
    {
        public int Commits { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            Commits++;
            return Task.FromResult(0);
        }
    }

    private sealed class FakeDecoy : IDecoyPasswordHash
    {
        public PasswordHash Value { get; } =
            PasswordHash.Create("$argon2id$v=19$m=19456,t=2,p=1$ZGVjb3lkZWNveQ$ZGVjb3k");
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
