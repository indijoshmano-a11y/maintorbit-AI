using MaintOrbit.Application.Abstractions.Messaging;

namespace MaintOrbit.Application.Modules.Identity.Commands.SignOut;

/// <summary>Ends the session the caller is currently authenticated with.</summary>
/// <remarks>
/// Carries nothing. The session is taken from the validated token, never from the request body —
/// a session identifier a caller could supply would be a caller able to end somebody else's
/// session.
/// </remarks>
public sealed record SignOutCommand : ICommand;

/// <summary>Ends every session belonging to the authenticated Employee.</summary>
/// <remarks>
/// The "sign out everywhere" of §3.5. Also carries nothing: the Employee comes from the token.
/// </remarks>
public sealed record SignOutEverywhereCommand : ICommand;
