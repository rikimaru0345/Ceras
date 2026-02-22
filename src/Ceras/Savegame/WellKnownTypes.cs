using System;
using System.Collections.Generic;

namespace Ceras.Savegame
{
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
        public const int DateTime = 37;  // 8 bytes (Ticks)
        public const int TimeSpan = 38;  // 8 bytes
        public const int Guid     = 39;  // 16 bytes
        public const int Type     = 40;  // System.Type
        // 41–127 reserved for future well-known types
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

        static WellKnownTypes()
        {
            FixedSizes[Byte]     = 1;
            FixedSizes[SByte]    = 1;
            FixedSizes[Char]     = 2;
            FixedSizes[Int16]    = 2;
            FixedSizes[UInt16]   = 2;
            FixedSizes[Single]   = 4;
            FixedSizes[String]   = 4;  // string-table index
            FixedSizes[Int64]    = 8;
            FixedSizes[UInt64]   = 8;
            FixedSizes[Double]   = 8;
            FixedSizes[DateTime] = 8;
            FixedSizes[TimeSpan] = 8;
            FixedSizes[Decimal]  = 16;
            FixedSizes[Guid]     = 16;
            // Bool, Int32, UInt32, Object, Collections, Tuples: 0 (variable)

            foreach (var kv in TypeToId) IdToTypeMap[kv.Value] = kv.Key;
            foreach (var kv in GenericDefToId) IdToTypeMap[kv.Value] = kv.Key;
            foreach (var kv in TupleDefToId) IdToTypeMap[kv.Value] = kv.Key;
            IdToTypeMap[Array] = typeof(Array);
        }

        public static bool TryGetId(Type type, out int id)
        {
            if (TypeToId.TryGetValue(type, out id))
                return true;

            if (type.IsArray)
            {
                id = Array;
                return true;
            }

            if (type.IsGenericType)
            {
                var def = type.GetGenericTypeDefinition();
                if (GenericDefToId.TryGetValue(def, out id))
                    return true;
                if (TupleDefToId.TryGetValue(def, out id))
                    return true;
            }

            id = -1;
            return false;
        }

        public static bool IsWellKnown(Type type) => TryGetId(type, out _);

        public static bool TryGetType(int id, out Type type)
            => IdToTypeMap.TryGetValue(id, out type);

        /// <summary>
        /// Returns the fixed byte size for a well-known type ID, or 0 if variable.
        /// </summary>
        public static int GetFixedSize(int wellKnownId)
            => (uint)wellKnownId < (uint)FixedSizes.Length ? FixedSizes[wellKnownId] : 0;
    }
}
