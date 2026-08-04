using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.Repositories;

namespace MaintOrbit.Application.Modules.Identity.Queries.Employees;

/// <summary>
/// The Company's Employee directory — <c>employee.read [C]</c> (§3.2).
/// </summary>
/// <remarks>
/// <b>There is no Company parameter, and there must not be.</b> TC-1 derives the tenant from the
/// credential; §5.1 states it plainly for filters — "Tenant: never a filter — always derived from
/// the credential". The rows this returns are the rows row-level security shows for the scope the
/// pipeline already opened.
/// </remarks>
/// <param name="Page">One-based page number.</param>
/// <param name="PageSize">Rows per page, bounded by the endpoint before it reaches here.</param>
public sealed record ListEmployeesQuery(int Page, int PageSize) : IQuery<EmployeePage>;

/// <summary>One Employee, as the directory shows them.</summary>
/// <remarks>
/// C2 fields only. Nothing here comes from <c>employee_credentials</c>, <c>sessions</c>, or
/// <c>mfa_enrollments</c> — those are separate aggregates precisely so that an ordinary directory
/// read cannot pull C4 material into memory as a side effect.
/// </remarks>
public sealed record EmployeeSummary(
    string Id,
    string Email,
    EmployeeStatus Status,
    DateTimeOffset? EmailVerifiedAtUtc,
    DateTimeOffset CreatedAtUtc);

/// <summary>A page of the directory, in the documented collection envelope (§4.4).</summary>
/// <remarks>
/// Carries a total because §4.4 says small bounded collections do: "counting them is cheap and the
/// UI benefits". The exclusions it names — ledger, audit, analytics — are the ones counted across
/// partitions at hundreds of millions of rows, which an Employee directory is not.
/// <para>
/// Offset rather than keyset, per §5.4: "offset is retained for small bounded collections … where
/// depth is inherently limited and a page number is more useful to a UI".
/// </para>
/// </remarks>
public sealed record EmployeePage(
    IReadOnlyList<EmployeeSummary> Items,
    int Page,
    int PageSize,
    int TotalCount,
    bool HasMore);

/// <summary>Reads a page of the Company's Employees.</summary>
/// <remarks>
/// It performs no authorization of its own. The permission was decided before the endpoint ran —
/// §3.7's model is that the operation declares what it needs and the pipeline enforces it — and a
/// second check here would be a second place to forget one.
/// </remarks>
public sealed class ListEmployeesQueryHandler(IEmployeeRepository employees)
    : IQueryHandler<ListEmployeesQuery, EmployeePage>
{
    public async Task<Result<EmployeePage>> HandleAsync(
        ListEmployeesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var skip = (query.Page - 1) * query.PageSize;

        var total = await employees.CountAsync(cancellationToken).ConfigureAwait(false);

        var page = await employees
            .ListAsync(skip, query.PageSize, cancellationToken)
            .ConfigureAwait(false);

        var items = page
            .Select(employee => new EmployeeSummary(
                employee.Id.ToString(),
                employee.Email.Value,
                employee.Status,
                employee.EmailVerifiedAtUtc,
                employee.CreatedAtUtc))
            .ToList();

        return Result.Success(new EmployeePage(
            items,
            query.Page,
            query.PageSize,
            total,
            HasMore: skip + items.Count < total));
    }
}
