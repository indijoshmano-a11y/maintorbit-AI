using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MaintOrbit.Infrastructure.Persistence.Conventions;

/// <summary>
/// Converters mapping domain value objects onto single columns.
/// </summary>
/// <remarks>
/// Registered through <c>ConfigureConventions</c> rather than per property, and that placement is
/// what makes them work. EF discovers the model before entity configurations run, and a reference
/// type with public properties — <see cref="Email"/> — is discovered as an <i>entity</i>. A
/// converter applied later in <c>IEntityTypeConfiguration</c> maps the column correctly and
/// leaves the stray entity type behind, which then fails the schema convention for having no
/// schema. Registering by type means the discovery never happens.
/// <para>
/// The converters live in Infrastructure because they are persistence concerns. The Domain
/// project holds no package reference at all, and an architecture test enforces that — EF types
/// cannot appear there.
/// </para>
/// </remarks>
internal static class ValueObjectConverters
{
    /// <summary>Maps <see cref="EmployeeId"/> to <c>uuid</c>.</summary>
    internal sealed class EmployeeIdConverter()
        : ValueConverter<EmployeeId, Guid>(id => id.Value, value => new EmployeeId(value));

    /// <summary>Maps <see cref="EmployeeCredentialId"/> to <c>uuid</c>.</summary>
    internal sealed class EmployeeCredentialIdConverter()
        : ValueConverter<EmployeeCredentialId, Guid>(id => id.Value, value => new EmployeeCredentialId(value));

    /// <summary>
    /// Maps <see cref="PasswordHash"/> to text.
    /// </summary>
    /// <remarks>
    /// The one place C4 material is deliberately unwrapped. It exists so that nothing else has
    /// to reach for <see cref="PasswordHash.Value"/>, which is the property that turns a hash
    /// back into an ordinary string.
    /// </remarks>
    internal sealed class PasswordHashConverter()
        : ValueConverter<PasswordHash, string>(hash => hash.Value, value => PasswordHash.Create(value));

    /// <summary>Maps <see cref="SessionId"/> to <c>uuid</c>.</summary>
    internal sealed class SessionIdConverter()
        : ValueConverter<SessionId, Guid>(id => id.Value, value => new SessionId(value));

    /// <summary>Maps <see cref="Domain.Modules.Auditing.ValueObjects.AuditEventId"/> to <c>uuid</c>.</summary>
    internal sealed class AuditEventIdConverter()
        : ValueConverter<Domain.Modules.Auditing.ValueObjects.AuditEventId, Guid>(
            id => id.Value, value => new Domain.Modules.Auditing.ValueObjects.AuditEventId(value));

    /// <summary>Maps <see cref="RefreshTokenId"/> to <c>uuid</c>.</summary>
    internal sealed class RefreshTokenIdConverter()
        : ValueConverter<RefreshTokenId, Guid>(id => id.Value, value => new RefreshTokenId(value));

    /// <summary>Maps <see cref="RefreshTokenFamilyId"/> to <c>uuid</c>.</summary>
    internal sealed class RefreshTokenFamilyIdConverter()
        : ValueConverter<RefreshTokenFamilyId, Guid>(
            id => id.Value, value => new RefreshTokenFamilyId(value));

    /// <summary>Maps <see cref="RefreshTokenHash"/> to text. C4 material, unwrapped here only.</summary>
    internal sealed class RefreshTokenHashConverter()
        : ValueConverter<RefreshTokenHash, string>(
            hash => hash.Value, value => RefreshTokenHash.Create(value));

    /// <summary>Maps <see cref="PasswordResetTokenId"/> to <c>uuid</c>.</summary>
    internal sealed class PasswordResetTokenIdConverter()
        : ValueConverter<PasswordResetTokenId, Guid>(
            id => id.Value, value => new PasswordResetTokenId(value));

    /// <summary>
    /// Maps <see cref="PasswordResetTokenHash"/> to text. C4 material, unwrapped here only.
    /// </summary>
    internal sealed class PasswordResetTokenHashConverter()
        : ValueConverter<PasswordResetTokenHash, string>(
            hash => hash.Value, value => PasswordResetTokenHash.Create(value));

    /// <summary>Maps <see cref="EmailVerificationTokenId"/> to <c>uuid</c>.</summary>
    internal sealed class EmailVerificationTokenIdConverter()
        : ValueConverter<EmailVerificationTokenId, Guid>(
            id => id.Value, value => new EmailVerificationTokenId(value));

    /// <summary>
    /// Maps <see cref="EmailVerificationTokenHash"/> to text. C4 material, unwrapped here only.
    /// </summary>
    internal sealed class EmailVerificationTokenHashConverter()
        : ValueConverter<EmailVerificationTokenHash, string>(
            hash => hash.Value, value => EmailVerificationTokenHash.Create(value));

    /// <summary>Maps <see cref="MfaEnrollmentId"/> to <c>uuid</c>.</summary>
    internal sealed class MfaEnrollmentIdConverter()
        : ValueConverter<MfaEnrollmentId, Guid>(id => id.Value, value => new MfaEnrollmentId(value));

    /// <summary>Maps <see cref="MfaRecoveryCodeId"/> to <c>uuid</c>.</summary>
    internal sealed class MfaRecoveryCodeIdConverter()
        : ValueConverter<MfaRecoveryCodeId, Guid>(
            id => id.Value, value => new MfaRecoveryCodeId(value));

    /// <summary>Maps <see cref="RecoveryCodeHash"/> to text. C4 material, unwrapped here only.</summary>
    internal sealed class RecoveryCodeHashConverter()
        : ValueConverter<RecoveryCodeHash, string>(
            hash => hash.Value, value => RecoveryCodeHash.Create(value));

    /// <summary>Maps <see cref="PermissionCode"/> to text — the key itself (§1.6).</summary>
    internal sealed class PermissionCodeConverter()
        : ValueConverter<PermissionCode, string>(
            code => code.Value, value => PermissionCode.Create(value));

    /// <summary>Maps <see cref="RoleCode"/> to text.</summary>
    internal sealed class RoleCodeConverter()
        : ValueConverter<RoleCode, string>(code => code.Value, value => RoleCode.Create(value));

    /// <summary>Maps <see cref="CompanyId"/> to <c>uuid</c>.</summary>
    internal sealed class CompanyIdConverter()
        : ValueConverter<CompanyId, Guid>(id => id.Value, value => new CompanyId(value));

    /// <summary>
    /// Maps <see cref="Email"/> to text.
    /// </summary>
    /// <remarks>
    /// Reading goes through <see cref="Email.Create"/> rather than a private constructor, so a
    /// row that no longer satisfies the value object's rules fails loudly on load instead of
    /// producing an <see cref="Email"/> the domain would have rejected. Rows are written only
    /// through the same type, so this should be unreachable — which is the point of asserting it.
    /// </remarks>
    internal sealed class EmailConverter()
        : ValueConverter<Email, string>(email => email.Value, value => Email.Create(value));
}
