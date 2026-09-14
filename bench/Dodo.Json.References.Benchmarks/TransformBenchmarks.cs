using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Dodo.Json.References.Benchmarks;

[MemoryDiagnoser]
public class TransformBenchmarks
{
    private ReadOnlyMemory<byte> _preserved;
    private JsonSerializerOptions _options = null!;
    private PipeWriter _pipe = null!;

    [Params(20, 2_000, 20_000)]
    public int Orders { get; set; }

    [ParamsAllValues]
    public GraphShape Shape { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _options = CatalogContext.Default.Catalog.Options;

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            JsonSerializer.Serialize(writer, CatalogFactory.Create(Orders, Shape), CatalogContext.Default.Catalog);
        }

        _preserved = buffer.WrittenMemory;
        _pipe = PipeWriter.Create(Stream.Null, new StreamPipeWriterOptions(minimumBufferSize: 64 * 1024, leaveOpen: true));
    }

    [Benchmark]
    public ValueTask TransformToPipe()
        => JsonReferenceTransformer.TransformToPipe(_preserved, _pipe, _options, CancellationToken.None);
}
