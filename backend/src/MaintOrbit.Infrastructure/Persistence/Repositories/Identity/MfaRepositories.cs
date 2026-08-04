using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace MaintOrbit.Infrastructure.Persistence.Repositories.Identity;

/// <summary>EF Core implementation of <see cref="IMfaEnrollmentRepository"/>.</summary>
internal sealed class MfaEnrollmentRepository(MaintOrbitDbContext context) : IMfaEnrollmentRepository
{
    /// <inheritdoc />
    public Task<MfaEnrollment?> FindCurrentForAsync(
        EmployeeId employeeId, CancellationToken cancellationToken) =>
        // Tracked: every caller changes what it finds — confirming it, spending a time step, or
        // disabling it — and an untracked aggregate would record all of that in memory and write
        // none of it, which would silently defeat replay protection.
        context.MfaEnrollments
            .Where(enrollment =>
                enrollment.EmployeeId == employeeId &&
                enrollment.Status != MfaEnrollmentStatus.Disabled)
            .OrderByDescending(enrollment => enrollment.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public void Add(MfaEnrollment enrollment) => context.MfaEnrollments.Add(enrollment);

    /// <inheritdoc />
    public void Remove(MfaEnrollment enrollment) => context.MfaEnrollments.Remove(enrollment);
}

/// <summary>EF Core implementation of <see cref="IMfaRecoveryCodeRepository"/>.</summary>
internal sealed class MfaRecoveryCodeRepository(MaintOrbitDbContext context)
    : IMfaRecoveryCodeRepository
{
    /// <inheritdoc />
    public Task<MfaRecoveryCode?> FindByHashAsync(
        MfaEnrollmentId enrollmentId, RecoveryCodeHash hash, CancellationToken cancellationToken) =>
        // Tracked, for the same reason: the caller spends the code it finds. Scoped to the
        // enrolment so a set issued for a factor that was disabled and replaced cannot satisfy the
        // new one.
        context.MfaRecoveryCodes.FirstOrDefaultAsync(
            code => code.EnrollmentId == enrollmentId && code.CodeHash == hash,
            cancellationToken);

    /// <inheritdoc />
    public Task<int> CountUnusedAsync(
        MfaEnrollmentId enrollmentId, CancellationToken cancellationToken) =>
        context.MfaRecoveryCodes.CountAsync(
            code => code.EnrollmentId == enrollmentId && code.UsedAtUtc == null,
            cancellationToken);

    /// <inheritdoc />
    public void Add(MfaRecoveryCode code) => context.MfaRecoveryCodes.Add(code);

    /// <inheritdoc />
    public Task<int> DeleteForEnrollmentAsync(
        MfaEnrollmentId enrollmentId, CancellationToken cancellationToken) =>
        // Set-based. A retained hash of a code that can no longer be redeemed protects nothing and
        // is one more copy of second-factor material to defend. Row-level security still applies,
        // so this reaches only the Company in scope.
        context.MfaRecoveryCodes
            .Where(code => code.EnrollmentId == enrollmentId)
            .ExecuteDeleteAsync(cancellationToken);
}
