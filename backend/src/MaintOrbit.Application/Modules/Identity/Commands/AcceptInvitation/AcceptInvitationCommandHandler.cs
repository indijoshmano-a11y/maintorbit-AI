using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Persistence;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.Repositories;

namespace MaintOrbit.Application.Modules.Identity.Commands.AcceptInvitation;

/// <summary>
/// Establishes a credential for an invited Employee and activates the account.
/// </summary>
/// <remarks>
/// One command, one transaction, one commit (backend-architecture-overview §3.6). Two aggregates
/// change together — the Employee becomes active and a credential appears — and they must become
/// visible together: an active Employee with no credential cannot authenticate, and a credential
/// against an inactive Employee is an account that was never opened. Both live in the identity
/// schema, so a single local transaction covers them without reaching across a module boundary.
/// </remarks>
public sealed class AcceptInvitationCommandHandler(
    IEmployeeRepository employees,
    IEmployeeCredentialRepository credentials,
    IPasswordHasher passwordHasher,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
    : ICommandHandler<AcceptInvitationCommand>
{
    public async Task<Result> HandleAsync(
        AcceptInvitationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrEmpty(command.Password))
        {
            // Checked before any work. The strength policy (FR-AUTH-002) belongs in a validation
            // behaviour ahead of this handler; what is enforced here is the floor below which the
            // hasher itself refuses to operate.
            return Result.Failure(Error.Validation("A password is required."));
        }

        var employee = await employees.FindAsync(command.EmployeeId, cancellationToken)
            .ConfigureAwait(false);

        if (employee is null)
        {
            // Absent, or belonging to another Company — row-level security makes those the same
            // observation, and §6.2 makes them the same answer.
            return Result.Failure(Error.NotFound("No such invitation."));
        }

        if (employee.Status != EmployeeStatus.Invited)
        {
            // Checked before hashing. Argon2id at production parameters costs real memory and
            // CPU, so doing it for a request that cannot succeed hands an attacker a way to spend
            // the server's resources by replaying a completed invitation (T-5).
            return Result.Failure(Error.Conflict(
                "This invitation has already been completed."));
        }

        if (await credentials.ExistsForAsync(employee.Id, cancellationToken).ConfigureAwait(false))
        {
            // The database enforces this too — ux_employee_credentials_employee_id. Checking here
            // turns a unique-violation exception into an ordinary conflict result, and the
            // constraint remains as the guarantee under concurrency.
            return Result.Failure(Error.Conflict(
                "This Employee already has a credential."));
        }

        var now = timeProvider.GetUtcNow();

        // Activated before the credential is built, so that a rule violation costs nothing. The
        // aggregate owns the rule; this handler only propagates its answer.
        var activation = employee.Activate(now);

        if (activation.IsFailure)
        {
            return activation;
        }

        var credential = EmployeeCredential.Establish(
            employee.CompanyId,
            employee.Id,
            passwordHasher.Hash(command.Password),
            PasswordAlgorithm.Argon2id,
            passwordHasher.CurrentVersion.Value,
            passwordHasher.CurrentParameters,
            establishedAtUtc: now);

        credentials.Add(credential);

        // The single commit. Both aggregates are already tracked, so this is the only point at
        // which anything reaches the database.
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
