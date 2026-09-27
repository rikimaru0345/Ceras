# Ceras robust format — layout model (Phase 3.1)

This documents the layout contract that generated writers/readers, the schema encoder, and the
migration interpreter must share. It is little-endian only; fixed-width primitives are the default.

## Envelope (two-region format, version 3)

```
header | schema bytes | schema string table | data section | payload string table
```

The header is a fixed 56-byte little-endian record written by one explicit codec
(`RobustHeader`):

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `CERA` |
| 4 | 1 | format version (currently 3) |
| 5 | 1 | payload string-table compression flags |
| 6 | 2 | reserved |
| 8 | 4 | game/app version (`robustVersion`) |
| 12 | 8 | producer build timestamp (UTC ticks; 0 = unknown) |
| 20 | 8 | schema string table offset (end of schema bytes) |
| 28 | 8 | data section offset |
| 36 | 8 | payload string table offset |
| 44 | 8 | deterministic layout fingerprint |
| 52 | 4 | CRC32 (computed with these 4 bytes zeroed) |

- The schema (bytes + its own string table) is **baked once** when the declared set is sealed, in
  deterministic order by type ID, and is reused by every `Save`. There is no save-time registry
  growth or discovery walk.
- Schema strings and payload strings live in **separate** regions and use independent indices, so
  the happy path can skip schema interpretation without decoding any metadata names.
- The layout fingerprint is deterministic over the declared layout and is independent of the
  producer build timestamp. Loading rejects a save whose build timestamp is newer than the current
  build (when the current build timestamp is known) before decoding the payload.


## Type identity

Stable type names are canonical: `Namespace.Outer+Nested\`Arity` plus ordered generic arguments
(containing type arguments first), e.g. `System.Collections.Generic.List\`1<System.Int32>`.
Arrays are `T[]`/`T[,]` (rank preserved), pointers are `T*`, and generic type parameters are
`!ordinal`. Names never contain assembly versions, culture, or public key tokens, so the same
logical layout fingerprints identically across .NET runtimes.

There is exactly one runtime identity helper, `Ceras.Format.TypeIdentity.GetCanonicalName`, and it
must match the generator's `Ceras.SourceGenerator.TypeHelpers.GetRobustName` exactly; the generator
emits these canonical names for field types and enum underlying types (never `Type.FullName`).
Structural/declared runtime schemas (`SchemaRegistry.TryBuildStructuralSchema`,
`BuildDeclaredSchema`) use the same helper. `SchemaRegistry.TryResolveType` parses canonical names
back to CLR types on cold paths; the remaining assembly scan is a last resort for genuinely
external/legacy unqualified `Type.FullName` names.

## Member layout

- Field order is deterministic: `MemberType + MemberName + DeclaringType + "f"/"p"` (ordinal).
- Field identity is the stable wire name (`[CerasStableName]` overrides the CLR name; previous names
  are aliases used only by migration).
- Members are written in the deterministic order; the schema records the order explicitly.

## Primitive encodings (fixed-width default)

| Type | Bytes | Encoding |
|---|---|---|
| bool | 1 | 0/1 |
| byte/sbyte | 1 | raw |
| char/int16/uint16 | 2 | little-endian |
| int32/uint32 | 4 | little-endian |
| int64/uint64 | 8 | little-endian |
| float/double | 4/8 | IEEE-754 little-endian |
| decimal | 16 | .NET decimal bits |
| DateTime/TimeSpan | 8 | ticks |
| Guid | 16 | raw |
| string | 4 + table | 4-byte string-table index |

Compact encodings (e.g. zigzag varints) require an explicit override reflected in the
layout/fingerprint; raw primitive encoding never persists CLR struct padding.

## References, null, and polymorphism

Reference/polymorphism markers are defined once in `Format/Protocol.cs`
(`RefNull=-1`, `RefNewValue=-2`, `RefNewValueSameType=-3`, `RefInlineType=-5`, bias `+5`).
A marker is written with the bias so non-negative values are object-cache IDs.

## Collections and arrays

Built-in collections (`List<>`, `Dictionary<,>`, `HashSet<>`, `Queue<>`, `Stack<>`, arrays) are
encoded **structurally**: a well-known definition ID plus ordered argument type IDs, not by
reflecting CLR internals. Counts use bias `+1`; `-1` marks null. This keeps implementation details
such as `Dictionary\`2+Entry` out of the schema (`WellKnownTypes.IsWellKnownCollectionDefinition`).

## Schema types

`SchemaTypeInfo` kinds: `Enum`, `Struct`, `Class`, `GenericInstantiation`. `GenericInstantiation`
records the definition type (`UnderlyingTypeName`) and ordered argument types (`ArgTypeNames`), which
lets the interpreter describe value/reference representation without requiring the old CLR type to
exist. Custom formatters own their bytes and are never described as an ordinary generated member
schema.

## Remaining (Phase 3.1)

- Explicit compact-encoding overrides and an explicit protocol-header spec.
- Deriving generated writers/readers and the schema encoder from a single layout IR.
- Custom-formatter context isolation guarantees.
