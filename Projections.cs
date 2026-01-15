using System.Text.Json;
using EventStore.Client;
using Microsoft.Extensions.Caching.Distributed;

namespace Warehouse;


public class Projections
{
    private const int SNAPSHOT_INTERVAL = 50;
    private static readonly TimeSpan SNAPSHOT_TTL = TimeSpan.FromMinutes(5);

    public static async Task<Product?> GetProductProjection(
        Guid productId,
        EventStoreClient client,
        WarehouseDbContext db,
        IDistributedCache cache)
    {
        var cacheKey = $"product-snapshot-{productId}";
        var cachedSnapshot = await cache.GetStringAsync(cacheKey);

         if (cachedSnapshot != null) // Снапшот + Recent Events
         {
             var snapshotData = JsonSerializer.Deserialize<ProductSnapshot>(cachedSnapshot);
             if (snapshotData != null)
             {
                 var product = new ProductSnapshot(productId, snapshotData.QuantityInStock, snapshotData.Version, snapshotData.CreatedAt);
                 return await ApplyRecentEvents(productId, product, client, cache);
             }
         }

        // All events + save snapshot
        return await RebuildProjection(productId, client, db, cache);
    }

    private static async Task<Product?> RebuildProjection(
        Guid productId,
        EventStoreClient client,
        WarehouseDbContext db,
        IDistributedCache cache)
    {
        var dbProduct = await db.Products.FindAsync(productId);
        if (dbProduct == null) return null;
        
        // Create a snapshot from DB state
        var product = new ProductSnapshot(productId, dbProduct.QuantityInStock, dbProduct.Version, DateTime.UtcNow);

        // Apply events that might have happened since the DB update (catch-up)
        return await ApplyRecentEvents(productId, product, client, cache);
    }

    private static async Task<Product> ApplyRecentEvents(
        Guid productId, ProductSnapshot product, EventStoreClient client, IDistributedCache cache)
    {
        var events = client.ReadStreamAsync(
            Direction.Forwards,
            $"product-{productId}",
            StreamPosition.FromInt64(100)
        );

        try
        {
            var eventsCount = 0;
            await foreach (var resolvedEvent in events)
            {
                ApplyEvent(product, resolvedEvent);
                product.Version = resolvedEvent.Event.EventNumber.ToInt64();
                eventsCount++;
            }
            
            if (eventsCount >= SNAPSHOT_INTERVAL)
            {
                await SaveSnapshotToCache(productId, product, cache);
            }
        }
        catch (StreamNotFoundException)
        {
            
        }
        return product;
    }

    private static async Task SaveSnapshotToCache(Guid productId, ProductSnapshot product, IDistributedCache cache)
    {
        var snapshotData = new ProductSnapshot(
            product.Id,
            product.QuantityInStock,
            product.Version,
            DateTime.UtcNow
        );

        var serializedSnapshot = JsonSerializer.Serialize(snapshotData);

        await cache.SetStringAsync(
            $"product-snapshot-{productId}",
            serializedSnapshot,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = SNAPSHOT_TTL }
        );
    }

    private static void ApplyEvent(Product product, ResolvedEvent resolvedEvent)
    {
        var eventData = resolvedEvent.Event.Data.ToArray();

        switch (resolvedEvent.Event.EventType)
        {
            case nameof(OrderPlacedEvent):
                var orderPlacedEvent = JsonSerializer.Deserialize<OrderPlacedEvent>(eventData);
                if (orderPlacedEvent != null)
                    product.Apply(orderPlacedEvent);
                break;

            case nameof(OrderCancelledEvent):
                var orderCancelledEvent = JsonSerializer.Deserialize<OrderCancelledEvent>(eventData);
                if (orderCancelledEvent != null) product.Apply(orderCancelledEvent);
                break;

            case nameof(ProductRestockedEvent):
                var productRestockedEvent = JsonSerializer.Deserialize<ProductRestockedEvent>(eventData);
                if (productRestockedEvent != null) product.Apply(productRestockedEvent);
                break;

            default:
                Console.WriteLine($"Unknown event type: {resolvedEvent.Event.EventType}");
                break;
        }
    }
}