using MaintOrbit.Api.Extensions;

// Composition root for the MaintOrbit AI API host.
//
// Milestone 10.2 establishes the configuration foundation. Middleware, endpoints,
// authentication, health checks, and module registration are added in later milestones —
// see docs/02-architecture/backend-architecture-overview.md.
//
// This file stays minimal by design: registration lives in extension methods, so the
// composition root reads as a sequence of intentions rather than a list of mechanics.

var builder = WebApplication.CreateBuilder(args);

// Per-developer overrides, git-ignored. Layered last so it wins over
// appsettings.{Environment}.json. Copy appsettings.Local.example.json to begin.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Reject an unrecognised ASPNETCORE_ENVIRONMENT before anything binds to it.
ConfigurationServiceCollectionExtensions.ValidateEnvironment(builder.Environment);

builder.Services.AddApplicationConfiguration(builder.Configuration);

var app = builder.Build();

app.Run();

/// <summary>
/// Exposes the compiler-generated <c>Program</c> class to the functional test project.
/// </summary>
/// <remarks>
/// Top-level statements produce an internal <c>Program</c>. Tests reference it to build the
/// host, so it is made public here rather than by weakening assembly-wide visibility.
/// </remarks>
public partial class Program;
