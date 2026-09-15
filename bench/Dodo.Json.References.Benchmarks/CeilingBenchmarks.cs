using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Dodo.Json.References.Benchmarks;

[MemoryDiagnoser]
public class CeilingBenchmarks
{
    private ReadOnlyMemory<byte> _preserved;
    private JsonSerializerOptions _options = null!;
    private PipeWriter _pipe = null!;

    [Params(20_000)]
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
    public async Task RawCopy()
    {
        _pipe.Write(_preserved.Span);
        await _pipe.FlushAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public async Task ReadThenRawCopy()
    {
        var reader = new Utf8JsonReader(_preserved.Span);
        while (reader.Read())
        {
        }

        _pipe.Write(_preserved.Span);
        await _pipe.FlushAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public async Task ReadThenRewrite()
    {
        var reader = new Utf8JsonReader(_preserved.Span);
        var writer = new Utf8JsonWriter(_pipe, new JsonWriterOptions { Encoder = _options.Encoder, SkipValidation = true });
        await using (writer.ConfigureAwait(false))
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        writer.WriteStartObject();
                        break;
                    case JsonTokenType.EndObject:
                        writer.WriteEndObject();
                        break;
                    case JsonTokenType.StartArray:
                        writer.WriteStartArray();
                        break;
                    case JsonTokenType.EndArray:
                        writer.WriteEndArray();
                        break;
                    case JsonTokenType.PropertyName:
                        writer.WritePropertyName(reader.ValueSpan);
                        break;
                    case JsonTokenType.String:
                        writer.WriteStringValue(reader.ValueSpan);
                        break;
                    case JsonTokenType.Number:
                        writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                        break;
                    case JsonTokenType.True:
                        writer.WriteBooleanValue(value: true);
                        break;
                    case JsonTokenType.False:
                        writer.WriteBooleanValue(value: false);
                        break;
                    case JsonTokenType.Null:
                        writer.WriteNullValue();
                        break;
                    default:
                        break;
                }
            }
        }

        await _pipe.FlushAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public ValueTask Transform()
        => JsonReferenceTransformer.TransformToPipe(_preserved, _pipe, _options, CancellationToken.None);
}
