using MaintOrbit.Application.Abstractions.Messaging;

namespace MaintOrbit.Application.Modules.Identity.Commands.RequestPasswordReset;

/// <summary>
/// Asks for a password reset link to be sent to an address.
/// </summary>
/// <remarks>
/// <b>It carries no response type, and that is the security property.</b> A result that could
/// differ between a known and an unknown address would be an account-enumeration oracle reachable
/// without any credential at all — so there is nothing for the endpoint to translate and nothing
/// for a caller to compare.
/// </remarks>
/// <param name="Email">The address the caller typed. Unvalidated, and possibly unknown.</param>
/// <param name="IpAddress">
/// Server-observed source address, recorded on the request. Never taken from the request body — a
/// caller-supplied address would be a caller writing their own entry in somebody's audit trail.
/// </param>
public sealed record RequestPasswordResetCommand(string? Email, string? IpAddress) : ICommand;
