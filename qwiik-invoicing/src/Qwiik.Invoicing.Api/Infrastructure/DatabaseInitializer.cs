using Microsoft.EntityFrameworkCore;

namespace Qwiik.Invoicing.Api.Infrastructure;

/// <summary>
/// Development-only convenience so the API runs out of the box (dotnet run / docker compose).
/// Applies migrations if any are compiled in; otherwise falls back to EnsureCreated.
/// Production deployments must apply migrations as an explicit pipeline step instead
/// (see SOLUTION_NOTES.md → Azure deployment).
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Database:InitializeOnStartup"))
            return;

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InvoicingDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<InvoicingDbContext>>();

        if (db.Database.GetMigrations().Any())
        {
            logger.LogInformation("Applying EF Core migrations...");
            await db.Database.MigrateAsync();
        }
        else
        {
            logger.LogWarning("No migrations found; creating schema with EnsureCreated (development only).");
            await db.Database.EnsureCreatedAsync();
        }
    }
}
