// Composition root for the MaintOrbit AI API host.
//
// Milestone 10.1 establishes structure only. Middleware, endpoints, authentication,
// and module registration are added in later milestones — see
// docs/02-architecture/backend-architecture-overview.md and
// docs/07-api/api-specification.md.

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.Run();
