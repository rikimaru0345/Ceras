using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Ceras.Format
{
    /// <summary>
    /// Why a type has no describable <c>(definition, ordered arguments)</c> collection shape, as
    /// reported by <see cref="WellKnownTypes.TryResolveCollectionShape"/>.
    /// <para>
    /// This exists so the predicate can be asked about every type without throwing: the callers that act
    /// on the answer live at three different layers, and each of them reports the failure in its own
    /// terms (a bake names the declaring type and member, the migration decoder names the file's stable
    /// name), while this is the single place that decides <em>what</em> the reason is.
    /// </para>
    /// </summary>
    enum CollectionShapeRejection
    {
        /// <summary>
        /// Nothing shape-specific is wrong: the type resolved to a collection shape, or it is not a
        /// collection shape at all (not a collection, an array — described as <c>T[]</c> by its own
        /// structural schema — or null). A caller that needs a sentence for a bare
        /// <c>false</c> gets the general "not a collection shape" one from
        /// <see cref="WellKnownTypes.DescribeCollectionShapeRejection"/>.
        /// </summary>
        None,

        /// <summary>
        /// A <c>Queue&lt;&gt;</c>/<c>Stack&lt;&gt;</c> subclass. The built-in <c>QueueFormatter</c> and
        /// <c>StackFormatter</c> construct the exact built-in type, so they lose the subclass, while
        /// <c>CollectionFormatter</c> needs an <c>Add</c> method that <c>Queue</c>/<c>Stack</c> do not
        /// have — so no Ceras formatter can round-trip the shape at all. Ceras refuses it loudly rather
        /// than describing a payload it cannot write or read.
        /// </summary>
        UnsupportedQueueOrStack,

        /// <summary>
        /// A collection the built-in collection formatter does serve, but whose single element/key/value
        /// type cannot be inferred, so no definition plus ordered arguments can describe its payload.
        /// </summary>
        UninferrableElementType,

        /// <summary>
        /// A collection shape the project owner decided Ceras does not support: <c>ReadOnlyCollection&lt;T&gt;</c> /
        /// <c>ReadOnlyDictionary&lt;K,V&gt;</c> (the built-in types and every subclass) and every subclass of
        /// <c>List&lt;T&gt;</c> / <c>Dictionary&lt;K,V&gt;</c>. The exact built-in <c>List&lt;T&gt;</c> and
        /// <c>Dictionary&lt;K,V&gt;</c> are <b>not</b> this rejection, and neither is a subclass of any other
        /// collection.
        /// <para>
        /// These shapes have no describable construction: the generated collection formatter allocates
        /// without arguments, so a subclass that needs arguments comes back as the base type (or not at
        /// all), and a <c>ReadOnlyCollection&lt;T&gt;</c> has no <c>Add</c> to be refilled through. Refusing
        /// them loudly everywhere beats promising a shape that only sometimes round-trips.
        /// </para>
        /// <para>
        /// The classifier itself is <see cref="UnsupportedCollectionPolicy.IsUnsupported"/>, and this value
        /// only records that the shape question was asked and answered. The check therefore runs
        /// <em>before</em> the <c>IDictionary</c>/<c>ICollection</c> branches below, which would otherwise
        /// describe all four families as an ordinary <c>List&lt;&gt;</c>/<c>Dictionary&lt;,&gt;</c> payload.
        /// </para>
        /// </summary>
        UnsupportedCollectionPolicy,
    }

    /// <summary>
    /// Type IDs 0–127 are permanently reserved for well-known .NET types.
    /// These IDs never appear in the schema section — both writer and reader
    /// know them at compile time via this class.
    /// User-defined types start at <see cref="UserTypeBase"/> (128).
    /// </summary>
    static class WellKnownTypes
    {
        // ── Primitives ────────────────────────────────────────────────────────
        public const int Null     = 0;
        public const int Bool     = 1;   // 1 byte
        public const int Byte     = 2;   // 1 byte
        public const int SByte    = 3;   // 1 byte
        public const int Char     = 4;   // 2 bytes
        public const int Int16    = 5;   // 2 bytes
        public const int UInt16   = 6;   // 2 bytes
        public const int Int32    = 7;   // 4 bytes
        public const int UInt32   = 8;   // 4 bytes
        public const int Single   = 9;   // 4 bytes  (float)
        public const int Int64    = 10;  // 8 bytes
        public const int UInt64   = 11;  // 8 bytes
        public const int Double   = 12;  // 8 bytes
        public const int Decimal  = 13;  // 16 bytes
        // ── Special ───────────────────────────────────────────────────────────
        public const int String   = 14;  // 4-byte string-table index (always fixed)
        public const int Object   = 15;  // polymorphic slot (type-ID + data)
        // ── Collections ───────────────────────────────────────────────────────
        public const int Array      = 16;  // T[]
        public const int List       = 17;  // List<T>
        public const int Dictionary = 18;  // Dictionary<K,V>
        public const int HashSet    = 19;  // HashSet<T>
        public const int Queue      = 20;  // Queue<T>
        public const int Stack      = 21;  // Stack<T>
        public const int Nullable   = 22;  // Nullable<T>
        // ── Tuple / ValueTuple ────────────────────────────────────────────────
        public const int Tuple1  = 23;
        public const int Tuple2  = 24;
        public const int Tuple3  = 25;
        public const int Tuple4  = 26;
        public const int Tuple5  = 27;
        public const int Tuple6  = 28;
        public const int Tuple7  = 29;
        public const int VTuple1 = 30;
        public const int VTuple2 = 31;
        public const int VTuple3 = 32;
        public const int VTuple4 = 33;
        public const int VTuple5 = 34;
        public const int VTuple6 = 35;
        public const int VTuple7 = 36;
        // ── BCL structs ───────────────────────────────────────────────────────
        public const int DateTime = 37;  // 8 bytes (DateTime.ToBinary: ticks + kind)
        public const int TimeSpan = 38;  // 8 bytes
        public const int Guid     = 39;  // 16 bytes
        public const int Type     = 40;  // System.Type
        public const int KeyValuePair = 41; // System.Collections.Generic.KeyValuePair<K,V>
        public const int DateTimeOffset = 42; // 10 bytes (ticks + offset minutes)
        // 43–127 reserved for future well-known types
        public const int UserTypeBase = 128;

        // ── Fixed byte sizes (0 = variable or not applicable) ─────────────────
        static readonly int[] FixedSizes = new int[128];

        static readonly Dictionary<Type, int> TypeToId = new()
        {
            [typeof(bool)]     = Bool,
            [typeof(byte)]     = Byte,
            [typeof(sbyte)]    = SByte,
            [typeof(char)]     = Char,
            [typeof(short)]    = Int16,
            [typeof(ushort)]   = UInt16,
            [typeof(int)]      = Int32,
            [typeof(uint)]     = UInt32,
            [typeof(float)]    = Single,
            [typeof(long)]     = Int64,
            [typeof(ulong)]    = UInt64,
            [typeof(double)]   = Double,
            [typeof(decimal)]  = Decimal,
            [typeof(string)]   = String,
            [typeof(object)]   = Object,
            [typeof(DateTime)] = DateTime,
            [typeof(DateTimeOffset)] = DateTimeOffset,
            [typeof(TimeSpan)] = TimeSpan,
            [typeof(Guid)]     = Guid,
            [typeof(Type)]     = Type,
        };

        static readonly Dictionary<Type, int> GenericDefToId = new()
        {
            [typeof(List<>)]        = List,
            [typeof(Dictionary<,>)] = Dictionary,
            [typeof(HashSet<>)]     = HashSet,
            [typeof(Queue<>)]       = Queue,
            [typeof(Stack<>)]       = Stack,
            [typeof(Nullable<>)]    = Nullable,
            [typeof(KeyValuePair<,>)] = KeyValuePair,
        };

        static readonly Dictionary<Type, int> TupleDefToId = new()
        {
            [typeof(Tuple<>)]                        = Tuple1,
            [typeof(Tuple<,>)]                       = Tuple2,
            [typeof(Tuple<,,>)]                      = Tuple3,
            [typeof(Tuple<,,,>)]                     = Tuple4,
            [typeof(Tuple<,,,,>)]                    = Tuple5,
            [typeof(Tuple<,,,,,>)]                   = Tuple6,
            [typeof(Tuple<,,,,,,>)]                  = Tuple7,
            [typeof(ValueTuple<>)]                   = VTuple1,
            [typeof(ValueTuple<,>)]                  = VTuple2,
            [typeof(ValueTuple<,,>)]                 = VTuple3,
            [typeof(ValueTuple<,,,>)]                = VTuple4,
            [typeof(ValueTuple<,,,,>)]               = VTuple5,
            [typeof(ValueTuple<,,,,,>)]              = VTuple6,
            [typeof(ValueTuple<,,,,,,>)]             = VTuple7,
        };

        static readonly Dictionary<int, Type> IdToTypeMap = new();

        // The same table read the other way: the canonical name a reserved id stands for. Built from
        // IdToTypeMap rather than written out, so a reserved id and its name cannot be added, renamed or
        // dropped on one side only. Only ids below UserTypeBase are in it, which is what
        // TryGetIdByCanonicalName promises: a *closed* name such as
        // "System.Collections.Generic.List`1<System.Int32>" is a manifest entry of its own, not a
        // reserved name, and resolves to nothing here.
        static readonly Dictionary<string, int> CanonicalNameToIdMap = new(StringComparer.Ordinal);

        static WellKnownTypes()
        {
            FixedSizes[Byte]     = 1;
            FixedSizes[SByte]    = 1;
            FixedSizes[Char]     = 2;
            FixedSizes[Int16]    = 2;
            FixedSizes[UInt16]   = 2;
            FixedSizes[Single]   = 4;
            FixedSizes[Int32]    = 4;  // robust writes fixed-width little-endian, never the standard zigzag varint
            FixedSizes[UInt32]   = 4;
            FixedSizes[String]   = 4;  // string-table index, also a fixed 4-byte payload field
            FixedSizes[Int64]    = 8;
            FixedSizes[UInt64]   = 8;
            FixedSizes[Double]   = 8;
            FixedSizes[DateTime] = 8;
            FixedSizes[TimeSpan] = 8;
            FixedSizes[DateTimeOffset] = 10; // 8-byte ticks + 2-byte offset minutes
            FixedSizes[Decimal]  = 16;
            FixedSizes[Guid]     = 16;
            // Bool, Object, Collections, Tuples: 0 (variable). Bool is one byte but is skipped by the
            // switch in SchemaTraversalEngine.SkipValue, which covers every variable shape as well.

            foreach (var kv in TypeToId) IdToTypeMap[kv.Value] = kv.Key;
            foreach (var kv in GenericDefToId) IdToTypeMap[kv.Value] = kv.Key;
            foreach (var kv in TupleDefToId) IdToTypeMap[kv.Value] = kv.Key;
            IdToTypeMap[Array] = typeof(Array);

            foreach (var kv in IdToTypeMap)
                CanonicalNameToIdMap[TypeIdentity.GetCanonicalName(kv.Value)] = kv.Key;
        }

        public static bool TryGetId(Type type, out int id)
        {
            if (TypeToId.TryGetValue(type, out id))
                return true;

            if (type == typeof(Array))
            {
                id = Array;
                return true;
            }

            if (type.IsGenericType && type.IsGenericTypeDefinition)
            {
                if (GenericDefToId.TryGetValue(type, out id))
                    return true;
                if (TupleDefToId.TryGetValue(type, out id))
                    return true;
            }

            id = -1;
            return false;
        }

        public static bool IsWellKnown(Type type) => TryGetId(type, out _);

        /// <summary>
        /// True for the built-in collection generic definitions whose element shape is encoded
        /// explicitly (definition + ordered arguments) rather than as reflected CLR fields.
        /// This keeps BCL internals such as <c>Dictionary`2+Entry</c> out of the schema.
        /// </summary>
        public static bool IsWellKnownCollectionDefinition(Type type)
        {
            if (type == null || !type.IsGenericType) return false;
            var def = type.IsGenericTypeDefinition ? type : type.GetGenericTypeDefinition();
            return def == typeof(List<>)
                || def == typeof(Dictionary<,>)
                || def == typeof(HashSet<>)
                || def == typeof(Queue<>)
                || def == typeof(Stack<>);
        }

        /// <summary>
        /// True for BCL value types with a built-in element layout described as a definition plus
        /// ordered argument type IDs, rather than reflected CLR fields:
        /// <see cref="KeyValuePair{TKey,TValue}"/> (key then value), <see cref="Nullable{T}"/> (one
        /// presence byte, then the value when present), and the <c>Tuple</c>/<c>ValueTuple</c> families
        /// (each item in order). Their reserved definition IDs do not by themselves describe an
        /// instance's representation; the ordered argument types are what make the payload describable
        /// (they are described as definition + ordered argument ids).
        /// </summary>
        public static bool IsWellKnownStructuralValueType(Type type)
        {
            if (type == null || !type.IsGenericType) return false;
            var def = type.IsGenericTypeDefinition ? type : type.GetGenericTypeDefinition();
            return def == typeof(KeyValuePair<,>) || def == typeof(Nullable<>) || TupleDefToId.ContainsKey(def);
        }

        /// <summary>
        /// The ordered element type IDs laid out by the built-in formatter for a structural value type,
        /// or null when <paramref name="definitionId"/> is not a tuple/value-tuple. Item order is
        /// <c>Item1..ItemN</c> (and <c>Rest</c> for the 8-argument form).
        /// </summary>
        public static bool IsTupleDefinition(int definitionId)
            => definitionId >= Tuple1 && definitionId <= VTuple7;

        /// <summary>
        /// The built-in <c>CollectionFormatter&lt;TCollection, TItem&gt;</c> is selected for any concrete
        /// type that implements <c>ICollection&lt;TItem&gt;</c> / <c>IDictionary&lt;K,V&gt;</c>, plus the
        /// <c>Queue&lt;&gt;</c>/<c>Stack&lt;&gt;</c> special-cases. Such a type is written as an explicit
        /// element count followed by the elements, never as reflected CLR fields, so it must be described
        /// structurally (definition + ordered argument IDs) rather than by enumerating its members
        /// This is exactly the predicate the generated manifest already uses to skip a type
        /// (mirrors <c>MemberExplorer.IsCollectionType</c>).
        /// <para>
        /// A collection subclass whose element/key/value types cannot be inferred cannot be described and
        /// must be rejected before saving; <see cref="TryResolveCollectionShape"/> says why.
        /// </para>
        /// </summary>
        public static bool IsCollectionFormatterShape(Type type)
        {
            if (type == null || type.IsArray) return false;
            if (type.IsInterface || type.IsAbstract) return false;
            if (!typeof(System.Collections.IEnumerable).IsAssignableFrom(type)) return false;

            return FindClosedInterface(type, typeof(System.Collections.Generic.ICollection<>)) != null
                || FindClosedInterface(type, typeof(System.Collections.Generic.IDictionary<,>)) != null
                || FindBase(type, typeof(HashSet<>)) != null
                || FindBase(type, typeof(Queue<>)) != null
                || FindBase(type, typeof(Stack<>)) != null;
        }

        /// <summary>
        /// Resolves the <c>(definition, ordered argument)</c> shape used by the built-in collection
        /// formatter for <paramref name="type"/>:
        /// <list type="bullet">
        /// <item>a closed built-in collection (definition is one of the reserved
        /// <c>List</c>/<c>Dictionary</c>/... ids) keeps its own definition;</item>
        /// <item>a <c>Queue&lt;&gt;</c>/<c>Stack&lt;&gt;</c> keeps its own definition;</item>
        /// <item>every other collection shape becomes a <c>List&lt;TItem&gt;</c>/<c>Dictionary&lt;K,V&gt;</c>
        /// description (its exact collection semantics live in the element/key/value types);</item>
        /// <item>an <c>IDictionary&lt;K,V&gt;</c> is preferred over <c>ICollection&lt;T&gt;</c>, matching the
        /// generated collection formatter selection.</item>
        /// </list>
        /// The result is used both for the structural schema and by the migration interpreter, so a
        /// collection subclass round-trips instead of decoding as an empty ordinary object.
        /// <para>
        /// A shape Ceras writes but cannot describe is an <b>answer</b>, not an exception: the rejection
        /// comes back as <paramref name="rejection"/> (see <see cref="CollectionShapeRejection"/> and
        /// <see cref="DescribeCollectionShapeRejection"/>) and each caller decides how to report it, with
        /// the context only it has — the bake knows the declaring type and the member that reached it, the
        /// decoder knows the name the file used. This predicate therefore never throws, and a caller can
        /// ask it about any type without having to guard the call.
        /// </para>
        /// </summary>
        public static bool TryResolveCollectionShape(Type? type, out Type? definition, out Type[]? args, out CollectionShapeRejection rejection)
        {
            definition = null;
            args = null;
            if (type == null || type.IsArray)
            {
                // An array is described as `T[]` by its own structural schema (see SchemaRegistry), never
                // as a collection shape, so there is nothing to reject here.
                rejection = CollectionShapeRejection.None;
                return false;
            }

            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                var ownDef = type.GetGenericTypeDefinition();
                if (GenericDefToId.ContainsKey(ownDef))
                {
                    definition = ownDef;
                    args = type.GetGenericArguments();
                    rejection = CollectionShapeRejection.None;
                    return true;
                }
            }

            // The owner's collection policy, before anything that would otherwise describe these shapes.
            // It has to come first: ReadOnlyDictionary<K,V> is picked up by the IDictionary branch below
            // and ReadOnlyCollection<T> by the ICollection branch, so a check after them would never be
            // reached. The exact List<T>/Dictionary<TKey,TValue> answered above and stays supported; only a
            // derived type is refused, which the predicate decides by walking the base chain.
            if (UnsupportedCollectionPolicy.IsUnsupported(type))
            {
                rejection = CollectionShapeRejection.UnsupportedCollectionPolicy;
                return false;
            }

            var dictItf = FindClosedInterface(type, typeof(System.Collections.Generic.IDictionary<,>));
            if (dictItf != null)
            {
                definition = typeof(Dictionary<,>);
                args = dictItf.GetGenericArguments();
                rejection = CollectionShapeRejection.None;
                return true;
            }

            // Preserve the semantic set/queue/stack shape when the type derives from the built-in
            // implementation, so the interpreter refills it correctly and the schema is accurate.
            var setBase = FindBase(type, typeof(HashSet<>));
            if (setBase != null)
            {
                definition = typeof(HashSet<>);
                args = setBase.GetGenericArguments();
                rejection = CollectionShapeRejection.None;
                return true;
            }

            var collectionItf = FindClosedInterface(type, typeof(System.Collections.Generic.ICollection<>));
            if (collectionItf != null)
            {
                definition = typeof(List<>);
                args = collectionItf.GetGenericArguments();
                rejection = CollectionShapeRejection.None;
                return true;
            }

            // Queue<>/Stack<> subclasses are NOT supported: the built-in QueueFormatter/StackFormatter
            // construct the exact built-in type (losing the subclass) and CollectionFormatter needs an
            // 'Add' method that Queue/Stack do not have. A shape that cannot round-trip is reported, not
            // admitted (and not thrown from here — see the remarks above).
            if (FindBase(type, typeof(Queue<>)) != null || FindBase(type, typeof(Stack<>)) != null)
            {
                rejection = CollectionShapeRejection.UnsupportedQueueOrStack;
                return false;
            }

            // A shape the built-in collection formatter does serve, but with no single element/key/value
            // type to describe its payload with. Every remaining branch above is a *concrete* element
            // type, so reaching this point with a collection shape means the type is undescribable.
            if (IsCollectionFormatterShape(type))
            {
                rejection = CollectionShapeRejection.UninferrableElementType;
                return false;
            }

            rejection = CollectionShapeRejection.None;
            return false;
        }

        /// <summary>
        /// The actionable reason <paramref name="type"/> has no describable collection shape: what is
        /// wrong with it, and what to use instead.
        /// <para>
        /// One implementation for every fail-loud site (the bake in <c>SchemaRegistry</c>, the bake-time
        /// member resolution in <c>SchemaSection</c>, and the migration decoder in
        /// <c>SchemaTraversalEngine</c>), so the same defect cannot be described two different ways
        /// depending on which layer noticed it.
        /// </para>
        /// </summary>
        public static string DescribeCollectionShapeRejection(Type? type, CollectionShapeRejection rejection)
        {
            string name = TypeIdentity.GetCanonicalNameOrUnknown(type);
            return rejection switch
            {
                CollectionShapeRejection.UnsupportedQueueOrStack =>
                    $"The collection type '{name}' derives from Queue<>/Stack<> and is not supported by Ceras. " +
                    "Use the built-in Queue<>/Stack<> directly, or expose the queue/stack contents through an ICollection<T> member.",

                CollectionShapeRejection.UninferrableElementType =>
                    $"The collection type '{name}' is handled by the built-in collection formatter, but Ceras cannot infer a " +
                    "single element/key/value type to describe it with. Give it a concrete ICollection<T> or IDictionary<K,V> " +
                    "implementation, and load the save with the build that wrote it if one is already written.",

                // One shared sentence for the whole policy, so the four refused families cannot be
                // described four different ways depending on which layer noticed them.
                CollectionShapeRejection.UnsupportedCollectionPolicy =>
                    $"The collection type '{name}' is a shape Ceras does not support. {UnsupportedCollectionPolicy.BanReason}",

                _ =>  // None: no shape-specific defect to explain; this is the general fallback.
                    $"'{name}' is not a collection shape Ceras describes: it is not a built-in collection, a collection " +
                    "subclass, or a shape the built-in collection formatter serves.",
            };
        }

        /// <summary>Finds the closed <paramref name="openGeneric"/> in <paramref name="type"/>'s base chain (concrete classes only).</summary>
        static Type? FindBase(Type type, Type openGeneric)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                if (!t.IsGenericType || t.IsGenericTypeDefinition) continue;
                if (t.GetGenericTypeDefinition() == openGeneric) return t;
            }
            return null;
        }

        /// <summary>
        /// Finds the closed interface generic definition in <paramref name="type"/>'s interface set,
        /// walking base types only for the special-case <c>Queue&lt;&gt;</c>/<c>Stack&lt;&gt;</c> shapes
        /// (which are concrete classes, not interfaces).
        /// </summary>
        static Type? FindClosedInterface(Type? type, Type openGeneric)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                if (!t.IsGenericType || t.IsGenericTypeDefinition) continue;
                if (t.GetGenericTypeDefinition() == openGeneric) return t;
            }

            foreach (var iface in type!.GetInterfaces())
            {
                if (!iface.IsGenericType) continue;
                if (iface.GetGenericTypeDefinition() == openGeneric) return iface;
            }

            return null;
        }

        /// <summary>
        /// The type a well-known id names, when there is one. Every entry in the map is a real
        /// <see cref="Type"/>, so a true answer is never null; a miss answers null and the caller reads
        /// the out only when this returned <c>true</c>.
        /// </summary>
        public static bool TryGetType(int id, [NotNullWhen(true)] out Type? type)
        {
            // The lookup goes through a local on purpose: forwarding the dictionary's own out argument
            // into this method's out argument costs the compiler the post-state it would otherwise read
            // off the parameter's NotNullWhen, and every caller would see the type it just found as
            // maybe-null.
            bool found = IdToTypeMap.TryGetValue(id, out var mapped);
            type = mapped;
            return found;
        }

        /// <summary>
        /// The reserved type id a canonical name stands for, for the reserved id space only (ids below
        /// <see cref="UserTypeBase"/>). This is the reverse of <see cref="TryGetType"/> and shares its
        /// table, so the name and the id cannot disagree: the bake resolves a member's type name through
        /// it, and <c>SchemaRegistry</c> refuses a manifest entry that would claim one of these names.
        /// <para>
        /// Only a <em>reserved</em> name answers. A closed canonical name —
        /// <c>System.Collections.Generic.List`1&lt;System.Int32&gt;</c>, <c>System.Int32[]</c> — is an
        /// ordinary manifest entry that happens to mention a reserved definition, and a name outside the
        /// canonical form is not a well-known name at all; both resolve to nothing here.
        /// </para>
        /// </summary>
        public static bool TryGetIdByCanonicalName(string canonicalName, out int id)
        {
            if (string.IsNullOrEmpty(canonicalName))
            {
                id = -1;
                return false;
            }

            return CanonicalNameToIdMap.TryGetValue(canonicalName, out id);
        }

        /// <summary>
        /// Returns the fixed byte size for a well-known type ID, or 0 if variable.
        /// </summary>
        public static int GetFixedSize(int wellKnownId)
            => (uint)wellKnownId < (uint)FixedSizes.Length ? FixedSizes[wellKnownId] : 0;
    }
}
