namespace MaintOrbit.Domain.Modules.Identity.Enums;

/// <summary>
/// Which client surface a session was established from.
/// </summary>
/// <remarks>
/// <c>sessions.client_type</c> is a documented column, but its values are not enumerated anywhere.
/// These are the three client surfaces the architecture names — the web console, the VS Code
/// Extension, and customer server applications — plus one for anything not yet recognised.
/// <para>
/// Recorded so an Employee reviewing their sessions can tell one device from another
/// (FR-AUTH-008), and so a new-device notification can say what kind of client appeared.
/// It is <b>not</b> an authorization input: the client type is asserted by the client, and
/// nothing that decides access may be taken from something the caller controls.
/// </para>
/// </remarks>
public enum SessionClientType
{
    /// <summary>A client that did not identify itself as one of the known surfaces.</summary>
    Unknown = 0,

    /// <summary>The web console.</summary>
    WebConsole = 1,

    /// <summary>The VS Code Extension.</summary>
    VsCodeExtension = 2,

    /// <summary>A customer server application calling the API directly.</summary>
    ServerApplication = 3
}
