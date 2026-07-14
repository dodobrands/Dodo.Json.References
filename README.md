# Dodo.Json.References

High-performance System.Text.Json reference serialization: a streaming `$id`/`$ref` → JSON Pointer
transformer plus pooled (options + reference resolver) leases for large documents.

## What it does

`ReferenceHandler.Preserve` emits opaque sequential ids:

```json
{ "$id": "1", "items": [ { "$id": "2", "name": "x" }, { "$ref": "2" } ] }
```

`JsonReferenceTransformer` rewrites them to RFC 6901 JSON Pointers — the object's real position —
and drops unreferenced `$id`s, in a two-pass streaming transform with no JsonNode DOM:

```json
{ "items": [ { "$id": "#/items/0", "name": "x" }, { "$ref": "#/items/0" } ] }
```

## Usage

```csharp
// One instance per (options, type) pair — reuse it (static field or per cache-key prefix).
private static readonly PooledReferenceSerializer<MenuModel> Serializer = new(jsonOptions);

await Serializer.SerializeWithPointers(model, httpResponse.BodyWriter, ct); // PipeWriter path
await Serializer.SerializeWithPointers(model, stream, ct);                  // Stream path

private static readonly PooledReferenceDeserializer<MenuModel> Deserializer = new(jsonOptions);
var model = await Deserializer.Deserialize(stream, ct);
```

Leases pool a warm `JsonTypeInfo<T>` graph and a payload-sized reference resolver per concurrent
operation; base options are snapshotted at construction and the first lease is built eagerly.

## Contracts

- Metadata detection is name-based (`$id`/`$ref`/`$values`); reference ids must not require JSON
  escaping; the encoder must not escape `/` or `~` in property names (default and
  `UnsafeRelaxedJsonEscaping` never do).
- `PooledReferenceHandler`/`PoolingReferenceResolver` are single-operation: reset between
  documents or use a fresh instance; the pooled serializer/deserializer enforce this via leases.

## Performance

Measured on a 6.6 MB production menu document (31,488 reference-tracked objects, Apple M4,
.NET 10) against the JsonNode-walking baseline: ~5.9× faster, ~5.7× less allocation
(per document: 104 ms / 113 MB → 17.4 ms / 19.9 MB via the PipeWriter path). Output is
byte-identical to the baseline on array-shaped graphs.
