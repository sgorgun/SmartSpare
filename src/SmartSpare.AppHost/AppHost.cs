var builder = DistributedApplication.CreateBuilder(args);

// Same major version as Azure Database for PostgreSQL Flexible Server. The password is generated
// by Aspire and kept in the AppHost user secrets, never in the repository.
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("17")
    .WithDataVolume("smartspare-postgres-data");

var database = postgres.AddDatabase("smartspare");

builder.AddProject<Projects.SmartSpare_Host>("host")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health/ready");

await builder.Build().RunAsync().ConfigureAwait(false);
