using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipelines;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
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

    private static ReadOnlySpan<byte> JsonWhitespaceOrColon
        => " \t\r\n:"u8;

    private static readonly JsonEncodedText EncodedId = JsonEncodedText.Encode(Utf8Id);
    private static readonly JsonEncodedText EncodedRef = JsonEncodedText.Encode(Utf8Ref);
    private static readonly JsonEncodedText EncodedValues = JsonEncodedText.Encode(Utf8Values);

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
        var writer = new Utf8JsonWriter(bufferWriter, GetWriterOptions(options, indented: false));
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
        var writer = new Utf8JsonWriter(bufferWriter, GetWriterOptions(options, indented: false));
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
        var writer = new Utf8JsonWriter(bufferWriter, GetWriterOptions(options, indented: false));
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
        var writer = new Utf8JsonWriter(bufferWriter, GetWriterOptions(options, indented: false));
        await using (writer.ConfigureAwait(false))
        {
            JsonSerializer.Serialize(writer, payload, typeInfo);
        }

        await TransformToPipe(bufferWriter.WrittenMemory, output, options, ct).ConfigureAwait(false);
    }

    private static JsonWriterOptions GetWriterOptions(JsonSerializerOptions options)
        => GetWriterOptions(options, options.WriteIndented);

    // The intermediate buffer is byte-scanned for "$ref":" and must never be indented; the final writer honors WriteIndented.
    private static JsonWriterOptions GetWriterOptions(JsonSerializerOptions options, bool indented)
        => new()
        {
            Encoder = options.Encoder,
            Indented = indented,
            IndentCharacter = options.IndentCharacter,
            IndentSize = options.IndentSize,
            MaxDepth = options.MaxDepth,
            NewLine = options.NewLine,
            SkipValidation = true
        };

    // PipeWriter wrapping keeps the writer in IBufferWriter mode: pooled segments instead of buffering the whole document.
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
        var writer = new Utf8JsonWriter(output, GetWriterOptions(options));
        await using (writer.ConfigureAwait(false))
        {
            await TransformCore(jsonBytes, writer, options.MaxDepth, ct).ConfigureAwait(false);
        }

        // Utf8JsonWriter.DisposeAsync with IBufferWriter only calls Advance(), not FlushAsync()
        var flush = await output.FlushAsync(ct).ConfigureAwait(false);
        if (flush.IsCanceled)
        {
            throw new OperationCanceledException();
        }
    }

    private static ValueTask TransformCore(
        ReadOnlyMemory<byte> jsonBytes,
        Utf8JsonWriter writer,
        int maxDepth,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        PathSegment[]? rentedPathStack = null;
        byte[]? rentedIndentScratch = null;
        var indented = writer.Options.Indented;
        Span<PathSegment> pathStack = stackalloc PathSegment[PointerPathBuilder.StackAllocDepth];
        Span<byte> pathScratch = stackalloc byte[PointerPathBuilder.ScratchSize];
        var pathStackDepth = 0;
        var jsonSpan = jsonBytes.Span;

        ulong[]? referencedBitmap = null;
        long[]? idPaths = null;
        var pathArena = Array.Empty<byte>();
        var pathArenaUsed = 0;

        try
        {
            // Pass 1: byte-scan for "$ref":" — decimal ids into the stack-first builder, exotic ones into a lazy overflow set.
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
            var reader = new Utf8JsonReader(jsonBytes.Span, new JsonReaderOptions { MaxDepth = maxDepth });

            Dictionary<string, long>? idToPathOverflow = null;
            Span<char> idDecodeSpan = stackalloc char[IdDecodeSpanSize];
            Span<char> pendingDroppedId = stackalloc char[IdDecodeSpanSize];
            string? pendingDroppedIdString = null;
            // -1 none; -2 numeric parked; >= 0 chars in pendingDroppedId (0 with non-null string = long id).
            var pendingDroppedIdLen = -1;
            var pendingDroppedNumericId = 0u;
            var pendingMetadata = PendingMetadata.None;
            var pendingPropertyOffset = -1;
            var pendingPropertyLength = 0;

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
                        writer.WriteStartObject();
                        break;

                    case JsonTokenType.EndObject:
                        pendingDroppedIdLen = -1;
                        pendingDroppedIdString = null;
                        writer.WriteEndObject();
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
                        writer.WriteStartArray();
                        break;

                    case JsonTokenType.EndArray:
                        writer.WriteEndArray();
                        if (pathStackDepth > 0 && pathStack[pathStackDepth - 1].IsArray)
                        {
                            pathStackDepth--;
                        }

                        break;

                    case JsonTokenType.PropertyName:
                        var propSpan = reader.ValueSpan;
                        var isMetadataCandidate = !propSpan.IsEmpty && propSpan[0] == (byte)'$';
                        if (isMetadataCandidate && propSpan.SequenceEqual(Utf8Id) && IsStringValueNext(jsonBytes.Span, (int)reader.BytesConsumed))
                        {
                            reader.Read();

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

                                    writer.WritePropertyName(EncodedId);
                                    writer.WriteRawValue(PointerPathBuilder.Slice(pathArena, pathRef), skipInputValidation: true);
                                }
                                else
                                {
                                    // Deferred drop: STJ requires $id before $values, so a wrapper's id is re-emitted if $values follows.
                                    pendingDroppedNumericId = numericId;
                                    pendingDroppedIdLen = -2;
                                    pendingDroppedIdString = null;
                                }

                                break;
                            }

                            string? idString = null;
                            scoped ReadOnlySpan<char> idSlice;
                            if (reader.ValueSpan.Length <= IdDecodeSpanSize)
                            {
                                var idLen = reader.CopyString(idDecodeSpan);
                                idSlice = idDecodeSpan[..idLen];
                            }
                            else
                            {
                                idString = reader.GetString()!;
                                idSlice = idString;
                            }

                            if (referencedOverflow is not null
                                && referencedOverflow.GetAlternateLookup<ReadOnlySpan<char>>().Contains(idSlice))
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

                                writer.WritePropertyName(EncodedId);
                                writer.WriteRawValue(PointerPathBuilder.Slice(pathArena, pathRef), skipInputValidation: true);
                            }
                            else if (idString is null)
                            {
                                idSlice.CopyTo(pendingDroppedId);
                                pendingDroppedIdLen = idSlice.Length;
                                pendingDroppedIdString = null;
                            }
                            else
                            {
                                pendingDroppedIdString = idString;
                                pendingDroppedIdLen = 0;
                            }

                            break;
                        }

                        pendingPropertyOffset = (int)reader.TokenStartIndex + 1; // skip opening quote
                        pendingPropertyLength = propSpan.Length;

                        if (pendingDroppedIdLen != -1)
                        {
                            if (isMetadataCandidate && propSpan.SequenceEqual(Utf8Values))
                            {
                                writer.WritePropertyName(EncodedId);
                                if (pendingDroppedIdLen == -2)
                                {
                                    pendingDroppedNumericId.TryFormat(pendingDroppedId, out var formatted, provider: CultureInfo.InvariantCulture);
                                    writer.WriteStringValue(pendingDroppedId[..formatted]);
                                }
                                else if (pendingDroppedIdString is not null)
                                {
                                    writer.WriteStringValue(pendingDroppedIdString);
                                }
                                else
                                {
                                    writer.WriteStringValue(pendingDroppedId[..pendingDroppedIdLen]);
                                }
                            }

                            pendingDroppedIdLen = -1;
                            pendingDroppedIdString = null;
                        }

                        if (isMetadataCandidate && propSpan.SequenceEqual(Utf8Ref))
                        {
                            writer.WritePropertyName(EncodedRef);
                            pendingMetadata = PendingMetadata.Ref;
                        }
                        else if (isMetadataCandidate && propSpan.SequenceEqual(Utf8Values))
                        {
                            writer.WritePropertyName(EncodedValues);
                            pendingMetadata = PendingMetadata.Values;
                        }
                        else if (!reader.ValueIsEscaped)
                        {
                            writer.WritePropertyName(propSpan);
                        }
                        else
                        {
                            // propSpan is still escaped here; raw re-emission would double-escape.
                            writer.WritePropertyName(reader.GetString()!);
                        }

                        break;

                    case JsonTokenType.String:
                        if (pendingMetadata == PendingMetadata.Ref)
                        {
                            pendingPropertyOffset = -1;
                            // The bit test keeps refs the pass-1 scan never saw (a converter's WriteRawValue can
                            // emit "$ref" with arbitrary spacing) away from slots the sparse rent-clear skipped.
                            if (NumericId.TryRead(ref reader, idDecodeSpan, out var numericRefId)
                                && numericRefId <= maxNumericId
                                && (referencedBitmap[numericRefId >> 6] & (1UL << (int)numericRefId)) != 0
                                && idPaths[numericRefId] != 0)
                            {
                                writer.WriteRawValue(PointerPathBuilder.Slice(pathArena, idPaths[numericRefId]), skipInputValidation: true);
                            }
                            else
                            {
                                scoped ReadOnlySpan<char> refIdSlice;
                                if (reader.ValueSpan.Length <= IdDecodeSpanSize)
                                {
                                    var refIdLen = reader.CopyString(idDecodeSpan);
                                    refIdSlice = idDecodeSpan[..refIdLen];
                                }
                                else
                                {
                                    refIdSlice = reader.GetString();
                                }

                                if (idToPathOverflow is not null
                                    && idToPathOverflow.GetAlternateLookup<ReadOnlySpan<char>>()
                                        .TryGetValue(refIdSlice, out var overflowPath))
                                {
                                    writer.WriteRawValue(PointerPathBuilder.Slice(pathArena, overflowPath), skipInputValidation: true);
                                }
                                else
                                {
                                    writer.WriteStringValue(refIdSlice);
                                }
                            }
                        }
                        else
                        {
                            var isStringElement = pendingPropertyOffset < 0;
                            PointerPathBuilder.BumpIndexForArrayElement(pathStack, pathStackDepth, pendingPropertyOffset);
                            pendingPropertyOffset = -1;
                            var tokenStart = (int)reader.TokenStartIndex;
                            var tokenEnd = (int)reader.BytesConsumed;
                            WriteRawScalar(writer, jsonSpan[tokenStart..tokenEnd], indented && isStringElement, ref rentedIndentScratch);
                        }

                        pendingMetadata = PendingMetadata.None;
                        break;

                    case JsonTokenType.Number:
                        var isNumberElement = pendingPropertyOffset < 0;
                        PointerPathBuilder.BumpIndexForArrayElement(pathStack, pathStackDepth, pendingPropertyOffset);
                        pendingPropertyOffset = -1;
                        // Raw copy preserves exact format (439.0 vs 439).
                        pendingMetadata = PendingMetadata.None;
                        WriteRawScalar(writer, reader.ValueSpan, indented && isNumberElement, ref rentedIndentScratch);
                        break;

                    case JsonTokenType.True:
                        PointerPathBuilder.BumpIndexForArrayElement(pathStack, pathStackDepth, pendingPropertyOffset);
                        pendingPropertyOffset = -1;
                        pendingMetadata = PendingMetadata.None;
                        writer.WriteBooleanValue(true);
                        break;

                    case JsonTokenType.False:
                        PointerPathBuilder.BumpIndexForArrayElement(pathStack, pathStackDepth, pendingPropertyOffset);
                        pendingPropertyOffset = -1;
                        pendingMetadata = PendingMetadata.None;
                        writer.WriteBooleanValue(false);
                        break;

                    case JsonTokenType.Null:
                        PointerPathBuilder.BumpIndexForArrayElement(pathStack, pathStackDepth, pendingPropertyOffset);
                        pendingPropertyOffset = -1;
                        pendingMetadata = PendingMetadata.None;
                        writer.WriteNullValue();
                        break;
                }
            }
        }
        finally
        {
            if (rentedPathStack is not null)
            {
                ArrayPool<PathSegment>.Shared.Return(rentedPathStack);
            }

            if (rentedIndentScratch is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedIndentScratch);
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

        return ValueTask.CompletedTask;
    }

    private static void WriteRawScalar(Utf8JsonWriter writer, ReadOnlySpan<byte> value, bool isIndentedElement, ref byte[]? scratch)
    {
        if (!isIndentedElement || writer.CurrentDepth == 0)
        {
            writer.WriteRawValue(value, skipInputValidation: true);
            return;
        }

        // WriteRawValue emits the separator but not the newline and indentation an indented writer puts before an array element.
        var options = writer.Options;
        var indentLength = options.IndentSize * writer.CurrentDepth;
        var length = options.NewLine.Length + indentLength + value.Length;
        if (scratch is null || scratch.Length < length)
        {
            if (scratch is not null)
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }

            scratch = ArrayPool<byte>.Shared.Rent(length);
        }

        var element = scratch.AsSpan(0, length);
        var newLineLength = Encoding.UTF8.GetBytes(options.NewLine, element);
        element.Slice(newLineLength, indentLength).Fill((byte)options.IndentCharacter);
        value.CopyTo(element[(newLineLength + indentLength)..]);
        writer.WriteRawValue(element, skipInputValidation: true);
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
