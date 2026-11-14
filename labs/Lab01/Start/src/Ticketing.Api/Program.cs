using System.Text.Json.Serialization;
using Ticketing.Api.Chaos;
using Ticketing.Api.Data;
using Ticketing.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Controllers, the way most existing APIs are built today
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Data: the real store, wrapped by the fault-injection decorator
builder.Services.AddSingleton<ChaosSettings>();
builder.Services.AddSingleton<InMemoryTicketStore>();
builder.Services.AddSingleton<ITicketStore>(sp =>
    new FaultInjectingTicketStore(
        sp.GetRequiredService<InMemoryTicketStore>(),
        sp.GetRequiredService<ChaosSettings>()));

builder.Services.AddScoped<ITicketService, TicketService>();

builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapChaosEndpoints();

app.Run();

public partial class Program;
