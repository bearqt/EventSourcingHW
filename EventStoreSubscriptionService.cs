using EventStore.Client;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

public class EventStoreSubscriptionService : BackgroundService
{
    private readonly EventStoreClient _client;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EventStoreSubscriptionService> _logger;
    private const string SubscriptionId = "ProductSubscription";

    public EventStoreSubscriptionService(
        EventStoreClient client,
        IServiceProvider serviceProvider,
        ILogger<EventStoreSubscriptionService> logger)
    {
        _client = client;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryDelay = TimeSpan.FromSeconds(2);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var checkpoint = await GetCheckpointAsync(stoppingToken);
                var position = checkpoint.HasValue
                    ? FromAll.After(new Position((ulong)checkpoint.Value, (ulong)checkpoint.Value))
                    : FromAll.Start;

                _logger.LogInformation("Starting subscription from position: {Checkpoint}", checkpoint);

                var droppedTcs = new TaskCompletionSource<Exception?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                await _client.SubscribeToAllAsync(
                    position,
                    EventAppeared,
                    filterOptions: new SubscriptionFilterOptions(EventTypeFilter.ExcludeSystemEvents()),
                    subscriptionDropped: (_, reason, ex) =>
                    {
                        _logger.LogWarning(ex, "Subscription dropped: {Reason}", reason);
                        droppedTcs.TrySetResult(ex);
                    },
                    cancellationToken: stoppingToken
                );

                await Task.WhenAny(droppedTcs.Task, Task.Delay(System.Threading.Timeout.Infinite, stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Subscription failed. Retrying in {DelaySeconds}s", retryDelay.TotalSeconds);
            }

            await Task.Delay(retryDelay, stoppingToken);
        }
    }

    private async Task EventAppeared(StreamSubscription subscription, ResolvedEvent resolvedEvent, CancellationToken cancellationToken)
    {
        if (resolvedEvent.Event.EventType.StartsWith("$")) return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();

            await ProcessEvent(db, resolvedEvent);
            await SaveCheckpointAsync(db, resolvedEvent.OriginalEvent.Position.CommitPosition);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing event {EventType}", resolvedEvent.Event.EventType);
        }
    }

    private async Task ProcessEvent(WarehouseDbContext db, ResolvedEvent resolvedEvent)
    {
        var eventData = resolvedEvent.Event.Data.ToArray();
        
        switch (resolvedEvent.Event.EventType)
        {
            case nameof(OrderPlacedEvent):
                var orderPlaced = JsonSerializer.Deserialize<OrderPlacedEvent>(eventData);
                if (orderPlaced != null)
                {
                    var product = await db.Products.FindAsync(orderPlaced.ProductId);
                    if (product != null)
                    {
                        product.Apply(orderPlaced);
                        product.Version = (long)resolvedEvent.Event.EventNumber.ToUInt64();
                    }
                }
                break;

            case nameof(OrderCancelledEvent):
                var orderCancelled = JsonSerializer.Deserialize<OrderCancelledEvent>(eventData);
                if (orderCancelled != null)
                {
                    var product = await db.Products.FindAsync(orderCancelled.ProductId);
                    if (product != null)
                    {
                        product.Apply(orderCancelled);
                        product.Version = (long)resolvedEvent.Event.EventNumber.ToUInt64();
                    }
                }
                break;

            case nameof(ProductRestockedEvent):
                var productRestocked = JsonSerializer.Deserialize<ProductRestockedEvent>(eventData);
                if (productRestocked != null)
                {
                    var product = await db.Products.FindAsync(productRestocked.ProductId);
                    if (product != null)
                    {
                        product.Apply(productRestocked);
                        product.Version = (long)resolvedEvent.Event.EventNumber.ToUInt64();
                    }
                }
                break;
        }

        await db.SaveChangesAsync();
    }

    private async Task<ulong?> GetCheckpointAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var checkpoint = await db.SubscriptionCheckpoints.FindAsync(new object[] { SubscriptionId }, cancellationToken);
        return checkpoint != null ? (ulong)checkpoint.Position : null;
    }

    private async Task SaveCheckpointAsync(WarehouseDbContext db, ulong position)
    {
        var checkpoint = await db.SubscriptionCheckpoints.FindAsync(SubscriptionId);
        if (checkpoint == null)
        {
            checkpoint = new SubscriptionCheckpoint { SubscriptionId = SubscriptionId, Position = (long)position };
            db.SubscriptionCheckpoints.Add(checkpoint);
        }
        else
        {
            checkpoint.Position = (long)position;
        }
        await db.SaveChangesAsync();
    }
}
