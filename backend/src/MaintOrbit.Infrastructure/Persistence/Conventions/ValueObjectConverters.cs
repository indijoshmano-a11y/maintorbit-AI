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
