using Ticketing.Api.Chaos;
using Ticketing.Api.Features;
using Ticketing.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApiDefaults();
builder.Services.AddErrorHandling();
builder.Services.AddTicketing();
builder.Services.AddTicketingResilience();
builder.Services.AddTicketingAuth();
builder.Services.AddTicketingRateLimits(builder.Configuration);
builder.Services.AddSupportSiteCors(builder.Configuration);
builder.Services.AddTicketingHealth(builder.Configuration);
builder.Services.AddTicketingTelemetry(builder.Configuration);

// Don't advertise the server software in every response
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

var app = builder.Build();

app.UseSecurityHeaders();
app.UseErrorHandling();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors(CorsPolicies.SupportSite);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseRequestTimeouts();

app.MapOpenApi().WithDocumentPerVersion();
if (app.Environment.IsDevelopment())
{
    // ASP.NET Core generates the document but ships no UI. Swagger UI renders it at /swagger.
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "v1"));
}
app.MapTicketingEndpoints();
app.MapTicketingHealth();
app.MapChaosEndpoints();

app.Run();

public partial class Program;
