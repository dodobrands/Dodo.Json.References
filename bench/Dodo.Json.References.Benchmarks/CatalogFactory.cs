namespace Dodo.Json.References.Benchmarks;

public enum GraphShape
{
    Unique,
    SharedPoolFirst,
    SharedPoolInline,
}

public static class CatalogFactory
{
    public static Catalog Create(int orderCount, GraphShape shape)
    {
        var random = new Random(20260914);
        var catalog = new Catalog();
        var shared = shape != GraphShape.Unique;
        var poolSize = shared ? Math.Max(8, orderCount / 8) : orderCount * 4;
        var customers = new List<Customer>(poolSize);
        var products = new List<Product>(poolSize);

        for (var i = 0; i < poolSize; i++)
        {
            customers.Add(new Customer { Number = i, Name = $"Customer {i}", City = Cities[i % Cities.Length] });
            products.Add(new Product { Sku = 100000 + i, Name = $"Product {i}", Description = Descriptions[i % Descriptions.Length] });
        }

        if (shape != GraphShape.SharedPoolInline)
        {
            catalog.Customers = customers;
            catalog.Products = products;
        }

        for (var i = 0; i < orderCount; i++)
        {
            var order = new Order
            {
                Number = i,
                Customer = shared ? customers[random.Next(poolSize)] : customers[i * 4 % poolSize],
                Note = Notes[i % Notes.Length],
            };

            var lineCount = 1 + random.Next(4);
            for (var line = 0; line < lineCount; line++)
            {
                order.Lines.Add(new LineItem
                {
                    Product = shared ? products[random.Next(poolSize)] : products[(i * 4 + line) % poolSize],
                    Quantity = 1 + random.Next(9),
                    Price = 1.25m * (1 + random.Next(400)),
                });
            }

            catalog.Orders.Add(order);
        }

        return catalog;
    }

    private static readonly string[] Cities = ["Moscow", "Berlin", "Lisbon", "Taipei", "Nur-Sultan", "Riyadh"];

    private static readonly string[] Descriptions =
    [
        "Thin crust, tomato base, mozzarella, basil.",
        "Deep pan, barbecue base, chicken, red onion, sweetcorn.",
        "Gluten free base, pesto, goat cheese, walnut.",
        "Classic base, four cheeses, oregano.",
    ];

    private static readonly string[] Notes =
    [
        "Leave at the door.",
        "Call on arrival; the bell does not work.",
        "Extra napkins please.",
        "",
    ];
}
