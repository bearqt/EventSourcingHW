using EventStore.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Регистрация клиента для EventStoreDB
builder.Services.AddEventStoreClient("esdb://localhost:2113?tls=false");

// Регистрация DbContext для PostgreSQL
builder.Services.AddDbContext<WarehouseDbContext>(options =>
{
    options.UseNpgsql("Host=localhost;Port=5432;Database=warehouse;Username=postgres;Password=postgres",
        b => b.MigrationsAssembly("Warehouse"));
});

// Redis для хранения снапшотов
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = "localhost:6379";
});

var app = builder.Build();

app.MapPost("/orders", async (PlaceOrderCommand command, EventStoreClient client, WarehouseDbContext db, IDistributedCache cache) =>
{
    // Получаем текущее состояние товара через проекцию
    var product = await Projections.GetProductProjection(command.ProductId, client, db, cache);

    // Проверяем бизнес-логику (достаточно ли товара)
    if (product.QuantityInStock >= command.Quantity)
    {
        // Создаем экземпляр события, используя конструктор record'а
        var orderPlacedEvent = new OrderPlacedEvent(command.ProductId, command.Quantity);

        // Готовим событие к отправке (сериализуем в JSON/UTF8)
        var eventData = new EventData(
            Uuid.NewUuid(),
            nameof(OrderPlacedEvent), // Имя события - теперь безопасно получаем из типа
            JsonSerializer.SerializeToUtf8Bytes((object)orderPlacedEvent) // Приводим к object для полиморфизма
        );

        // Записываем событие в поток, связанный с продуктом
        await client.AppendToStreamAsync(
            $"product-{command.ProductId}", // Имя потока
            StreamState.Any, // Не проверяем версию потока
            new[] { eventData }
        );

        return Results.Ok("Order placed successfully.");
    }
    else
    {
        return Results.BadRequest("Insufficient stock.");
    }
});

app.MapPost("/orders/cancel", async (CancelOrderCommand command, EventStoreClient client, WarehouseDbContext db) =>
{
    var orderCancelledEvent = new OrderCancelledEvent(command.ProductId, command.Quantity);

    var eventData = new EventData(
        Uuid.NewUuid(),
        nameof(OrderCancelledEvent),
        JsonSerializer.SerializeToUtf8Bytes((object)orderCancelledEvent)
    );

    await client.AppendToStreamAsync(
        $"product-{command.ProductId}",
        StreamState.Any,
        new[] { eventData }
    );

    return Results.Ok("Order cancelled successfully.");
});

app.MapPost("/products/restock", async (RestockProductCommand command, EventStoreClient client, WarehouseDbContext db) =>
{
    var productRestockedEvent = new ProductRestockedEvent(command.ProductId, command.Quantity);

    var eventData = new EventData(
        Uuid.NewUuid(),
        nameof(ProductRestockedEvent),
        JsonSerializer.SerializeToUtf8Bytes((object)productRestockedEvent)
    );

    await client.AppendToStreamAsync(
        $"product-{command.ProductId}",
        StreamState.Any,
        new[] { eventData }
    );

    return Results.Ok("Product restocked successfully.");
});

app.MapGet("/products", async (EventStoreClient client, WarehouseDbContext db, IDistributedCache cache) => 
{
    var productsIds = db.Products
            .Select(x => x.Id)
            .ToList();
    var projections = new List<Product>();
    foreach ( var productId in productsIds )
    {
        var projection = await Projections.GetProductProjection(productId, client, db, cache);
        if (projection is not null)
            projections.Add(projection);
    }
    return projections;
});

app.MapGet("/products/{productId}/events", (Guid productId, EventStoreClient client) =>
{
    // Читаем поток событий для продукта от начала до конца
    var events = client.ReadStreamAsync(
        Direction.Forwards,
        $"product-{productId}",
        StreamPosition.Start
    );
    // Десериализуем и возвращаем каждое событие
    return events.Select(e => JsonSerializer.Deserialize<OrderPlacedEvent>(e.Event.Data.ToArray()));
});

app.Run();

// Базовый интерфейс для всех событий, добавляющий метаданные
public interface IEvent
{
    // Альтернатива - https://github.com/phatboyg/NewId
    Guid EventId => Guid.NewGuid();
    public DateTime OccurredOn => DateTime.UtcNow;
}

// Маркерный интерфейс для доменных событий.
public interface IDomainEvent : IEvent { }

// Событие, описывающее факт размещения заказа,
// определенное как неизменяемый record
public record OrderPlacedEvent(Guid ProductId, int Quantity) : IDomainEvent;
public record OrderCancelledEvent(Guid ProductId, int Quantity) : IDomainEvent;
public record ProductRestockedEvent(Guid ProductId, int Quantity) : IDomainEvent;

public record PlaceOrderCommand(Guid ProductId, int Quantity);
public record CancelOrderCommand(Guid ProductId, int Quantity);
public record RestockProductCommand(Guid ProductId, int Quantity);

public class Product
{
    public Guid Id { get; private set; }
    public int QuantityInStock { get; private set; }

    public Product(Guid id, int quantityInStock)
    {
        Id = id;
        QuantityInStock = quantityInStock;
    }

    // Apply events to mutate the state of the product.
    public void Apply(OrderPlacedEvent @event)
    {
        if (QuantityInStock >= @event.Quantity)
            QuantityInStock -= @event.Quantity;
        else
            throw new InvalidOperationException("Insufficient stock.");
    }

    public void Apply(OrderCancelledEvent @event)
    {
        QuantityInStock += @event.Quantity;
    }

    public void Apply(ProductRestockedEvent @event)
    {
        QuantityInStock += @event.Quantity;
    }
}

public class ProductSnapshot(Guid id, int quantityInStock, long version, DateTime createdAt) : Product(id, quantityInStock)
{
    public long Version { get; set; } = version;
    public DateTime CreatedAt { get; set; } = createdAt;
}

public class WarehouseDbContext : DbContext
{
    public DbSet<Product> Products { get; set; }

    public WarehouseDbContext(DbContextOptions<WarehouseDbContext> options) : base(options)
    {
    }

    public WarehouseDbContext()
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Product>().HasKey(p => p.Id);
    }
}

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
            var product = new ProductSnapshot(productId, snapshotData.QuantityInStock, snapshotData.Version, snapshotData.CreatedAt);

            return await ApplyRecentEvents(productId, product, client, cache);
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
        var product = await db.Products.FindAsync(productId);

        long eventsCount = 0;

        var events = client.ReadStreamAsync(Direction.Forwards, $"product-{productId}", StreamPosition.Start);
        try
        {
            await foreach (var resolvedEvent in events)
            {
                eventsCount++;
                ApplyEvent(product, resolvedEvent);
            }
        }
        catch (StreamNotFoundException)
        {
            // Stream does not exist yet, just ignore
        }
        

        var productSnapshot = new ProductSnapshot(product.Id, product.QuantityInStock, 1, DateTime.Now);

        await SaveSnapshotToCache(productId, productSnapshot, cache);

        return product;
        
    }

    private static async Task<Product> ApplyRecentEvents(
        Guid productId, ProductSnapshot product, EventStoreClient client, IDistributedCache cache)
    {
        var events = client.ReadStreamAsync(
            Direction.Forwards,
            $"product-{productId}",
            StreamPosition.FromInt64(product.Version + 1)
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

