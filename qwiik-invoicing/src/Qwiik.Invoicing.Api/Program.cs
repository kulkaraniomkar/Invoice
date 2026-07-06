using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Qwiik.Invoicing.Api.Features.Invoices;
using Qwiik.Invoicing.Api.Infrastructure;
using Qwiik.Invoicing.Api.Infrastructure.Tenancy;
using Qwiik.Invoicing.Api.Middleware;
using Qwiik.Invoicing.Api.Swagger;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Structured logging. Console sink locally; in Azure the same pipeline ships to
// Application Insights / Log Analytics without code changes.
builder.Host.UseSerilog((context, loggerConfig) => loggerConfig
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext());

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Qwiik Invoicing API",
        Version = "v1",
        Description = "Multi-tenant invoice management module. All /api endpoints require an X-Tenant-Id header (GUID)."
    });
    options.MapType<DateOnly>(() => new OpenApiSchema { Type = "string", Format = "date" });
    options.OperationFilter<TenantHeaderOperationFilter>();
});

builder.Services.AddDbContext<InvoicingDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

// One TenantProvider instance per request: the middleware writes it, the DbContext reads it.
builder.Services.AddScoped<TenantProvider>();
builder.Services.AddScoped<ITenantProvider>(sp => sp.GetRequiredService<TenantProvider>());

builder.Services.AddScoped<IInvoiceService, InvoiceService>();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    await DatabaseInitializer.InitializeAsync(app);
}

app.UseMiddleware<TenantResolutionMiddleware>();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

// Exposed so WebApplicationFactory-based integration tests can bootstrap the app.
public partial class Program;
