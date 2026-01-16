using EventStore.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Warehouse;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Регистрация клиента для EventStoreDB
var eventStoreConnection = builder.Configuration["EventStore:ConnectionString"] ?? "esdb://localhost:2113?tls=false";
builder.Services.AddEventStoreClient(eventStoreConnection);

// Регистрация DbContext для PostgreSQL
builder.Services.AddDbContext<WarehouseDbContext>(options =>
{
    var postgresConnection = builder.Configuration["Postgres:ConnectionString"] ??
        "Host=localhost;Port=5432;Database=warehouse;Username=postgres;Password=postgres";
    options.UseNpgsql(postgresConnection,
        b => b.MigrationsAssembly("Warehouse"));
});

// Redis для хранения снапшотов
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration["Redis:Configuration"] ?? "localhost:6379";
});

builder.Services.AddHttpClient("delivery", client =>
{
    var baseUrl = builder.Configuration["Delivery:BaseUrl"] ?? "http://localhost:8081";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
});

// Register Background Service
builder.Services.AddHostedService<EventStoreSubscriptionService>();

var app = builder.Build();

app.MapPost("/orders", async (PlaceOrderCommand command, EventStoreClient client, WarehouseDbContext db, IDistributedCache cache, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
{
    // Получаем текущее состояние товара через проекцию
    
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var product = await Projections.GetProductProjection(command.ProductId, client, db, cache);
    stopwatch.Stop();
    Console.WriteLine($"Projections.GetProductProjection took {stopwatch.ElapsedMilliseconds} ms");

    // Проверяем бизнес-логику (достаточно ли товара)
    if (product != null && product.QuantityInStock >= command.Quantity)
    {
        var orderId = Guid.NewGuid();
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

        var deliveryRequest = new CreateDeliveryRequestDto
        {
            OrderId = orderId.ToString("N"),
            Address = new DeliveryAddressDto
            {
                RecipientName = command.RecipientName,
                Phone = command.Phone,
                Line1 = command.AddressLine1,
                Line2 = command.AddressLine2,
                City = command.City,
                Region = command.Region,
                PostalCode = command.PostalCode,
                CountryCode = command.CountryCode,
            }
        };

        async Task<IResult> CompensateAsync()
        {
            Console.WriteLine("ERROR sending request to delivery service. Rolling back with compensation...");

            var cancelEvent = new OrderCancelledEvent(command.ProductId, command.Quantity);
            var cancelEventData = new EventData(
                Uuid.NewUuid(),
                nameof(OrderCancelledEvent),
                JsonSerializer.SerializeToUtf8Bytes((object)cancelEvent)
            );

            await client.AppendToStreamAsync(
                $"product-{command.ProductId}",
                StreamState.Any,
                new[] { cancelEventData },
                cancellationToken: ct
            );
            Console.WriteLine("OrderCancelledEvent was published to EventStore. Compensation successfull");

            return Results.Problem("Delivery creation failed. Order has been compensated.", statusCode: 502);
        }

        var deliveryClient = httpClientFactory.CreateClient("delivery");
        try
        {
            Console.WriteLine("Sending request to delivery-service...");
            using var response = await deliveryClient.PostAsJsonAsync("/v1/deliveries", deliveryRequest, ct);
            if (!response.IsSuccessStatusCode)
            {
                return await CompensateAsync();
            }
        }
        catch (Exception)
        {
            return await CompensateAsync();
        }
        Console.WriteLine("Success creating order");
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

app.MapGet("/products", async (WarehouseDbContext db) =>
{
    return await db.Products.ToListAsync();
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

public record PlaceOrderCommand(
    Guid ProductId,
    int Quantity,
    string RecipientName,
    string Phone,
    string AddressLine1,
    string? AddressLine2,
    string City,
    string? Region,
    string PostalCode,
    string CountryCode
);
public record CancelOrderCommand(Guid ProductId, int Quantity);
public record RestockProductCommand(Guid ProductId, int Quantity);

public sealed class CreateDeliveryRequestDto
{
    public string OrderId { get; set; } = "";
    public DeliveryAddressDto Address { get; set; } = new();
}

public sealed class DeliveryAddressDto
{
    public string RecipientName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Line1 { get; set; } = "";
    public string? Line2 { get; set; }
    public string City { get; set; } = "";
    public string? Region { get; set; }
    public string PostalCode { get; set; } = "";
    public string CountryCode { get; set; } = "";
}

public class Product
{
    public Guid Id { get; private set; }
    public int QuantityInStock { get; private set; }
    public long Version { get; set; }

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
    public new long Version { get; set; } = version;
    public DateTime CreatedAt { get; set; } = createdAt;
}

public class WarehouseDbContext : DbContext
{
    public DbSet<Product> Products { get; set; }
    public DbSet<SubscriptionCheckpoint> SubscriptionCheckpoints { get; set; }

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


