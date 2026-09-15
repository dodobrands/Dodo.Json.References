# Dodo.Json.References

High-performance System.Text.Json reference serialization: a streaming `$id`/`$ref` → JSON Pointer
transformer plus pooled (options + reference resolver) leases for large documents.

## What it does

`ReferenceHandler.Preserve` emits opaque sequential ids:

```json
{ "$id": "1", "items": [ { "$id": "2", "name": "x" }, { "$ref": "2" } ] }
```

`JsonReferenceTransformer` rewrites them to **RFC 6901 JSON Pointers** in the section 6 URI-fragment
form (`#`-prefixed, percent-encoded) into the logical object graph
(`$values` wrappers are transparent) and drops unreferenced object `$id`s — collection-wrapper
`$id`s stay, STJ requires them before `$values` — in a two-pass no-DOM transform over a pooled
full-document buffer:

```json
{ "items": [ { "$id": "#/items/0", "name": "x" }, { "$ref": "#/items/0" } ] }
```

## Serializing

One instance per (base options, type) pair — a static field or a keyed cache, never per call.
Concurrent calls are safe: each checks out its own lease. The base options need no
`ReferenceHandler` — each lease swaps in its own pooling handler.

```csharp
private static readonly PooledReferenceSerializer<MenuModel> Serializer = new(MenuJsonOptions);

await Serializer.SerializeWithPointers(model, response.BodyWriter, ct); // PipeWriter overload
await Serializer.SerializeWithPointers(model, response.Body, ct);       // Stream overload
```

### Stream or PipeWriter?

- **`PipeWriter`** when the writer is the transport itself — Kestrel `Response.BodyWriter` with no
  stream-wrapping middleware. Bytes are committed straight into transport segments, one
  `FlushAsync` at the end. ASP.NET Core completes the writer; standalone `Pipe` callers must
  `Complete()` it themselves.
- **`Stream`** when anything wraps the output — response compression, encryption, files, blob
  storage. The package wraps it in `PipeWriter.Create(minimumBufferSize: 64 KB, leaveOpen: true)`:
  pooled 64 KB segments, async-only writes, the internal wrapper is completed even on exception,
  and the caller keeps stream ownership.

The trap (production incident): with response compression enabled, `Response.BodyWriter` is
ASP.NET's `StreamPipeWriter` over the compression stream with default **4096-byte** segments — a
multi-MB document rents a 4 KB array per segment, holds all of them until the single end-flush,
then burst-returns ~1500 at once. `ArrayPool<byte>.Shared` buckets can't absorb that:
pool-exhausted allocations and dropped returns, all `len:4096`, under load. One word fixes it:

```csharp
await Serializer.SerializeWithPointers(model, response.BodyWriter, ct); // only without wrapping middleware
await Serializer.SerializeWithPointers(model, response.Body, ct);       // compression on: 64 KB wrap applies
```

Never mix `Body` and `BodyWriter` in one request. `MemoryStream` benchmarks can't reproduce the
churn — they take the Stream overload; verify against the real middleware pipeline.

## Deserializing

```csharp
private static readonly PooledReferenceDeserializer<MenuModel> Deserializer = new(MenuJsonOptions);

MenuModel? model = await Deserializer.Deserialize(stream, ct);
```

Ids match as opaque strings: native `$id`/`$ref` and pointer-rewritten output both read back, as
long as every `$ref` follows its `$id`. The input stream is not disposed — the caller owns it.

## Pinning a context or per-lease converters

Both pooled types take a type-info factory. It runs once per lease and receives the lease's options
with the pooling handler already installed — the place to attach converters that must share the
operation's reference resolver, or to pin a source-generated context:

```csharp
private static readonly PooledReferenceDeserializer<MenuView> Deserializer = new(
    BaseOptions,
    options => BuildTypeInfo(options), // e.g. converters resolving against the lease's resolver
    maxRetained: 4);
```

## Sizing `maxRetained`

Default: `ObjectPoolFactory.DefaultMaxRetained` = `clamp(2 × cores, 16, 64)`. `DefaultObjectPool`
never trims and retention only grows to observed concurrency — but each retained lease pins a warm
`JsonTypeInfo<T>` graph plus resolver maps whose high-water capacity survives `Reset()`, and a pool
miss builds a fresh lease (full type-info graph resolution — the pre-pooling per-call cost).

- One hot instance (an HTTP endpoint): keep the default.
- A call site with configured parallelism (Kafka consumer `DegreeOfParallelism`, a semaphore
  width): match it exactly.
- Many instances (per country/culture/tenant): cap explicitly and tier by traffic — a parallel
  warmup wider than the caps grows *every* pool to its cap, and the cap is then the permanent
  footprint (instances × cap × lease size):

```csharp
maxRetained: country switch
{
    CountryCode.ABC => 16,                   // dominant traffic
    CountryCode.DEF or CountryCode.XYZ => 4,
    _ => 2                                  // misses just rebuild per call
}
```

## One-off use without pooling

`JsonReferenceTransformer` is public. Options must use `ReferenceHandler.Preserve` (or any handler
emitting the same `$id`/`$ref`/`$values` shape) — the pooled types install their own, nothing does
it for you here:

```csharp
await JsonReferenceTransformer.SerializeWithPointers(model, output, preserveOptions, ct);
await JsonReferenceTransformer.SerializeWithPointers(model, output, jsonTypeInfo, ct); // trim/AOT-safe
```

The options overloads carry `RequiresUnreferencedCode`/`RequiresDynamicCode`; the `JsonTypeInfo<T>`
overloads are the trim/Native AOT path. Same Stream/PipeWriter overloads and rules as above.
`PooledReferenceHandler` + `PoolingReferenceResolver` are the building blocks for custom pooling;
the pair is single-operation — `PoolingReferenceResolver.Reset()` between documents.

## Contracts

- Pointers follow RFC 6901 section 6: section 3 escaping (`~` → `~0`, `/` → `~1`), then RFC 3986
  percent-encoding of every byte the fragment rule disallows, over the UTF-8 octets of the decoded
  property name. ASCII names built from unreserved and sub-delimiter characters pass through as-is.
- Metadata detection is name-based (`$id`/`$ref`/`$values`). Ids that require JSON escaping are
  decoded before matching, so `"1"` and `"\u0031"` name the same object; escaped non-numeric ids
  fall off the dense fast path onto a slower dictionary lookup.
- Input must be `ReferenceHandler.Preserve`-shaped: a `$ref` never precedes its `$id` (STJ always
  writes them in that order). Forward references keep their original id string, untransformed.
- Base options are snapshotted at construction — later mutations are not observed; the first lease
  is built eagerly (fail-fast).
- `PooledReferenceHandler`/`PoolingReferenceResolver` are single-operation: reset between
  documents or use a fresh instance; the pooled serializer/deserializer enforce this via leases.

## Performance

Measured externally (harness not in this repo) on a 6.6 MB production menu document
(31,488 reference-tracked objects, Apple M4, .NET 10) against the JsonNode-walking baseline: ~5.9× faster, ~5.7× less allocation
(per document: 104 ms / 113 MB → 17.4 ms / 19.9 MB via the PipeWriter path). Output is
byte-identical to the baseline on array-shaped graphs.
