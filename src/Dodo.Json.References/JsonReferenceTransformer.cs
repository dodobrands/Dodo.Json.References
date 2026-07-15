using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipelines;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Dodo.Json.References;

/// <remarks>Ids must not require JSON escaping and the encoder must not escape '/' or '~' in property names (default and UnsafeRelaxed never do).</remarks>
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
    private static async ValueTask TransformToStream(
        ReadOnlyMemory<byte> jsonBytes,
        Stream output,
        JsonSerializerOptions options,
        CancellationToken ct)
    {
        var pipeWriter = PipeWriter.Create(output, StreamOutputOptions);
        try
        {
            await TransformToPipe(jsonBytes, pipeWriter, options, ct).ConfigureAwait(false);
        }
        finally
        {
            await pipeWriter.CompleteAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask TransformToPipe(
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
        await output.FlushAsync(ct).ConfigureAwait(false);
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

        // Pass 1: byte-scan for "$ref":" — decimal ids into the stack-first builder, exotic ones into a lazy overflow set.
        HashSet<string>? referencedOverflow = null;
        var referencedIds = new PooledSpanBuilder<uint>(
            stackalloc uint[StackAllocIdCount],
            growFloor: Volatile.Read(ref _referencedIdsHint)
        );
        CollectReferencedIds(jsonSpan, ref referencedIds, out var maxNumericId, ref referencedOverflow);

        // Dense structures are wiped only for the used range and returned dirty; every access is max-id-guarded.
        var bitmapWords = (int)(maxNumericId >> 6) + 1;
        var referencedBitmap = ArrayPool<ulong>.Shared.Rent(bitmapWords);
        Array.Clear(referencedBitmap, 0, bitmapWords);
        foreach (var id in referencedIds.WrittenSpan)
        {
            referencedBitmap[id >> 6] |= 1UL << (int)id;
        }

        referencedIds.Dispose();

        var idPaths = ArrayPool<byte[]?>.Shared.Rent((int)maxNumericId + 1);
        Array.Clear(idPaths, 0, (int)maxNumericId + 1);

        try
        {
            // Pass 2: transform and write, dropping unreferenced $id properties.
            var reader = new Utf8JsonReader(jsonBytes.Span, new JsonReaderOptions { MaxDepth = maxDepth });

            Dictionary<string, byte[]>? idToPathOverflow = null;
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

                            if (TryReadNumericId(ref reader, out var numericId))
                            {
                                if (numericId <= maxNumericId
                                    && (referencedBitmap[numericId >> 6] & (1UL << (int)numericId)) != 0)
                                {
                                    ref var pathRef = ref idPaths[numericId];
                                    pathRef ??= BuildCurrentPath(jsonSpan, pathStack, pathStackDepth, pathScratch);

                                    writer.WritePropertyName(EncodedId);
                                    writer.WriteRawValue(pathRef, skipInputValidation: true);
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
                                idToPathOverflow ??= new Dictionary<string, byte[]>();
                                ref var pathRef = ref CollectionsMarshal.GetValueRefOrAddDefault(
                                    idToPathOverflow.GetAlternateLookup<ReadOnlySpan<char>>(),
                                    idSlice,
                                    out var exists
                                );

                                if (!exists)
                                {
                                    pathRef = BuildCurrentPath(jsonSpan, pathStack, pathStackDepth, pathScratch);
                                }

                                writer.WritePropertyName(EncodedId);
                                writer.WriteRawValue(pathRef!, skipInputValidation: true);
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
                        else
                        {
                            pendingPropertyOffset = (int)reader.TokenStartIndex + 1; // skip opening quote

                            if (!reader.ValueIsEscaped)
                            {
                                writer.WritePropertyName(propSpan);
                                pendingPropertyLength = propSpan.Length;
                            }
                            else
                            {
                                // propSpan is still escaped here; raw re-emission would double-escape.
                                writer.WritePropertyName(reader.GetString()!);
                                pendingPropertyLength = jsonSpan[pendingPropertyOffset..(int)reader.BytesConsumed]
                                    .LastIndexOf((byte)'"');
                            }
                        }

                        break;

                    case JsonTokenType.String:
                        if (isRefProperty)
                        {
                            pendingPropertyOffset = -1;
                            if (TryReadNumericId(ref reader, out var numericRefId)
                                && numericRefId <= maxNumericId
                                && idPaths[numericRefId] is { } numericPath)
                            {
                                writer.WriteRawValue(numericPath, skipInputValidation: true);
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
                                    writer.WriteRawValue(overflowPath, skipInputValidation: true);
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
                        writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                        break;

                    case JsonTokenType.True:
                        BumpIndexForArrayElement(pathStack, pathStackDepth, pendingPropertyOffset);
                        pendingPropertyOffset = -1;
                        writer.WriteBooleanValue(true);
                        break;

                    case JsonTokenType.False:
                        BumpIndexForArrayElement(pathStack, pathStackDepth, pendingPropertyOffset);
                        pendingPropertyOffset = -1;
                        writer.WriteBooleanValue(false);
                        break;

                    case JsonTokenType.Null:
                        BumpIndexForArrayElement(pathStack, pathStackDepth, pendingPropertyOffset);
                        pendingPropertyOffset = -1;
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

            // Null the written (set-bit) slots or the pool keeps rooting the path arrays; dense bitmaps memset instead.
            var setBits = 0;
            for (var w = 0; w < bitmapWords; w++)
            {
                setBits += BitOperations.PopCount(referencedBitmap[w]);
            }

            // Above ~1/8 density scattered stores touch nearly every cache line anyway; a sequential wipe is cheaper.
            if (setBits > (int)(maxNumericId >> 3))
            {
                Array.Clear(idPaths, 0, (int)maxNumericId + 1);
            }
            else
            {
                // Set bits are exactly the written slots (idPaths is only ever touched at set-bit indexes).
                for (var w = 0; w < bitmapWords; w++)
                {
                    var word = referencedBitmap[w];
                    while (word != 0)
                    {
                        idPaths[(w << 6) + BitOperations.TrailingZeroCount(word)] = null;
                        word &= word - 1;
                    }
                }
            }

            ArrayPool<ulong>.Shared.Return(referencedBitmap);
            ArrayPool<byte[]?>.Shared.Return(idPaths);
        }

        return ValueTask.CompletedTask;
    }

    // Names are copied as raw escaped JSON bytes with RFC 6901 specials escaped ('/' ~1, '~' ~0).
    private static byte[] BuildCurrentPath(
        ReadOnlySpan<byte> jsonSpan,
        ReadOnlySpan<PathSegment> pathStack,
        int depth,
        Span<byte> scratch)
    {
        if (depth == 0)
            return RootPathBytes;

        var path = new PooledSpanBuilder<byte>(scratch);
        path.Append((byte)'"');
        path.Append((byte)'#');

        var segments = pathStack[..depth];
        for (var i = 0; i < segments.Length; i++)
        {
            ref readonly var seg = ref segments[i];

            // Worst case per segment: separators + fully escaped name + ten index digits + quote.
            path.EnsureFree(2 * seg.PropertyNameLength + 13);
            var dst = path.FreeSpan;
            var pos = 0;
            if (seg.PropertyNameOffset >= 0)
            {
                var propNameSpan = jsonSpan.Slice(seg.PropertyNameOffset, seg.PropertyNameLength);
                dst[pos++] = (byte)'/';
                if (!propNameSpan.ContainsAny((byte)'/', (byte)'~'))
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

        var result = path.ToArray();
        path.Dispose();
        return result;
    }

    private static int WritePointerEscaped(ReadOnlySpan<byte> propertyName, Span<byte> buffer, int pos)
    {
        foreach (var b in propertyName)
        {
            switch (b)
            {
                case (byte)'/':
                    buffer[pos++] = (byte)'~';
                    buffer[pos++] = (byte)'1';
                    break;
                case (byte)'~':
                    buffer[pos++] = (byte)'~';
                    buffer[pos++] = (byte)'0';
                    break;
                default:
                    buffer[pos++] = b;
                    break;
            }
        }

        return pos;
    }

    private static void CollectReferencedIds(
        ReadOnlySpan<byte> jsonSpan,
        ref PooledSpanBuilder<uint> ids,
        out uint maxNumericId,
        ref HashSet<string>? overflow)
    {
        maxNumericId = 0;
        var remaining = jsonSpan;

        while (true)
        {
            var idx = remaining.IndexOf(RefPattern);
            if (idx < 0)
                break;

            remaining = remaining[(idx + RefPattern.Length)..];

            var endQuote = remaining.IndexOf((byte)'"');
            if (endQuote < 0)
                break;

            var idBytes = remaining[..endQuote];
            if (TryParseNumericId(idBytes, out var numericId))
            {
                ids.Append(numericId);
                if (numericId > maxNumericId)
                {
                    maxNumericId = numericId;
                }
            }
            else
            {
                overflow ??= [];
                overflow.Add(System.Text.Encoding.UTF8.GetString(idBytes));
            }

            remaining = remaining[(endQuote + 1)..];
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

    private static bool TryReadNumericId(ref Utf8JsonReader reader, out uint id)
    {
        if (!reader.ValueIsEscaped)
        {
            return TryParseNumericId(reader.ValueSpan, out id);
        }

        id = 0;
        return false;
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