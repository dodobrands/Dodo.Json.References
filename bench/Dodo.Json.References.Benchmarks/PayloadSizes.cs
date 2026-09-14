using System.Buffers;
using System.Text.Json;

namespace Dodo.Json.References.Benchmarks;

public static class PayloadSizes
{
    public static void Print()
    {
        foreach (var orders in new[] { 20, 2_000, 20_000 })
        {
            foreach (var shared in new[] { true, false })
            {
                var buffer = new ArrayBufferWriter<byte>();
                using var writer = new Utf8JsonWriter(buffer);
                JsonSerializer.Serialize(writer, CatalogFactory.Create(orders, shared), CatalogContext.Default.Catalog);
                writer.Flush();

                var output = new MemoryStream();
                JsonReferenceTransformer
                    .SerializeWithPointers(CatalogFactory.Create(orders, shared), output, CatalogContext.Default.Catalog)
                    .GetAwaiter()
                    .GetResult();

                Console.WriteLine($"orders={orders,6} shared={shared,-5} preserve={buffer.WrittenCount,10:N0}B pointers={output.Length,10:N0}B");
            }
        }
    }
}
