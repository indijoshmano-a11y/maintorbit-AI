using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Repositories;

namespace MaintOrbit.Application.Modules.Identity.Queries.Employees;

/// <summary>
/// The caller's own Employee record — <c>employee.read [S]</c> (§3.2).
/// </summary>
/// <remarks>
/// It carries no identifier. §3.2 gives <c>/me</c> as the Self-scoped read, and an Employee
/// identifier in the request would let a caller holding only <c>[S]</c> aim it at somebody else —
/// which is precisely the widening the Self scope exists to prevent.
/// </remarks>
public sealed record GetCurrentEmployeeQuery : IQuery<EmployeeSummary>;

/// <summary>Reads the authenticated Employee.</summary>
/// <remarks>
/// The Employee comes from <see cref="ICurrentIdentity"/>, which reads the validated token, and
/// the lookup runs inside the tenant scope the pipeline opened — so the row is found under
/// row-level security or not at all.
/// </remarks>
public sealed class GetCurrentEmployeeQueryHandler(
    ICurrentIdentity currentIdentity,
    IEmployeeRepository employees)
    : IQueryHandler<GetCurrentEmployeeQuery, EmployeeSummary>
{
    public async Task<Result<EmployeeSummary>> HandleAsync(
        GetCurrentEmployeeQuery query, CancellationToken cancellationToken)
    {
        var employeeId = currentIdentity.RequireEmployeeId();

        var employee = await employees.FindAsync(employeeId, cancellationToken)
            .ConfigureAwait(false);

        if (employee is null)
        {
            // A validated token for a row that is not visible. Deleted between issuance and now,
            // or invisible under the tenant scope — §6.2 makes those the same answer, and the
            // session check upstream has already dealt with the ordinary revocation case.
            return Result.Failure<EmployeeSummary>(Error.NotFound("No such Employee."));
        }

        return Result.Success(new EmployeeSummary(
            employee.Id.ToString(),
            employee.Email.Value,
            employee.Status,
            employee.EmailVerifiedAtUtc,
            employee.CreatedAtUtc));
    }
}
