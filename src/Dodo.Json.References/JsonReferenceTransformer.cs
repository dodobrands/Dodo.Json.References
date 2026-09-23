using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Dodo.Json.References;

/// <remarks>Pointers are emitted in the RFC 6901 section 6 URI-fragment form: section 3 escaping (~0/~1) then RFC 3986 percent-encoding.</remarks>
public static class JsonReferenceTransformer
{
    private const int IdDecodeSpanSize = 64;

    private const int StreamSegmentSize = 64 * 1024;

    private static readonly StreamPipeWriterOptions StreamOutputOptions =
        new(minimumBufferSize: StreamSegmentSize, leaveOpen: true);

    private static ReadOnlySpan<byte> Utf8Id
        => "$id"u8;

    private static ReadOnlySpan<byte> Utf8Ref
        => "$ref"u8;

    private static ReadOnlySpan<byte> Utf8Values
        => "$values"u8;

    internal static ReadOnlySpan<byte> JsonWhitespace
        => " \t\r\n"u8;

    private static ReadOnlySpan<byte> JsonWhitespaceOrColon
        => " \t\r\n:"u8;

    private const string ReflectionSerializationMessage =
        "Serializing from JsonSerializerOptions resolves metadata by reflection; use the JsonTypeInfo<T> overload under trimming or Native AOT.";
    /// <summary>Serializes <paramref name="payload"/> to <paramref name="output"/> with every <c>$id</c>/<c>$ref</c> rewritten to an RFC 6901 JSON Pointer.</summary>
    [RequiresUnreferencedCode(ReflectionSerializationMessage)]
    [RequiresDynamicCode(ReflectionSerializationMessage)]
    public static async Task SerializeWithPointers<T>(
        T payload,
        Stream output,
        JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);

        using var bufferWriter = new PooledJsonBufferWriter();
        var writer = new Utf8JsonWriter(bufferWriter, GetWriterOptions(options));
        await using (writer.ConfigureAwait(false))
        {
            JsonSerializer.Serialize<T>(writer, payload, options);
        }

        await TransformToStream(bufferWriter.WrittenMemory, output, options, ct).ConfigureAwait(false);
    }

    /// <summary>Serializes <paramref name="payload"/> to <paramref name="output"/> with every <c>$id</c>/<c>$ref</c> rewritten to an RFC 6901 JSON Pointer.</summary>
    public static async Task SerializeWithPointers<T>(
        T payload,
        Stream output,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(typeInfo);

        using var bufferWriter = new PooledJsonBufferWriter();
        var options = typeInfo.Options;
        var writer = new Utf8JsonWriter(bufferWriter, GetWriterOptions(options));
        await using (writer.ConfigureAwait(false))
        {
            JsonSerializer.Serialize(writer, payload, typeInfo);
        }

        await TransformToStream(bufferWriter.WrittenMemory, output, options, ct).ConfigureAwait(false);
    }

    /// <summary>Serializes <paramref name="payload"/> to <paramref name="output"/> with every <c>$id</c>/<c>$ref</c> rewritten to an RFC 6901 JSON Pointer.</summary>
    /// <remarks>Standalone <see cref="Pipe"/> callers must call <see cref="PipeWriter.Complete"/> afterwards; ASP.NET Core completes <c>HttpResponse.BodyWriter</c> itself.</remarks>
    [RequiresUnreferencedCode(ReflectionSerializationMessage)]
    [RequiresDynamicCode(ReflectionSerializationMessage)]
    public static async Task SerializeWithPointers<T>(
        T payload,
        PipeWriter output,
        JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);

        using var bufferWriter = new PooledJsonBufferWriter();
        var writer = new Utf8JsonWriter(bufferWriter, GetWriterOptions(options));
        await using (writer.ConfigureAwait(false))
        {
            JsonSerializer.Serialize<T>(writer, payload, options);
        }

        await TransformToPipe(bufferWriter.WrittenMemory, output, options, ct).ConfigureAwait(false);
    }

    /// <summary>Serializes <paramref name="payload"/> to <paramref name="output"/> with every <c>$id</c>/<c>$ref</c> rewritten to an RFC 6901 JSON Pointer.</summary>
    /// <remarks>Standalone <see cref="Pipe"/> callers must call <see cref="PipeWriter.Complete"/> afterwards; ASP.NET Core completes <c>HttpResponse.BodyWriter</c> itself.</remarks>
    public static async Task SerializeWithPointers<T>(
        T payload,
        PipeWriter output,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(typeInfo);

        using var bufferWriter = new PooledJsonBufferWriter();
        var options = typeInfo.Options;
        var writer = new Utf8JsonWriter(bufferWriter, GetWriterOptions(options));
        await using (writer.ConfigureAwait(false))
        {
            JsonSerializer.Serialize(writer, payload, typeInfo);
        }

        await TransformToPipe(bufferWriter.WrittenMemory, output, options, ct).ConfigureAwait(false);
    }

    private static JsonWriterOptions GetWriterOptions(JsonSerializerOptions options)
        => new()
        {
            Encoder = options.Encoder,
            Indented = options.WriteIndented,
            IndentCharacter = options.IndentCharacter,
            IndentSize = options.IndentSize,
            MaxDepth = options.MaxDepth,
            NewLine = options.NewLine,
            SkipValidation = true
        };

    internal static async ValueTask TransformToStream(
        ReadOnlyMemory<byte> jsonBytes,
        Stream output,
        JsonSerializerOptions options,
        CancellationToken ct)
    {
        var pipeWriter = PipeWriter.Create(output, StreamOutputOptions);
        Exception? failure = null;
        try
        {
            await TransformToPipe(jsonBytes, pipeWriter, options, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            await pipeWriter.CompleteAsync(failure).ConfigureAwait(false);
        }
    }

    internal static async ValueTask TransformToPipe(
        ReadOnlyMemory<byte> jsonBytes,
        PipeWriter output,
        JsonSerializerOptions options,
        CancellationToken ct)
    {
        TransformCore(jsonBytes.Span, output, options.MaxDepth, ct);

        var flush = await output.FlushAsync(ct).ConfigureAwait(false);
        if (flush.IsCanceled)
        {
            throw new OperationCanceledException();
        }
    }

    private static void TransformCore(
        ReadOnlySpan<byte> jsonSpan,
        IBufferWriter<byte> output,
        int maxDepth,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        PathSegment[]? rentedPathStack = null;
        Span<PathSegment> pathStack = stackalloc PathSegment[PointerPathBuilder.StackAllocDepth];
        Span<byte> pathScratch = stackalloc byte[PointerPathBuilder.ScratchSize];
        var pathStackDepth = 0;

        ulong[]? referencedBitmap = null;
        long[]? idPaths = null;
        var pathArena = Array.Empty<byte>();
        var pathArenaUsed = 0;

        try
        {
            // Pass 1: byte-scan for "$ref": then a quote — decimal ids into the stack-first builder, exotic ones into a lazy overflow set.
            HashSet<string>? referencedOverflow = null;
            uint maxNumericId;
            int bitmapWords;
            var referencedIds = new PooledSpanBuilder<uint>(
                stackalloc uint[ReferencedIdScanner.StackAllocIdCount],
                growFloor: ReferencedIdScanner.SizeHint
            );
            try
            {
                ReferencedIdScanner.Collect(jsonSpan, ref referencedIds, out maxNumericId, ref referencedOverflow);

                bitmapWords = (int)(maxNumericId >> 6) + 1;
                referencedBitmap = ArrayPool<ulong>.Shared.Rent(bitmapWords);
                Array.Clear(referencedBitmap, 0, bitmapWords);
                foreach (var id in referencedIds.WrittenSpan)
                {
                    referencedBitmap[id >> 6] |= 1UL << (int)id;
                }
            }
            finally
            {
                referencedIds.Dispose();
            }

            var trackedIdCount = 0;
            for (var w = 0; w < bitmapWords; w++)
            {
                trackedIdCount += BitOperations.PopCount(referencedBitmap[w]);
            }

            idPaths = ArrayPool<long>.Shared.Rent((int)maxNumericId + 1);
            PointerPathBuilder.ClearTrackedIdPaths(idPaths, referencedBitmap, bitmapWords, maxNumericId, trackedIdCount);
            var arenaEstimate = (long)(trackedIdCount + (referencedOverflow?.Count ?? 0)) * 48;
            pathArena = ArrayPool<byte>.Shared.Rent((int)Math.Clamp(arenaEstimate, 1024, 1 << 22));

            // Pass 2: transform and write, dropping unreferenced $id properties.
            var reader = new Utf8JsonReader(jsonSpan, new JsonReaderOptions { MaxDepth = maxDepth });

            Dictionary<string, long>? idToPathOverflow = null;
            Span<char> idDecodeSpan = stackalloc char[IdDecodeSpanSize];
            var pendingMetadata = PendingMetadata.None;
            var pendingPropertyOffset = -1;
            var pendingPropertyLength = 0;
            var copied = 0;
            var pendingDropEnd = -1;

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        if (pendingPropertyOffset >= 0)
                        {
                            if (pathStackDepth == pathStack.Length)
                                pathStack = PointerPathBuilder.GrowPathStack(pathStack, ref rentedPathStack);

                            pathStack[pathStackDepth++] = new PathSegment
                            {
                                PropertyNameOffset = pendingPropertyOffset,
                                PropertyNameLength = pendingPropertyLength,
                                ArrayIndex = -1,
                                IsArray = false
                            };
                            pendingPropertyOffset = -1;
                        }
                        else if (pathStackDepth > 0)
                        {
                            ref var top = ref pathStack[pathStackDepth - 1];
                            if (top.IsArray)
                                top.ArrayIndex++;
                        }

                        pendingMetadata = PendingMetadata.None;
                        break;

                    case JsonTokenType.EndObject:
                        if (pendingDropEnd >= 0)
                        {
                            copied = pendingDropEnd;
                            pendingDropEnd = -1;
                        }

                        if (pathStackDepth > 0 && !pathStack[pathStackDepth - 1].IsArray)
                        {
                            pathStackDepth--;
                        }

                        break;

                    case JsonTokenType.StartArray:
                        if (pathStackDepth == pathStack.Length)
                            pathStack = PointerPathBuilder.GrowPathStack(pathStack, ref rentedPathStack);

                        if (pendingMetadata == PendingMetadata.Values)
                        {
                            // $values is transparent in pointer paths: index-only segment, no name, no enclosing-index bump.
                            pendingPropertyOffset = -1;
                            pathStack[pathStackDepth++] = new PathSegment
                            {
                                PropertyNameOffset = -1,
                                PropertyNameLength = 0,
                                ArrayIndex = -1,
                                IsArray = true
                            };
                        }
                        else if (pendingPropertyOffset >= 0)
                        {
                            pathStack[pathStackDepth++] = new PathSegment
                            {
                                PropertyNameOffset = pendingPropertyOffset,
                                PropertyNameLength = pendingPropertyLength,
                                ArrayIndex = -1,
                                IsArray = true
                            };
                            pendingPropertyOffset = -1;
                        }
                        else
                        {
                            // Array-as-element or root array: bump the enclosing index, then track own indexes namelessly.
                            if (pathStackDepth > 0)
                            {
                                ref var top = ref pathStack[pathStackDepth - 1];
                                if (top.IsArray)
                                    top.ArrayIndex++;
                            }

                            pathStack[pathStackDepth++] = new PathSegment
                            {
                                PropertyNameOffset = -1,
                                PropertyNameLength = 0,
                                ArrayIndex = -1,
                                IsArray = true
                            };
                        }

                        pendingMetadata = PendingMetadata.None;
                        break;

                    case JsonTokenType.EndArray:
                        if (pathStackDepth > 0 && pathStack[pathStackDepth - 1].IsArray)
                        {
                            pathStackDepth--;
                        }

                        break;

                    case JsonTokenType.PropertyName:
                        var propSpan = reader.ValueSpan;
                        var isMetadataCandidate = !propSpan.IsEmpty && propSpan[0] == (byte)'$';
                        var isValues = isMetadataCandidate && propSpan.SequenceEqual(Utf8Values);
                        if (pendingDropEnd >= 0)
                        {
                            if (!isValues)
                            {
                                copied = pendingDropEnd;
                            }

                            pendingDropEnd = -1;
                        }

                        if (isMetadataCandidate && propSpan.SequenceEqual(Utf8Id) && IsStringValueNext(jsonSpan, (int)reader.BytesConsumed))
                        {
                            var nameStart = (int)reader.TokenStartIndex;
                            reader.Read();

                            long idPath = 0;
                            if (NumericId.TryRead(ref reader, idDecodeSpan, out var numericId))
                            {
                                if (numericId <= maxNumericId
                                    && (referencedBitmap[numericId >> 6] & (1UL << (int)numericId)) != 0)
                                {
                                    ref var pathRef = ref idPaths[numericId];
                                    if (pathRef == 0)
                                    {
                                        pathRef = PointerPathBuilder.AppendCurrentPath(jsonSpan, pathStack, pathStackDepth, pathScratch, ref pathArena, ref pathArenaUsed);
                                    }

                                    idPath = pathRef;
                                }
                            }
                            else if (referencedOverflow is not null)
                            {
                                scoped ReadOnlySpan<char> idSlice = reader.ValueSpan.Length <= IdDecodeSpanSize
                                    ? idDecodeSpan[..reader.CopyString(idDecodeSpan)]
                                    : reader.GetString();

                                if (referencedOverflow.GetAlternateLookup<ReadOnlySpan<char>>().Contains(idSlice))
                                {
                                    idToPathOverflow ??= new Dictionary<string, long>();
                                    ref var pathRef = ref CollectionsMarshal.GetValueRefOrAddDefault(
                                        idToPathOverflow.GetAlternateLookup<ReadOnlySpan<char>>(),
                                        idSlice,
                                        out var exists
                                    );

                                    if (!exists)
                                    {
                                        pathRef = PointerPathBuilder.AppendCurrentPath(jsonSpan, pathStack, pathStackDepth, pathScratch, ref pathArena, ref pathArenaUsed);
                                    }

                                    idPath = pathRef;
                                }
                            }

                            if (idPath != 0)
                            {
                                Splice(output, jsonSpan, ref copied, (int)reader.TokenStartIndex, (int)reader.BytesConsumed, PointerPathBuilder.Slice(pathArena, idPath));
                            }
                            else
                            {
                                // Deferred drop: STJ requires $id before $values, so a wrapper's id is kept if $values follows.
                                var (dropStart, dropEnd) = DroppedPropertyRange(jsonSpan, copied, nameStart, (int)reader.BytesConsumed);
                                output.Write(jsonSpan[copied..dropStart]);
                                copied = dropStart;
                                pendingDropEnd = dropEnd;
                            }

                            break;
                        }

                        pendingPropertyOffset = (int)reader.TokenStartIndex + 1; // skip opening quote
                        pendingPropertyLength = propSpan.Length;

                        if (isValues)
                        {
                            pendingMetadata = PendingMetadata.Values;
                        }
                        else if (isMetadataCandidate && propSpan.SequenceEqual(Utf8Ref))
                        {
                            pendingMetadata = PendingMetadata.Ref;
                        }

                        break;

                    case JsonTokenType.String:
                        if (pendingMetadata == PendingMetadata.Ref)
                        {
                            pendingPropertyOffset = -1;
                            // The bit test keeps refs the pass-1 scan never saw (a converter's WriteRawValue can
                            // emit "$ref" with arbitrary spacing) away from slots the sparse rent-clear skipped.
                            var refPath = 0L;
                            if (NumericId.TryRead(ref reader, idDecodeSpan, out var numericRefId))
                            {
                                if (numericRefId <= maxNumericId
                                    && (referencedBitmap[numericRefId >> 6] & (1UL << (int)numericRefId)) != 0)
                                {
                                    refPath = idPaths[numericRefId];
                                }
                            }
                            else if (idToPathOverflow is not null)
                            {
                                scoped ReadOnlySpan<char> refIdSlice = reader.ValueSpan.Length <= IdDecodeSpanSize
                                    ? idDecodeSpan[..reader.CopyString(idDecodeSpan)]
                                    : reader.GetString();
                                idToPathOverflow.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(refIdSlice, out refPath);
                            }

                            if (refPath != 0)
                            {
                                Splice(output, jsonSpan, ref copied, (int)reader.TokenStartIndex, (int)reader.BytesConsumed, PointerPathBuilder.Slice(pathArena, refPath));
                            }
                        }
                        else
                        {
                            PointerPathBuilder.BumpIndexForArrayElement(pathStack, pathStackDepth, pendingPropertyOffset);
                            pendingPropertyOffset = -1;
                        }

                        pendingMetadata = PendingMetadata.None;
                        break;

                    case JsonTokenType.Number:
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                    case JsonTokenType.Null:
                        PointerPathBuilder.BumpIndexForArrayElement(pathStack, pathStackDepth, pendingPropertyOffset);
                        pendingPropertyOffset = -1;
                        pendingMetadata = PendingMetadata.None;
                        break;
                }
            }

            output.Write(jsonSpan[copied..]);
        }
        finally
        {
            if (rentedPathStack is not null)
            {
                ArrayPool<PathSegment>.Shared.Return(rentedPathStack);
            }

            if (referencedBitmap is not null)
            {
                ArrayPool<ulong>.Shared.Return(referencedBitmap);
            }

            if (idPaths is not null)
            {
                ArrayPool<long>.Shared.Return(idPaths);
            }

            if (pathArena.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(pathArena);
            }
        }
    }

    private static void Splice(IBufferWriter<byte> output, ReadOnlySpan<byte> json, ref int copied, int start, int end, ReadOnlySpan<byte> replacement)
    {
        output.Write(json[copied..start]);
        output.Write(replacement);
        copied = end;
    }

    private static (int Start, int End) DroppedPropertyRange(ReadOnlySpan<byte> json, int copied, int nameStart, int valueEnd)
    {
        var previous = json[..nameStart].LastIndexOfAnyExcept(JsonWhitespace);
        if (json[previous] == (byte)',' && previous >= copied)
        {
            return (previous, valueEnd);
        }

        var start = Math.Max(previous + 1, copied);
        var next = valueEnd + json[valueEnd..].IndexOfAnyExcept(JsonWhitespace);
        return json[next] == (byte)',' ? (start, next + 1) : (start, next);
    }

    private static bool IsStringValueNext(ReadOnlySpan<byte> json, int afterName)
    {
        var rest = json[afterName..];
        return rest[rest.IndexOfAnyExcept(JsonWhitespaceOrColon)] == (byte)'"';
    }

    private enum PendingMetadata : byte
    {
        None,
        Ref,
        Values
    }
}
