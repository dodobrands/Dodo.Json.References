using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipelines;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Dodo.Json.References;

/// <remarks>Pointers are emitted in the RFC 6901 section 6 URI-fragment form: section 3 escaping (~0/~1) then RFC 3986 percent-encoding.</remarks>
public static class JsonReferenceTransformer
{
    // Covers the reader's default MaxDepth of 64; deeper documents spill into pooled growth.
    private const int StackAllocPathDepth = 80;

    // Hoisted to one per-document scratch: zero-init is paid once, not per path.
    private const int PathScratchSize = 512;

    private const int StackAllocIdCount = 128;
    private const int IdDecodeSpanSize = 64;

    // Exclusive: renting bound + 1 slots would round up to the next ArrayPool bucket (32MB, not 16MB).
    private const uint MaxDenseNumericId = 1 << 21;

    private const int MaxDenseIdEscapedLength = 7 * 6;

    private static readonly SearchValues<byte> PointerLiteralBytes = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._!$&'()*+,;=:@?"u8
    );

    private static readonly byte[] UpperHexDigits = [.. "0123456789ABCDEF"u8];

    private const int StreamSegmentSize = 64 * 1024;

    private static readonly StreamPipeWriterOptions StreamOutputOptions =
        new(minimumBufferSize: StreamSegmentSize, leaveOpen: true);

    // Learned grow floor, capped so one pathological document cannot inflate it for good.
    private const int MaxReferencedIdsHint = 65_536;
    private static int _referencedIdsHint = 4096;

    private static ReadOnlySpan<byte> Utf8Id
        => "$id"u8;

    private static ReadOnlySpan<byte> Utf8Ref
        => "$ref"u8;

    private static ReadOnlySpan<byte> Utf8Values
        => "$values"u8;

    private static ReadOnlySpan<byte> RefPattern
        => "\"$ref\":\""u8;

    private static readonly JsonEncodedText EncodedId = JsonEncodedText.Encode(Utf8Id);
    private static readonly JsonEncodedText EncodedRef = JsonEncodedText.Encode(Utf8Ref);
    private static readonly JsonEncodedText EncodedValues = JsonEncodedText.Encode(Utf8Values);

    private static readonly byte[] RootPathBytes = [.. "\"#\""u8];

    private const string ReflectionSerializationMessage =
        "Serializing from JsonSerializerOptions resolves metadata by reflection; use the JsonTypeInfo<T> overload under trimming or Native AOT.";

    // Negative PropertyNameOffset = nameless segment ($values wrapper or array-in-array): contributes only its index.
    [StructLayout(LayoutKind.Sequential)]
    private struct PathSegment
    {
        public int PropertyNameOffset;
        public int PropertyNameLength;
        public int ArrayIndex;
        public bool IsArray;
    }

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
        Span<PathSegment> pathStack = stackalloc PathSegment[StackAllocPathDepth];
        Span<byte> pathScratch = stackalloc byte[PathScratchSize];
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
                stackalloc uint[StackAllocIdCount],
                growFloor: Volatile.Read(ref _referencedIdsHint)
            );
            try
            {
                CollectReferencedIds(jsonSpan, ref referencedIds, out maxNumericId, ref referencedOverflow);

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
            ClearTrackedIdPaths(idPaths, referencedBitmap, bitmapWords, maxNumericId, trackedIdCount);
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
            var isRefProperty = false;
            var isPendingValuesProperty = false;
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
                                pathStack = GrowPathStack(pathStack, ref rentedPathStack);

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

                        isRefProperty = false;
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
                            pathStack = GrowPathStack(pathStack, ref rentedPathStack);

                        if (isPendingValuesProperty)
                        {
                            // $values is transparent in pointer paths: index-only segment, no name, no enclosing-index bump.
                            isPendingValuesProperty = false;
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

                        isRefProperty = false;
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
                        if (isMetadataCandidate && propSpan.SequenceEqual(Utf8Id))
                        {
                            reader.Read();

                            if (TryReadNumericId(ref reader, idDecodeSpan, out var numericId))
                            {
                                if (numericId <= maxNumericId
                                    && (referencedBitmap[numericId >> 6] & (1UL << (int)numericId)) != 0)
                                {
                                    ref var pathRef = ref idPaths[numericId];
                                    if (pathRef == 0)
                                    {
                                        pathRef = AppendCurrentPath(jsonSpan, pathStack, pathStackDepth, pathScratch, ref pathArena, ref pathArenaUsed);
                                    }

                                    writer.WritePropertyName(EncodedId);
                                    writer.WriteRawValue(PathSlice(pathArena, pathRef), skipInputValidation: true);
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
                                    pathRef = AppendCurrentPath(jsonSpan, pathStack, pathStackDepth, pathScratch, ref pathArena, ref pathArenaUsed);
                                }

                                writer.WritePropertyName(EncodedId);
                                writer.WriteRawValue(PathSlice(pathArena, pathRef), skipInputValidation: true);
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
                            isRefProperty = true;
                        }
                        else if (isMetadataCandidate && propSpan.SequenceEqual(Utf8Values))
                        {
                            writer.WritePropertyName(EncodedValues);
                            isPendingValuesProperty = true;
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
                        if (isRefProperty)
                        {
                            pendingPropertyOffset = -1;
                            // The bit test keeps refs the pass-1 scan never saw (a converter's WriteRawValue can
                            // emit "$ref" with arbitrary spacing) away from slots the sparse rent-clear skipped.
                            if (TryReadNumericId(ref reader, idDecodeSpan, out var numericRefId)
                                && numericRefId <= maxNumericId
                                && (referencedBitmap[numericRefId >> 6] & (1UL << (int)numericRefId)) != 0
                                && idPaths[numericRefId] != 0)
                            {
                                writer.WriteRawValue(PathSlice(pathArena, idPaths[numericRefId]), skipInputValidation: true);
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
                                    writer.WriteRawValue(PathSlice(pathArena, overflowPath), skipInputValidation: true);
                                }
                                else
                                {
                                    writer.WriteStringValue(refIdSlice);
                                }
                            }

                            isRefProperty = false;
                        }
                        else
                        {
                            BumpIndexForArrayElement(pathStack, pathStackDepth, pendingPropertyOffset);
                            pendingPropertyOffset = -1;
                            var tokenStart = (int)reader.TokenStartIndex;
                            var tokenEnd = (int)reader.BytesConsumed;
                            writer.WriteRawValue(jsonSpan[tokenStart..tokenEnd], skipInputValidation: true);
                        }

                        break;

                    case JsonTokenType.Number:
                        BumpIndexForArrayElement(pathStack, pathStackDepth, pendingPropertyOffset);
                        pendingPropertyOffset = -1;
                        // Raw copy preserves exact format (439.0 vs 439).
                        isRefProperty = false;
                        writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                        break;

                    case JsonTokenType.True:
                        BumpIndexForArrayElement(pathStack, pathStackDepth, pendingPropertyOffset);
                        pendingPropertyOffset = -1;
                        isRefProperty = false;
                        writer.WriteBooleanValue(true);
                        break;

                    case JsonTokenType.False:
                        BumpIndexForArrayElement(pathStack, pathStackDepth, pendingPropertyOffset);
                        pendingPropertyOffset = -1;
                        isRefProperty = false;
                        writer.WriteBooleanValue(false);
                        break;

                    case JsonTokenType.Null:
                        BumpIndexForArrayElement(pathStack, pathStackDepth, pendingPropertyOffset);
                        pendingPropertyOffset = -1;
                        isRefProperty = false;
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

    private static void ClearTrackedIdPaths(long[] idPaths, ulong[] referencedBitmap, int bitmapWords, uint maxNumericId, int trackedIdCount)
    {
        // Measured crossover: ~1/16-1/8 density on 128B-line arm64, higher on 64B-line x64; a too-low
        // threshold costs a full-range memset on large sparse ranges, so 1/8 errs on the cheap side.
        if (trackedIdCount > (int)(maxNumericId >> 3))
        {
            Array.Clear(idPaths, 0, (int)maxNumericId + 1);
        }
        else
        {
            // Set bits cover every slot pass 2 can touch (a dangling $ref's slot is tracked but never written).
            for (var w = 0; w < bitmapWords; w++)
            {
                var word = referencedBitmap[w];
                while (word != 0)
                {
                    idPaths[(w << 6) + BitOperations.TrailingZeroCount(word)] = 0;
                    word &= word - 1;
                }
            }
        }
    }

    // Arena handle: offset in the high 32 bits, length in the low 32.
    private static ReadOnlySpan<byte> PathSlice(byte[] arena, long packed)
        => arena.AsSpan((int)(packed >> 32), (int)packed);

    private static long AppendToPathArena(ReadOnlySpan<byte> value, ref byte[] arena, ref int used)
    {
        if (arena.Length - used < value.Length)
        {
            var required = (long)used + value.Length;
            var grown = ArrayPool<byte>.Shared.Rent(
                (int)Math.Clamp(Math.Max((long)arena.Length * 2, required), required, Array.MaxLength)
            );
            arena.AsSpan(0, used).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(arena);
            arena = grown;
        }

        value.CopyTo(arena.AsSpan(used));
        var packed = ((long)used << 32) | (uint)value.Length;
        used += value.Length;
        return packed;
    }

    private static long AppendCurrentPath(
        ReadOnlySpan<byte> jsonSpan,
        ReadOnlySpan<PathSegment> pathStack,
        int depth,
        Span<byte> scratch,
        ref byte[] arena,
        ref int used)
    {
        if (depth == 0)
            return AppendToPathArena(RootPathBytes, ref arena, ref used);

        var path = new PooledSpanBuilder<byte>(scratch);
        try
        {
            path.Append((byte)'"');
            path.Append((byte)'#');

            var segments = pathStack[..depth];
            for (var i = 0; i < segments.Length; i++)
            {
                ref readonly var seg = ref segments[i];

                // Worst case per segment: separators + fully escaped name + ten index digits + quote.
                path.EnsureFree(3 * seg.PropertyNameLength + 13);
                var dst = path.FreeSpan;
                var pos = 0;
                if (seg.PropertyNameOffset >= 0)
                {
                    var propNameSpan = jsonSpan.Slice(seg.PropertyNameOffset, seg.PropertyNameLength);
                    dst[pos++] = (byte)'/';
                    if (!propNameSpan.ContainsAnyExcept(PointerLiteralBytes))
                    {
                        propNameSpan.CopyTo(dst[pos..]);
                        pos += propNameSpan.Length;
                    }
                    else
                    {
                        pos = WritePointerEscaped(propNameSpan, dst, pos);
                    }
                }

                if (seg is { IsArray: true, ArrayIndex: >= 0 })
                {
                    dst[pos++] = (byte)'/';
                    seg.ArrayIndex.TryFormat(dst[pos..], out var written, provider: CultureInfo.InvariantCulture);
                    pos += written;
                }

                path.Advance(pos);
            }

            path.Append((byte)'"');

            return AppendToPathArena(path.WrittenSpan, ref arena, ref used);
        }
        finally
        {
            path.Dispose();
        }
    }

    private static int WritePointerEscaped(ReadOnlySpan<byte> propertyName, Span<byte> buffer, int pos)
    {
        Span<byte> decoded = stackalloc byte[4];
        var i = 0;
        while (i < propertyName.Length)
        {
            scoped ReadOnlySpan<byte> logical;
            if (propertyName[i] == (byte)'\\')
            {
                i += DecodeJsonEscape(propertyName[i..], decoded, out var written);
                logical = decoded[..written];
            }
            else
            {
                logical = propertyName.Slice(i, 1);
                i++;
            }

            foreach (var b in logical)
            {
                pos = WritePointerByte(b, buffer, pos);
            }
        }

        return pos;
    }

    private static int WritePointerByte(byte b, Span<byte> buffer, int pos)
    {
        switch (b)
        {
            case (byte)'/':
                buffer[pos++] = (byte)'~';
                buffer[pos++] = (byte)'1';
                return pos;

            case (byte)'~':
                buffer[pos++] = (byte)'~';
                buffer[pos++] = (byte)'0';
                return pos;
        }

        if (PointerLiteralBytes.Contains(b))
        {
            buffer[pos++] = b;
            return pos;
        }

        buffer[pos++] = (byte)'%';
        buffer[pos++] = UpperHexDigits[b >> 4];
        buffer[pos++] = UpperHexDigits[b & 0xF];
        return pos;
    }

    private static int DecodeJsonEscape(ReadOnlySpan<byte> tail, scoped Span<byte> decoded, out int written)
    {
        written = 1;
        if (tail.Length < 2)
        {
            decoded[0] = tail[0];
            return 1;
        }

        switch (tail[1])
        {
            case (byte)'b':
                decoded[0] = 0x08;
                return 2;

            case (byte)'f':
                decoded[0] = 0x0C;
                return 2;

            case (byte)'n':
                decoded[0] = 0x0A;
                return 2;

            case (byte)'r':
                decoded[0] = 0x0D;
                return 2;

            case (byte)'t':
                decoded[0] = 0x09;
                return 2;

            case (byte)'u' when tail.Length >= 6:
                break;

            default:
                decoded[0] = tail[1];
                return 2;
        }

        _ = Utf8Parser.TryParse(tail[2..6], out int scalar, out _, 'x');
        var consumed = 6;
        if (char.IsHighSurrogate((char)scalar)
            && tail.Length >= 12
            && tail[6] == (byte)'\\'
            && tail[7] == (byte)'u'
            && Utf8Parser.TryParse(tail[8..12], out int low, out _, 'x')
            && char.IsLowSurrogate((char)low))
        {
            scalar = char.ConvertToUtf32((char)scalar, (char)low);
            consumed = 12;
        }

        written = (Rune.TryCreate(scalar, out var rune) ? rune : Rune.ReplacementChar).EncodeToUtf8(decoded);
        return consumed;
    }

    private static void CollectReferencedIds(
        ReadOnlySpan<byte> jsonSpan,
        ref PooledSpanBuilder<uint> ids,
        out uint maxNumericId,
        ref HashSet<string>? overflow)
    {
        maxNumericId = 0;
        var offset = 0;
        Span<char> decodeScratch = stackalloc char[MaxDenseIdEscapedLength];

        while (true)
        {
            var idx = jsonSpan[offset..].IndexOf(RefPattern);
            if (idx < 0)
                break;

            var quoteAt = offset + idx + RefPattern.Length - 1;
            var valueAt = quoteAt + 1;

            var endQuote = jsonSpan[valueAt..].IndexOf((byte)'"');
            if (endQuote < 0)
                break;

            var idBytes = jsonSpan.Slice(valueAt, endQuote);
            offset = valueAt + endQuote + 1;

            if (TryParseNumericId(idBytes, out var numericId))
            {
                ids.Append(numericId);
                if (numericId > maxNumericId)
                {
                    maxNumericId = numericId;
                }

                continue;
            }

            if (idBytes.IndexOf((byte)'\\') < 0)
            {
                (overflow ??= []).Add(System.Text.Encoding.UTF8.GetString(idBytes));
                continue;
            }

            var escapedReader = new Utf8JsonReader(jsonSpan[quoteAt..], isFinalBlock: false, state: default);
            if (!escapedReader.Read() || escapedReader.TokenType != JsonTokenType.String)
            {
                continue;
            }

            offset = quoteAt + (int)escapedReader.BytesConsumed;
            if (escapedReader.ValueSpan.Length > MaxDenseIdEscapedLength)
            {
                (overflow ??= []).Add(escapedReader.GetString()!);
                continue;
            }

            var decodedLength = escapedReader.CopyString(decodeScratch);
            var decodedId = decodeScratch[..decodedLength];
            if (TryParseNumericId(decodedId, out var escapedNumericId))
            {
                ids.Append(escapedNumericId);
                if (escapedNumericId > maxNumericId)
                {
                    maxNumericId = escapedNumericId;
                }

                continue;
            }

            (overflow ??= []).Add(new string(decodedId));
        }

        var learned = Math.Min(MaxReferencedIdsHint, (long)BitOperations.RoundUpToPowerOf2((uint)ids.Count));
        InterlockedMath.Max(ref _referencedIdsHint, (int)learned);
    }

    // Digits only, no leading zero, under the dense bound — exotic ids can never alias a dense slot.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseNumericId(ReadOnlySpan<byte> value, out uint id)
    {
        id = 0;
        if (value.Length is < 1 or > 7 || (value[0] == (byte)'0' && value.Length != 1))
        {
            return false;
        }

        uint parsed = 0;
        foreach (var b in value)
        {
            var digit = (uint)(b - (byte)'0');
            if (digit > 9)
            {
                return false;
            }

            parsed = parsed * 10 + digit;
        }

        if (parsed >= MaxDenseNumericId)
        {
            return false;
        }

        id = parsed;
        return true;
    }

    private static bool TryParseNumericId(ReadOnlySpan<char> value, out uint id)
    {
        id = 0;
        if (value.Length is < 1 or > 7 || (value[0] == '0' && value.Length != 1))
        {
            return false;
        }

        uint parsed = 0;
        foreach (var c in value)
        {
            var digit = (uint)(c - '0');
            if (digit > 9)
            {
                return false;
            }

            parsed = parsed * 10 + digit;
        }

        if (parsed >= MaxDenseNumericId)
        {
            return false;
        }

        id = parsed;
        return true;
    }

    private static bool TryReadNumericId(ref Utf8JsonReader reader, scoped Span<char> decodeScratch, out uint id)
    {
        id = 0;
        if (reader.TokenType != JsonTokenType.String)
        {
            return false;
        }

        if (!reader.ValueIsEscaped)
        {
            return TryParseNumericId(reader.ValueSpan, out id);
        }

        if (reader.ValueSpan.Length > MaxDenseIdEscapedLength)
        {
            return false;
        }

        var length = reader.CopyString(decodeScratch);
        return TryParseNumericId(decodeScratch[..length], out id);
    }

    // Primitive and null array elements advance the enclosing index too, or later pointer paths drift.
    private static void BumpIndexForArrayElement(Span<PathSegment> pathStack, int depth, int pendingPropertyOffset)
    {
        if (pendingPropertyOffset >= 0 || depth == 0)
            return;

        ref var top = ref pathStack[depth - 1];
        if (top.IsArray)
            top.ArrayIndex++;
    }

    private static Span<PathSegment> GrowPathStack(Span<PathSegment> current, ref PathSegment[]? rented)
    {
        var grown = ArrayPool<PathSegment>.Shared.Rent(current.Length * 2);
        current.CopyTo(grown);
        if (rented is not null)
        {
            ArrayPool<PathSegment>.Shared.Return(rented);
        }

        rented = grown;
        return grown;
    }
}