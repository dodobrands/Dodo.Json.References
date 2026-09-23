using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Dodo.Json.References.Benchmarks;

public static class RealPayload
{
    public const string PathVariable = "DODO_JSON_REFERENCES_BENCH_PAYLOAD";

    public static ReadOnlyMemory<byte> Load()
    {
        var path = Environment.GetEnvironmentVariable(PathVariable);
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException($"Set {PathVariable} to a ReferenceHandler.Preserve JSON file.");
        }

        return File.ReadAllBytes(path);
    }

    public static async Task<int> Transform(string source, string destination)
    {
        var json = await File.ReadAllBytesAsync(source).ConfigureAwait(false);
        await using var output = File.Create(destination);
        var options = new JsonSerializerOptions();
        await JsonReferenceTransformer.TransformToStream(json, output, options, CancellationToken.None).ConfigureAwait(false);
        return json.Length;
    }
}

[MemoryDiagnoser]
public class RealPayloadBenchmarks
{
    private ReadOnlyMemory<byte> _preserved;
    private JsonSerializerOptions _options = null!;
    private ReadOnlyMemory<byte> _preservedIndented;
    private PipeWriter _pipe = null!;

    [GlobalSetup]
    public void Setup()
    {
        _preserved = RealPayload.Load();
        _options = new JsonSerializerOptions();
        _preservedIndented = Indent(_preserved);
        _pipe = PipeWriter.Create(Stream.Null, new StreamPipeWriterOptions(minimumBufferSize: 64 * 1024, leaveOpen: true));
    }

    [Benchmark]
    public long ReaderOnly()
    {
        var reader = new Utf8JsonReader(_preserved.Span);
        long tokens = 0;
        while (reader.Read())
        {
            tokens++;
        }

        return tokens;
    }

    [Benchmark]
    public long ReaderOnlyIndented()
    {
        var reader = new Utf8JsonReader(_preservedIndented.Span);
        long tokens = 0;
        while (reader.Read())
        {
            tokens++;
        }

        return tokens;
    }

    [Benchmark(Baseline = true)]
    public ValueTask Transform()
        => JsonReferenceTransformer.TransformToPipe(_preserved, _pipe, _options, CancellationToken.None);

    [Benchmark]
    public ValueTask TransformIndented()
        => JsonReferenceTransformer.TransformToPipe(_preservedIndented, _pipe, _options, CancellationToken.None);

    private static ReadOnlyMemory<byte> Indent(ReadOnlyMemory<byte> json)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var document = JsonDocument.Parse(json);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            document.WriteTo(writer);
        }

        return buffer.WrittenMemory;
    }
}
