using MaintOrbit.Domain.Common.Results;

namespace MaintOrbit.Application.Abstractions.Messaging;

/// <summary>
/// A request that reads without changing state.
/// </summary>
/// <remarks>
/// A separate marker from <see cref="ICommand"/> because the pipeline branches on it. ADR-0012
/// places Transaction at position 5 and states "commands only; queries never open a write
/// transaction"; backend-architecture-overview §3.6 adds that queries "do not participate in the
/// outbox". The dispatcher tells the two apart by type, which only works if the type says which
/// it is.
/// <para>
/// <b>Authorization is not weaker here.</b> A query passes through the same permission gate as a
/// command — reading a Company's Employee directory is exactly the kind of operation §3.7 governs.
/// What differs is the transaction and the outbox, not the check.
/// </para>
/// </remarks>
public interface IQuery<TResponse>;

/// <summary>Handles a query.</summary>
public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    Task<Result<TResponse>> HandleAsync(TQuery query, CancellationToken cancellationToken);
}
