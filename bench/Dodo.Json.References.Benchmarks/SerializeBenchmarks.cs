using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BenchmarkDotNet.Attributes;

namespace Dodo.Json.References.Benchmarks;

[MemoryDiagnoser]
public class SerializeBenchmarks
{
    private Catalog _catalog = null!;
    private JsonTypeInfo<Catalog> _typeInfo = null!;
    private PipeWriter _pipe = null!;

    [Params(20, 2_000, 20_000)]
    public int Orders { get; set; }

    [ParamsAllValues]
    public GraphShape Shape { get; set; }

    [Params(false, true)]
    public bool Indented { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _catalog = CatalogFactory.Create(Orders, Shape);
        _typeInfo = Indented
            ? (JsonTypeInfo<Catalog>)new JsonSerializerOptions(CatalogContext.Default.Options) { WriteIndented = true }.GetTypeInfo(typeof(Catalog))
            : CatalogContext.Default.Catalog;
        _pipe = PipeWriter.Create(Stream.Null, new StreamPipeWriterOptions(minimumBufferSize: 64 * 1024, leaveOpen: true));
    }

    [Benchmark(Baseline = true)]
    public void PreserveOnly()
    {
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = Indented });
        JsonSerializer.Serialize(writer, _catalog, _typeInfo);
        writer.Flush();
        Stream.Null.Write(buffer.WrittenSpan);
    }

    [Benchmark]
    public Task PointersToStream()
        => JsonReferenceTransformer.SerializeWithPointers(_catalog, Stream.Null, _typeInfo);

    [Benchmark]
    public Task PointersToPipe()
        => JsonReferenceTransformer.SerializeWithPointers(_catalog, _pipe, _typeInfo);
}
