using MaintOrbit.Application.Abstractions.Messaging;

namespace MaintOrbit.Application.Modules.Identity.Commands.CompletePasswordReset;

/// <summary>
/// Redeems a reset token and sets a new password.
/// </summary>
/// <remarks>
/// The token identifies the Employee. Nothing here names one, and nothing here accepts the old
/// password — a reset exists precisely because the Employee cannot supply it, and a field for an
/// Employee identifier would let a caller aim a token they hold at an account they do not.
/// </remarks>
/// <param name="Token">The token from the emailed link.</param>
/// <param name="NewPassword">The password to set.</param>
public sealed record CompletePasswordResetCommand(string? Token, string? NewPassword) : ICommand;
