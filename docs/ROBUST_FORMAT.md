# Ceras Robust format Format — Design Specification

## Goals

- **Fast path**: zero schema overhead at runtime when schema matches (AOT-generated formatters, direct field access, no boxing, no reflection)
- **Full version tolerance**: add, remove, or rename fields and types across game versions without keeping dead code
- **AOT-safe migration**: the slow path (schema mismatch) uses only schema traversal + reflection — no `Expression.Compile`
- **One-time migration cost**: on first load after an update, migrate in memory and re-save; all subsequent loads hit the fast path
- **Self-describing schema**: the schema section alone is sufficient to skip any value, even for types deleted from the codebase

---

## File Layout

```
[Header]          fixed size, always at offset 0
[Schema section]  follows header immediately
[Data section]    offset stored in header
[String table]    offset stored in header (at end of file)
```

### Header (fixed 38 bytes)

| Field | Size | Notes |
|---|---|---|
| Magic | 4 bytes | `CERA` |
| Format version | 1 byte | bumped on breaking format changes |
| Game version | 4 bytes | uint32; reject load if save > current |
| Schema fingerprint | 8 bytes | xxHash64 of all current type schemas |
| Flags | 1 byte | bit 0: has-checksum |
| Data section offset | 8 bytes | absolute byte offset |
| String table offset | 8 bytes | absolute byte offset |
| Checksum | 4 bytes | CRC32 of entire file (this field = 0 during hash) |

---

## Type ID System

Type IDs are written as **LEB128 unsigned varints** throughout the file.

### Well-Known Type IDs (0–127, never appear in schema section)

```
0   null / void
1   bool        1 byte
2   byte        1 byte
3   sbyte       1 byte
4   char        2 bytes
5   short       2 bytes
6   ushort      2 bytes
7   int         4 bytes
8   uint        4 bytes
9   float       4 bytes
10  long        8 bytes
11  ulong       8 bytes
12  double      8 bytes
13  decimal     16 bytes
14  string      4-byte string-table index (fixed width, always)
15  object      polymorphic slot (type-ID + data)
16  T[]
17  List<T>
18  Dictionary<K,V>
19  HashSet<T>
20  Queue<T>
21  Stack<T>
22  Nullable<T>
23–29  Tuple<T1>..Tuple<T1..T7>
30–36  ValueTuple<T1>..ValueTuple<T1..T7>
37  DateTime    8 bytes (Ticks)
38  TimeSpan    8 bytes
39  Guid        16 bytes
40–127  reserved
```

Generic well-known types (16–36) are followed in the schema by their type argument IDs.

### User-Defined Type IDs (128+)

Assigned sequentially during schema-section construction. All user types have an entry in the schema section.

---

## Schema Section

Written once per save file. Describes every user-defined type encountered during serialization.

```
type_count: LEB128

for each type:
  type_id:          LEB128
  stable_name:      inline string (UTF-8 length-prefixed, NOT from string table)
  previous_names:   LEB128 count + inline strings
  kind:             1 byte  (0=enum  1=struct  2=class  3=generic-instantiation)

  if kind == enum:
    underlying_type_id: LEB128         (must be one of the int well-known IDs)
    value_count:        LEB128
    for each value:
      name:   inline string
      value:  int64 (fixed 8 bytes)

  if kind == struct or class:
    field_count:    LEB128
    for each field:
      stable_name:     inline string
      previous_names:  LEB128 count + inline strings
      type_id:         LEB128
      flags:           1 byte
                         bit 0: is_monomorphic
                                (no type-ID per element/value; element type is exact)
    fixed_byte_size:  LEB128   (0 = variable; >0 = exact size, computed recursively)

  if kind == generic-instantiation:
    definition_type_id:    LEB128
    arg_count:             LEB128
    arg_type_ids:          LEB128[]
```

**Fixed-size rule**: a type's `fixed_byte_size` is non-zero iff all fields recursively are:
primitives, enums, fixed-size structs, or strings (which are always 4 bytes in data).
Classes are always variable (reference marker + possible null).

**Inline strings** in the schema section are length-prefixed UTF-8, independent of the string table. The schema section is a one-time parse during migration; string-table lookup overhead is irrelevant there.

---

## String Table

Located at the offset given in the header (end of file).

```
string_count: LEB128
for each string:
  byte_length: LEB128
  utf8_bytes:  byte[byte_length]
```

During deserialization: load the entire string table into `string[]` before reading the data section. String references in the data section are **4-byte fixed-width indices** into this array.

During serialization: maintain `Dictionary<string, int>` (intern table). Write 4-byte indices inline. Flush string table at end.

---

## Data Section

The data section is a single binary stream produced by the generated formatters (fast path) or the schema traversal engine (slow path). Both paths write **identical bytes** — speed difference is compiled code vs interpreted schema walking.

### Encoding Rules

| Value type | Encoding |
|---|---|
| Primitive (int, float, …) | Raw fixed-width bytes |
| Enum | Raw bytes of underlying type |
| String | 4-byte string-table index |
| Fixed-size struct | Fields concatenated, no markers |
| Nullable\<T\> | 1-byte present flag + optional T |
| Reference type (monomorphic field) | Marker byte + data (see below) |
| Reference type (polymorphic field) | Marker byte + type-ID + data |
| Collection (monomorphic elements) | LEB128 count + element data × count |
| Collection (polymorphic elements) | LEB128 count + (type-ID + element data) × count |

### Reference Marker Byte

```
0x00  null
0x01  existing object — followed by LEB128 object-ID
0x02  new object     — followed by data (and type-ID if polymorphic field/slot)
```

### Monomorphic vs Polymorphic

Set per-field and per-collection in the schema section (`is_monomorphic` flag).

**Auto-detected as monomorphic**: value types, sealed classes with no registered subtypes, well-known-type containers with value-type elements.
**Auto-detected as polymorphic**: abstract classes, interfaces, non-sealed classes with registered subtypes.
**Override**: `[Monomorphic]` / `[Polymorphic]` attributes on the field.

Monomorphic removes all per-element type-ID writes. For monomorphic + fixed-size elements: an entire `List<Vector3>` skips in O(1) (read count, skip count×12 bytes) — including during migration for deleted types.

---

## Fast Path

Activated when the schema fingerprint in the header matches the fingerprint computed from current type schemas at startup.

1. Parse header
2. Load string table into `string[]`
3. Skip schema section (offset known from header)
4. Read data section using generated formatters directly

Generated formatters:
- Are registered via `[CerasSerializable]` and `RobustFormatters.UseRobustFormatters(config)`
- Write/read fields in `SchemaMemberComparer` order (matches `DynamicFormatter` for compatibility)
- Have no schema overhead — direct field access, injected sub-formatters, no boxing

---

## Slow Path (Migration)

Activated when fingerprint does not match.

1. Parse header; check game version (reject if save > current, emit "update required" error)
2. Load string table
3. Parse schema section fully into an in-memory type map
4. For each type: diff old schema fields vs current type's fields → build migration plan:
   - Field name in old schema maps to current field (direct or via `[PreviousName]`)
   - Field in old schema, absent in current → **skip** (schema traversal, see below)
   - Field in current, absent in old → **default value**
   - Field present in both, type changed → **attempt coercion** (numeric widening etc.), else default
5. Read data section using schema traversal engine (reflection-based, AOT-safe)
6. Fire `[OnAfterDeserialize]` with `MigrationContext` for developer fixup
7. Caller re-saves in new format; next load hits fast path

### Schema Traversal (Skipping Unknown Fields)

To skip a value of a type no longer in the codebase, walk the old schema entry recursively:
- Fixed-size type: `offset += fixed_byte_size`
- String: `offset += 3`
- Collection: read LEB128 count; iterate, skip each element recursively
- Reference type: read marker; if `0x01` skip LEB128 object-ID; if `0x02` skip fields recursively via schema

No `Type.GetType`, no `Expression.Compile`. Pure schema arithmetic.

---

## Naming & Attributes

```csharp
// Stable serialized name (independent of C# class name; can rename class freely)
[CerasSerializable("Player")]
public class PlayerEntity { ... }

// Reactive rename (type was renamed after shipping)
[CerasSerializable]
[PreviousTypeName("MyGame.V1.PlayerInventory")]
public class Inventory { ... }

// Field-level stable name + previous names
public class Player {
    [SerializedAs("HitPoints")]          // stable wire name
    [PreviousName("MaxHp", "HP")]        // old names in older saves
    public int HitPoints;

    [Monomorphic]                        // override auto-detection
    public List<Item> Items;

    [Exclude]                            // never serialized
    public float CachedSpeed;
}
```

Default stable name (no argument to `[CerasSerializable]`): simple class name without namespace.

---

## Migration Hook

```csharp
public class Player {
    public Wallet Wallet;

    [OnAfterDeserialize]
    void OnLoaded(MigrationContext ctx) {
        if (!ctx.WasMigrated) return;
        // ctx.SkippedFields: fields in old save with no current mapping
        // ctx.AddedFields:   fields defaulted because absent in old save
        // ctx.OldGameVersion: uint32 from file header
        if (Wallet == null)
            Wallet = new Wallet { Gold = LegacyGold };
    }
}
```

`MigrationContext` is only non-trivial during migration; `WasMigrated == false` on fast path.

---

## Enum Strategy

Enums are serialized by **integer value** (compact, fast). The schema section embeds the full name→value mapping. On migration, value-to-name resolution uses the old schema's embedded mapping — inconsistencies (removed values, renumbering) are detectable and reported via `MigrationContext.SkippedFields`. Recommendation: only ever append new enum values; never remove or renumber.

---

## Schema Fingerprint

Computed at application startup: xxHash64 over the canonical serialization of all `[CerasSerializable]` type schemas in stable order (sorted by stable name). Written to file header on save. Compared on load — mismatch triggers slow path. Fingerprint covers field names, types, order, and all `[PreviousName]` entries.
