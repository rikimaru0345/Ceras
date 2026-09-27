# Ceras robust format — layout model (Phase 3.1)

This documents the layout contract that generated writers/readers, the schema encoder, and the
migration interpreter must share. It is little-endian only; fixed-width primitives are the default.

## Envelope (see also Phase 3.3)

```
header | schema section | data section | string table
```

The header carries the format version, a layout fingerprint, the producer-build timestamp, section
offsets and a CRC (stored with its own bytes zeroed during computation). Matching fingerprints use
baked IDs and generated readers without parsing the schema; mismatches enter the interpreter.

## Type identity

Stable type names are canonical: `Namespace.Outer+Nested\`Arity` plus ordered generic arguments
(containing type arguments first), e.g. `System.Collections.Generic.List\`1<System.Int32>`.
See `TypeHelpers.GetRobustName` (generator) and `TypeHelpers.GetStableTypeName`.

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
