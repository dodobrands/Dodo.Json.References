using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Dodo.Json.References.Benchmarks;

[MemoryDiagnoser]
public class SerializeBenchmarks
{
    private Catalog _catalog = null!;
    private PipeWriter _pipe = null!;

    [Params(20, 2_000, 20_000)]
    public int Orders { get; set; }

    [Params(true, false)]
    public bool Shared { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _catalog = CatalogFactory.Create(Orders, Shared);
        _pipe = PipeWriter.Create(Stream.Null, new StreamPipeWriterOptions(minimumBufferSize: 64 * 1024, leaveOpen: true));
    }

    [Benchmark(Baseline = true)]
    public void PreserveOnly()
    {
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        using var writer = new Utf8JsonWriter(buffer);
        JsonSerializer.Serialize(writer, _catalog, CatalogContext.Default.Catalog);
        writer.Flush();
        Stream.Null.Write(buffer.WrittenSpan);
    }

    [Benchmark]
    public Task PointersToStream()
        => JsonReferenceTransformer.SerializeWithPointers(_catalog, Stream.Null, CatalogContext.Default.Catalog);

    [Benchmark]
    public Task PointersToPipe()
        => JsonReferenceTransformer.SerializeWithPointers(_catalog, _pipe, CatalogContext.Default.Catalog);
}
