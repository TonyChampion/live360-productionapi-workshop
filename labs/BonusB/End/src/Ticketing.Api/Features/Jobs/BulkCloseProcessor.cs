using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Features.Events;
using Ticketing.Api.Features.Tickets;

namespace Ticketing.Api.Features.Jobs;

/// <summary>
/// Does the work for one attempt of a bulk close job. Scoped: it lives for one job, like a request,
/// because ITicketEvents and its handlers are scoped.
/// </summary>
public class BulkCloseProcessor(ITicketStore store, ITicketEvents events, JobStore jobs, TicketLog log)
{
    public async Task RunAsync(TicketJob job, CancellationToken cancellationToken)
    {
        foreach (var id in job.TicketIds)
        {
            // A retry resumes where the last attempt stopped, so no ticket is closed (or announced) twice
            if (jobs.HasResult(job.Id, id))
                continue;

            var ticket = await store.GetAsync(id, cancellationToken);
            string outcome;

            if (ticket is null)
                outcome = "not-found";
            else if (!ticket.CanMoveTo(TicketStatus.Closed))
                outcome = "already-closed";
            else
            {
                ticket.Status = TicketStatus.Closed;
                await store.UpdateAsync(ticket, cancellationToken);
                log.Closed(ticket.Id, job.RequestedBy);
                await events.PublishAsync(new TicketClosed(ticket.Id, job.RequestedBy), cancellationToken);
                outcome = "closed";
            }

            jobs.Update(job.Id, s => s.Results.Add(new JobItemResult(id, outcome)));
        }
    }
}
