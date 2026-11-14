using Ticketing.Api.Features.Escalations;

namespace Ticketing.Api.Features.Jobs;

public static class JobsExtensions
{
    public static IServiceCollection AddTicketJobs(this IServiceCollection services)
    {
        services.AddOptions<JobOptions>().BindConfiguration("Jobs");
        services.AddSingleton<ITicketJobQueue, ChannelTicketJobQueue>();
        services.AddSingleton<JobStore>();
        services.AddScoped<BulkCloseProcessor>();
        services.AddHostedService<TicketJobWorker>();

        services.AddOptions<SlaOptions>().BindConfiguration("Sla");
        services.AddSingleton<EscalationLog>();
        services.AddHostedService<SlaMonitor>();

        return services;
    }
}
