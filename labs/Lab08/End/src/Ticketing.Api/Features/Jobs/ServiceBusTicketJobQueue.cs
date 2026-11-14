using System.Runtime.CompilerServices;
using Azure.Messaging.ServiceBus;

namespace Ticketing.Api.Features.Jobs;

/// <summary>
/// The same interface on Azure Service Bus. Provided for comparison; it compiles, but it isn't
/// registered, so nothing in the lab needs Azure. Swapping it in is one registration change:
///
///   services.AddSingleton(new ServiceBusClient("&lt;namespace&gt;.servicebus.windows.net", new DefaultAzureCredential()));
///   services.AddSingleton&lt;ITicketJobQueue&gt;(sp =>
///       new ServiceBusTicketJobQueue(sp.GetRequiredService&lt;ServiceBusClient&gt;(), "ticket-jobs"));
/// </summary>
public sealed class ServiceBusTicketJobQueue(ServiceBusClient client, string queueName) : ITicketJobQueue, IAsyncDisposable
{
    private readonly ServiceBusSender _sender = client.CreateSender(queueName);
    private readonly ServiceBusReceiver _receiver = client.CreateReceiver(queueName);

    public async ValueTask EnqueueAsync(TicketJob job, CancellationToken cancellationToken = default)
    {
        var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(job))
        {
            // With duplicate detection on, a resend of the same attempt is dropped by the broker
            MessageId = $"{job.Id}:{job.Attempt}",
            ContentType = "application/json",
            Subject = "bulk-close"
        };

        // The SDK propagates the current trace context on the message automatically
        await _sender.SendMessageAsync(message, cancellationToken);
    }

    public async IAsyncEnumerable<TicketJob> DequeueAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await _receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (message is null)
                continue;   // nothing arrived within the wait; ask again

            var job = message.Body.ToObjectFromJson<TicketJob>()!;

            // Completing on receive keeps this interface simple, and it's AT-MOST-ONCE: if the
            // process dies mid-job, the job is gone. Production code completes after the work
            // succeeds and abandons on failure, so the broker redelivers it, and after
            // MaxDeliveryCount moves it to the queue's built-in dead-letter queue. That needs an
            // acknowledgment in the interface. See "Talk it over" in the Lab 8 manual.
            await _receiver.CompleteMessageAsync(message, cancellationToken);
            yield return job;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        await _receiver.DisposeAsync();
    }
}
