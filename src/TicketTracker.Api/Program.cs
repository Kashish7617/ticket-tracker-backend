using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TicketTracker.Api.Data;
using TicketTracker.Api.Infrastructure;
using TicketTracker.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ---- Services ------------------------------------------------------------------------

// Enums travel as "open" / "in_progress" / "closed"; integers are rejected.
// Applied to MVC (request/response bodies) and to the HTTP JSON options, which is what
// the OpenAPI generator reads, so the documented schema matches the real wire format.
static void ConfigureJson(System.Text.Json.JsonSerializerOptions json) =>
    json.Converters.Add(new JsonStringEnumConverter(EnumNames.Policy, allowIntegerValues: false));

builder.Services.AddControllers().AddJsonOptions(o => ConfigureJson(o.JsonSerializerOptions));
builder.Services.ConfigureHttpJsonOptions(o => ConfigureJson(o.SerializerOptions));

// RFC 9457 ProblemDetails for every error response (400, 404, 500, ...).
builder.Services.AddProblemDetails();

builder.Services.AddOpenApi();

// SQL Server is the real database. "Sqlite" is a zero-install fallback for running the app
// on a machine without SQL Server (demo / local dev only): data goes to a local file.
var useSqlite = string.Equals(
    builder.Configuration["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase);

if (useSqlite)
{
    var sqlitePath = builder.Configuration.GetConnectionString("Sqlite") ?? "Data Source=tickets-dev.db";
    builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(sqlitePath));
}
else
{
    var connectionString = builder.Configuration.GetConnectionString("TicketTracker")
        ?? throw new InvalidOperationException("Connection string 'TicketTracker' is not configured.");

    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ITicketService, TicketService>();

const string CorsPolicy = "frontend";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddHealthChecks();

var app = builder.Build();

// ---- Pipeline ------------------------------------------------------------------------

// Unhandled exceptions become a 500 ProblemDetails without leaking stack traces.
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi(); // /openapi/v1.json
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/openapi/v1.json", "Ticket Tracker API v1");
        o.RoutePrefix = "swagger";
    });
}

app.UseCors(CorsPolicy);

app.MapControllers();
app.MapHealthChecks("/health");

if (useSqlite)
{
    // The migrations are SQL Server-specific, so the SQLite fallback creates the schema directly.
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
}
else if (app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    // Convenient for local dev and docker-compose. In production, migrations should run
    // as a separate deployment step (e.g. an idempotent script or migration bundle).
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

await app.RunAsync();

// Exposed so the integration tests can use WebApplicationFactory<Program>.
public partial class Program;
