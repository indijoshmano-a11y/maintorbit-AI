using MaintOrbit.Domain.Common.Results;

namespace MaintOrbit.Application.Abstractions.Messaging;

/// <summary>
/// A request that changes state.
/// </summary>
/// <remarks>
/// A marker, because the pipeline branches on it: ADR-0012 places Transaction at position 5 and
/// notes "commands only; queries never open a write transaction". The dispatcher tells the two
/// apart by type, which only works if the type says which it is.
/// <para>
/// The dispatcher and its nine ordered behaviours are not built here — this milestone implements
/// one use case, and Authorization, Outbox dispatch, and Audit have nothing to act on yet. The
/// contracts exist now so the handler written today is the one the dispatcher invokes later,
/// rather than something reshaped to fit it.
/// </para>
/// </remarks>
public interface ICommand;

/// <summary>A request that changes state and produces a value.</summary>
public interface ICommand<TResponse> : ICommand;

/// <summary>Handles a command that produces no value.</summary>
public interface ICommandHandler<in TCommand>
    where TCommand : ICommand
{
    Task<Result> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

/// <summary>Handles a command that produces a value.</summary>
public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<Result<TResponse>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
