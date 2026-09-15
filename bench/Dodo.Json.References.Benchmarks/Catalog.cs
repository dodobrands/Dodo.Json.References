using System.Text.Json.Serialization;

namespace Dodo.Json.References.Benchmarks;

public sealed class Catalog
{
    public List<Customer> Customers { get; set; } = [];
    public List<Product> Products { get; set; } = [];
    public List<Order> Orders { get; set; } = [];
}

public sealed class Customer
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string City { get; set; } = "";
}

public sealed class Product
{
    public int Sku { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class LineItem
{
    public Product? Product { get; set; }
    public int Quantity { get; set; }
    public decimal Price { get; set; }
}

public sealed class Order
{
    public int Number { get; set; }
    public Customer? Customer { get; set; }
    public List<LineItem> Lines { get; set; } = [];
    public string Note { get; set; } = "";
}

[JsonSourceGenerationOptions(ReferenceHandler = JsonKnownReferenceHandler.Preserve)]
[JsonSerializable(typeof(Catalog))]
public sealed partial class CatalogContext : JsonSerializerContext;
