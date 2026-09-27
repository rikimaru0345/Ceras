// The identity half of the polymorphic type-value decoder, pinned directly rather than through a save.
//
// A nested described type value is interned as a schema entry, and that entry *is* the engine's identity
// for the payload — so two type values that decode to the same structure must resolve to the same entry,
// and two that differ must not. It is also what makes the retained cost of a save proportional to the
// number of distinct nested shapes rather than to the number of values that mention them.
//
// A save-level test cannot show either directly: repeated values look identical whether or not they
// shared an entry, and the only observable symptom of not sharing is a cap being hit, which is pinned in
// HistoricalPolymorphicFixtureTests. This file asks the decoder itself.
using System;
using System.Buffers;
using System.Collections.Generic;
using Xunit;
using Ceras.Format;

namespace Ceras.Test.TypeValueInterning
{
    public class TypeValueInterningTests
    {
        /// <summary>
        /// A decoder over an empty schema section, so every terminal manifest id is unknown and the only
        /// entries that can exist are the synthesized ones. Nothing here needs a real file.
        /// </summary>
        static TypeValueShapes Decoder() => new TypeValueShapes(new FileSchemaView(new Dictionary<string, OldTypeSchema>()), _ => null);

        /// <summary>Decodes one type value written as the writer writes it, and returns its payload entry.</summary>
        static OldTypeSchema Intern(Action<IBufferWriter<byte>> write, TypeValueShapes decoder = null)
        {
            decoder ??= Decoder();
            var bytes = new ArrayBufferWriter<byte>();
            write(bytes);
            var sequence = new ReadOnlySequence<byte>(bytes.WrittenMemory);
            var reader = new SequenceReader<byte>(sequence);
            int tag = SchemaSection.ReadTypeIdLeb128(ref reader, "the test's tag");
            return decoder.InternRoot(decoder.Decode(tag, ref reader));
        }

        /// <summary>Writes the type value for a closed built-in generic, as RobustTypeFormatter does.</summary>
        static void WriteGeneric(IBufferWriter<byte> w, uint definition, params uint[] arguments)
        {
            SchemaSection.WriteLeb128(w, definition);
            SchemaSection.WriteLeb128(w, (uint)arguments.Length);
            foreach (var argument in arguments) SchemaSection.WriteLeb128(w, argument);
        }

        /// <summary>
        /// The type value of a closed built-in generic whose single argument is itself a whole nested type
        /// value, which is what makes the grammar recursive. Written through a nested buffer so the bytes
        /// are exactly the writer's, in order.
        /// </summary>
        static void WriteGenericOfNested(IBufferWriter<byte> w, uint definition, Action<IBufferWriter<byte>> argument)
        {
            var nested = new ArrayBufferWriter<byte>();
            argument(nested);
            SchemaSection.WriteLeb128(w, definition);
            SchemaSection.WriteLeb128(w, 1);
            w.Write(nested.WrittenSpan);
        }

        /// <summary>The type value of an array: its tag, then the element type as a whole type value.</summary>
        static void WriteArrayOf(IBufferWriter<byte> w, Action<IBufferWriter<byte>> element)
        {
            SchemaSection.WriteLeb128(w, WellKnownTypes.Array);
            element(w);
        }

        [Fact]
        public void Two_Equal_Type_Values_Share_One_Entry()
        {
            var decoder = Decoder();

            var first = Intern(w => WriteGeneric(w, WellKnownTypes.List, WellKnownTypes.Int32), decoder);
            var second = Intern(w => WriteGeneric(w, WellKnownTypes.List, WellKnownTypes.Int32), decoder);

            Assert.Equal(first.TypeId, second.TypeId);
            Assert.Equal(1, decoder.InternedCount);
        }

        [Fact]
        public void A_Repeated_Value_Costs_No_Further_Identity_However_Often_It_Appears()
        {
            // The property the per-load bound depends on: occurrences are free, distinct shapes are not.
            var decoder = Decoder();
            for (int i = 0; i < 5_000; i++) Intern(w => WriteGeneric(w, WellKnownTypes.List, WellKnownTypes.Int32), decoder);

            Assert.Equal(1, decoder.InternedCount);
        }

        [Fact]
        public void A_Different_Argument_Is_A_Different_Entry()
        {
            var decoder = Decoder();

            var ints = Intern(w => WriteGeneric(w, WellKnownTypes.List, WellKnownTypes.Int32), decoder);
            var strings = Intern(w => WriteGeneric(w, WellKnownTypes.List, WellKnownTypes.String), decoder);

            Assert.NotEqual(ints.TypeId, strings.TypeId);
            Assert.Equal(2, decoder.InternedCount);
        }

        [Fact]
        public void A_Different_Definition_Is_A_Different_Entry()
        {
            var decoder = Decoder();

            var list = Intern(w => WriteGeneric(w, WellKnownTypes.List, WellKnownTypes.Int32), decoder);
            var hashSet = Intern(w => WriteGeneric(w, WellKnownTypes.HashSet, WellKnownTypes.Int32), decoder);

            Assert.NotEqual(list.TypeId, hashSet.TypeId);
        }

        [Fact]
        public void Nesting_Is_Part_Of_The_Identity_At_Every_Level()
        {
            // `List<List<int>>` and `List<List<string>>` differ only at the second level, so the key has to
            // be recursive: a key built from the outer node alone would give them the same entry, and the
            // inner payload would then be read with the wrong element type.
            var decoder = Decoder();

            var nestedInts = Intern(w => WriteGenericOfNested(w, WellKnownTypes.List,
                e => WriteGeneric(e, WellKnownTypes.List, WellKnownTypes.Int32)), decoder);
            var nestedStrings = Intern(w => WriteGenericOfNested(w, WellKnownTypes.List,
                e => WriteGeneric(e, WellKnownTypes.List, WellKnownTypes.String)), decoder);
            var again = Intern(w => WriteGenericOfNested(w, WellKnownTypes.List,
                e => WriteGeneric(e, WellKnownTypes.List, WellKnownTypes.Int32)), decoder);

            Assert.NotEqual(nestedInts.TypeId, nestedStrings.TypeId);
            Assert.Equal(nestedInts.TypeId, again.TypeId);
            Assert.Equal(again.ArgTypeIds, nestedInts.ArgTypeIds);

            // Two entries per nested shape, the outer node and the argument it holds, and the third lookup
            // found both of the first's rather than making a third and a fourth.
            Assert.Equal(4, decoder.InternedCount);
        }

        [Fact]
        public void Argument_Order_Is_Part_Of_The_Identity()
        {
            // `Dictionary<K,V>` and `Dictionary<V,K>` are the same definition with the arguments swapped,
            // so key and value have to be told apart by position.
            var decoder = Decoder();

            var keyFirst = Intern(w => WriteGeneric(w, WellKnownTypes.Dictionary,
                WellKnownTypes.String, WellKnownTypes.Int32), decoder);
            var valueFirst = Intern(w => WriteGeneric(w, WellKnownTypes.Dictionary,
                WellKnownTypes.Int32, WellKnownTypes.String), decoder);

            Assert.NotEqual(keyFirst.TypeId, valueFirst.TypeId);
        }

        [Fact]
        public void A_Jagged_Array_Shares_Its_Entry_With_An_Equal_One()
        {
            var decoder = Decoder();

            var first = Intern(w => WriteArrayOf(w, e => WriteGeneric(e, WellKnownTypes.List, WellKnownTypes.Int32)), decoder);
            var second = Intern(w => WriteArrayOf(w, e => WriteGeneric(e, WellKnownTypes.List, WellKnownTypes.Int32)), decoder);
            var flat = Intern(w => WriteArrayOf(w, e => SchemaSection.WriteLeb128(e, WellKnownTypes.Int32)), decoder);

            Assert.Equal(first.TypeId, second.TypeId);
            Assert.NotEqual(first.TypeId, flat.TypeId);      // int[] and List<int>[] are different types
            Assert.Equal(first.ArgTypeIds, second.ArgTypeIds);

            // Three entries: the two array nodes and the one `List<int>` the first array holds as its
            // element — the third shape adds only the flat array itself.
            Assert.Equal(3, decoder.InternedCount);
        }

        [Fact]
        public void A_Synthesized_Entry_Never_Collides_With_A_Real_Type_Id()
        {
            // The whole id scheme rests on this: manifest ids are handed out from 128 upwards, and the
            // synthesized range starts far above any manifest a real application can have.
            Assert.True(TypeValueShapes.FirstSynthesizedId > WellKnownTypes.UserTypeBase);

            var entry = Intern(w => WriteGeneric(w, WellKnownTypes.List, WellKnownTypes.Int32));
            Assert.True(entry.TypeId >= TypeValueShapes.FirstSynthesizedId);
            Assert.Equal(WellKnownTypes.List, entry.DefinitionTypeId);
            Assert.Equal(new[] { WellKnownTypes.Int32 }, entry.ArgTypeIds);
        }

        [Fact]
        public void Each_Load_Starts_From_An_Empty_Index()
        {
            // Nothing is retained across operations: the decoder is per-load state, so two loads of the
            // same bytes intern the same id rather than the second one landing on a stale entry.
            var first = Intern(w => WriteGeneric(w, WellKnownTypes.List, WellKnownTypes.Int32));
            var second = Intern(w => WriteGeneric(w, WellKnownTypes.List, WellKnownTypes.Int32));

            Assert.Equal(first.TypeId, second.TypeId);
        }
    }
}
