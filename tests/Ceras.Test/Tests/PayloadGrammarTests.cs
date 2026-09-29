// The payload grammar, pinned where it now lives.
//
// `Ceras.Format.PayloadGrammar` is the file-side grammar — what a value the *file* describes is written
// as, decided from the file's own entries. It was lifted out of `SchemaTraversalEngine` unchanged, so
// these tests are mostly about properties that had to be *preserved* by the move, plus the one decision
// that is new (`FileCarriesReferenceMarker`).
//
// Four things are pinned here:
//
//  1. **The payload grammars.** All eight `PayloadShape` values, and for each one the two other file-side
//     decisions that go with it — the counted shapes against their own element-count encoding, `Opaque`
//     against its recorded inline flag. `Unknown` is a user generic's member list, a grammar in its own
//     right and the one the skipper used to get wrong by reading a member count for it. Every enum value is
//     asserted reachable, so a ninth member fails this file rather than sitting untested.
//
//  2. **The two element-count encodings.** `Queue<>`/`Stack<>` write a fixed unbiased 4-byte int; every
//     other collection writes a biased varint where `-1` is a null. For small counts the two encodings
//     produce *plausible* wrong answers rather than failures, which is why the encoding is chosen from the
//     file's definition id and why the bound and the null case are asserted per encoding.
//
//  3. **The two reference-marker rules.** `SchemaTraversalEngine.CarriesReferenceMarker` answers from the
//     *current* CLR type; `PayloadGrammar.FileCarriesReferenceMarker` answers from the *file's* record.
//     They agree on everything a single writer produces and part company in two places — an identity this
//     build cannot resolve, and a user generic whose open type changed between a struct and a class — both
//     asserted here directly rather than left to be discovered. The interpreter keeps the current-model
//     rule: this change extracts, it does not alter what the interpreter does.
//
//  4. **Byte-precise agreement with the checked-in old files.** The last two tests drive the interpreter
//     directly over real bytes from `fixtures/Ceras.HistoricalWriter` and assert it consumes *exactly* the
//     declared data section, and that the grammar's answers on the file's own schema section are the ones
//     the engine used. Nothing here re-implements a writer: the bytes are read, never produced.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.PayloadGrammarRules
{
    public class PayloadGrammarTests : TestBase
    {
        // ── The payload grammars ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One row per shape, as the four values the theory takes: the entry a file's own section carries,
        /// the <c>PayloadShape</c> the grammar owes it, and the two other file-side decisions — so a shape
        /// is pinned as a whole rather than one predicate at a time.
        /// <para>
        /// The shape is passed by <em>name</em> rather than by value. <c>PayloadShape</c> is internal to
        /// the Ceras assembly, so a public theory cannot name it in its signature, and making the enum
        /// public to suit a test would widen the format's surface — the shape is a detail of how a file is
        /// read, not part of the contract an application writes against. The name is parsed against the
        /// enum itself, so a renamed or removed member fails the parse rather than passing silently.
        /// </para>
        /// </summary>
        static object[] Row(OldTypeSchema entry, string shape, bool isCollection, bool fixedCount)
            => new object[] { entry, shape, isCollection, fixedCount };

        /// <summary>Neither a well-known type nor an entry in the section these rows describe: the id a
        /// hand-assembled <c>GenericInstantiation</c> points its definition at when the definition is a
        /// <em>user</em> generic, and <see cref="UserDefinitionId"/> when the section describes it.</summary>
        const int UndescribedId = 4242;

        /// <summary>The id a file's own section gives the entry describing the open user generic.</summary>
        const int UserDefinitionId = WellKnownTypes.UserTypeBase + 1;

        /// <summary>The id of the closed instantiation of that user generic.</summary>
        const int UserInstantiationId = WellKnownTypes.UserTypeBase + 2;

        // Ids for the standalone entries above, kept out of the way of the user generic's own two so no two
        // entries of one section can collide. A section indexes by id, so a collision would silently replace
        // an entry rather than fail.
        const int OpaqueId = WellKnownTypes.UserTypeBase + 500;
        const int ClassId = WellKnownTypes.UserTypeBase + 600;
        const int StructId = WellKnownTypes.UserTypeBase + 700;

        const string UserDefinitionName = "Ceras.Test.PayloadGrammarRules.Box`1";
        const string UserInstantiationName = "Ceras.Test.PayloadGrammarRules.Box`1<System.Int32>";

        /// <summary>
        /// A user generic whose *open* type the entries above name, so a file's section can describe a
        /// closed instantiation of it. Declared in this file's own context and never written or read: the
        /// marker rules are pure functions of an <see cref="OldTypeSchema"/> and a CLR type, so the type
        /// only has to <em>exist</em> for the current-model rule to be asked about it.
        /// </summary>
        [CerasContext("PayloadGrammar")]
        [CerasSerializable]
        public class Box<T>
        {
            public int Tag;
            public T Value;
        }

        /// <summary>A value type, for the struct row of the marker table. Written inline with no marker in
        /// front of it, whatever the current build calls it.</summary>
        [CerasContext("PayloadGrammar")]
        [CerasSerializable]
        public struct Point
        {
            public int X;
        }

        public static IEnumerable<object[]> Shapes()
        {
            // An opaque entry is not a generic instantiation at all: it has no definition id and no
            // arguments, so it is classified by its kind and nothing about a definition applies.
            yield return Row(Opaque("PayloadGrammarOpaque", inline: false), "Opaque", isCollection: false, fixedCount: false);
            yield return Row(Opaque("PayloadGrammarOpaqueInline", inline: true), "Opaque", isCollection: false, fixedCount: false);

            yield return Row(Generic(WellKnownTypes.Array, WellKnownTypes.Int32), "Array", isCollection: false, fixedCount: false);

            yield return Row(Generic(WellKnownTypes.KeyValuePair, WellKnownTypes.String, WellKnownTypes.Int32),
                "KeyValuePair", isCollection: false, fixedCount: false);

            yield return Row(Generic(WellKnownTypes.Nullable, WellKnownTypes.Int32), "Nullable", isCollection: false, fixedCount: false);

            // `Tuple` (a class) and `ValueTuple` (a struct) are the same grammar — the items in order — and
            // reach it through two different definitions. The classification is therefore the same for both
            // while the reference-marker answer is the opposite, which is why both are asserted below.
            yield return Row(Generic(WellKnownTypes.Tuple2, WellKnownTypes.Int32, WellKnownTypes.String),
                "Tuple", isCollection: false, fixedCount: false);
            yield return Row(Generic(WellKnownTypes.VTuple2, WellKnownTypes.Int32, WellKnownTypes.String),
                "Tuple", isCollection: false, fixedCount: false);

            yield return Row(Generic(WellKnownTypes.List, WellKnownTypes.Int32), "Elements", isCollection: true, fixedCount: false);
            yield return Row(Generic(WellKnownTypes.HashSet, WellKnownTypes.Int32), "Elements", isCollection: true, fixedCount: false);

            // The two rows the fixed-count rule exists for.
            yield return Row(Generic(WellKnownTypes.Queue, WellKnownTypes.Int32), "Elements", isCollection: true, fixedCount: true);
            yield return Row(Generic(WellKnownTypes.Stack, WellKnownTypes.Int32), "Elements", isCollection: true, fixedCount: true);

            yield return Row(Generic(WellKnownTypes.Dictionary, WellKnownTypes.String, WellKnownTypes.Int32),
                "Entries", isCollection: true, fixedCount: false);

            // A definition the well-known set does not name is a *user* generic, and its payload is its member
            // list. Not a count followed by elements — which is the whole reason the classifier and the
            // count rule are separate answers rather than one.
            yield return Row(Generic(UserDefinitionId, WellKnownTypes.Int32), "Unknown", isCollection: false, fixedCount: false);

            // And a plain class, which the classifier answers Unknown for as well: it is not one of the
            // structural shapes, and its payload is members too.
            yield return Row(Class("PayloadGrammarClass"), "Unknown", isCollection: false, fixedCount: false);
        }

        /// <summary>One row per shape, and the whole enum is covered by
        /// <see cref="Every_Payload_Shape_Value_Is_Reachable"/>: a ninth <c>PayloadShape</c> would have no
        /// row here and fails that test rather than going untested.</summary>
        [Theory]
        [MemberData(nameof(Shapes))]
        public void Every_Payload_Grammar_Is_Classified_From_The_Files_Own_Entry(
            OldTypeSchema entry, string shape, bool isCollection, bool fixedCount)
        {
            // Parsed against the enum itself, so a member that no longer exists fails here rather than
            // comparing against a value nothing has.
            var expected = Enum.Parse<PayloadGrammar.PayloadShape>(shape);

            Assert.Equal(expected, PayloadGrammar.ClassifyPayload(entry));
            Assert.Equal(isCollection, PayloadGrammar.IsCollectionPayload(entry.DefinitionTypeId));
            Assert.Equal(fixedCount, PayloadGrammar.UsesFixedElementCount(entry.DefinitionTypeId));
        }

        [Fact]
        public void Every_Payload_Shape_Value_Is_Reachable()
        {
            var covered = new HashSet<PayloadGrammar.PayloadShape>(
                Shapes().Select(r => Enum.Parse<PayloadGrammar.PayloadShape>((string)r[1])));

            var missing = Enum.GetValues<PayloadGrammar.PayloadShape>().Where(v => !covered.Contains(v)).ToArray();

            Assert.True(missing.Length == 0,
                "these PayloadShape values have no row in Shapes(): " + string.Join(", ", missing));
        }

        [Fact]
        public void The_Argument_At_A_Position_Is_The_Files_Own_And_A_Missing_One_Is_Null()
        {
            // A dictionary's two arguments, in order: `Dictionary<K,V>` and `Dictionary<V,K>` describe the
            // same two identities and are different payloads, so the position is the whole distinction.
            var entry = Generic(WellKnownTypes.Dictionary, WellKnownTypes.String, WellKnownTypes.Int32);

            Assert.Equal(WellKnownTypes.String, PayloadGrammar.ArgId(entry, 0));
            Assert.Equal(WellKnownTypes.Int32, PayloadGrammar.ArgId(entry, 1));

            // Past the end is null rather than a throw, because a caller asks this to find out whether the
            // file recorded an argument at all — and "it did not" is the answer it has to be able to carry.
            Assert.Null(PayloadGrammar.ArgId(entry, 2));
            Assert.Null(PayloadGrammar.ArgId(Generic(WellKnownTypes.List), 0));   // recorded none at all

            // A negative index is a different thing and is not answered: an argument is a position in an
            // ordered list, so a negative one is a caller bug rather than a position the file failed to
            // record. Pinned so "null for a missing one" is not mistaken for "null for anything".
            Assert.Throws<IndexOutOfRangeException>(() => PayloadGrammar.ArgId(entry, -1));
        }

        [Fact]
        public void Advancing_By_A_Fixed_Width_Consumes_Exactly_That_Many_Bytes_And_Produces_No_Value()
        {
            var reader = ReaderOver(0, 1, 2, 3, 4, 5, 6, 7);

            object result = PayloadGrammar.Advance(ref reader, 3);

            // A skip produces no value, and the returned null is what lets one call stand in as an arm of
            // the reader's read-or-skip switch. The witness is the position, not the return: exactly three
            // of the eight bytes are gone, and the next read is the *fourth* byte, so an off-by-one in the
            // width would be visible rather than absorbed.
            Assert.Null(result);
            Assert.Equal(3, reader.Consumed);
            Assert.Equal(5, reader.Remaining);
            Assert.Equal(3, SerializerBinary.ReadByte(ref reader));
        }

        // ── The two element-count encodings ─────────────────────────────────────────────────────────

        [Fact]
        public void A_Queue_And_A_Stack_Count_Is_The_Fixed_Unbiased_Four_Byte_Form()
        {
            foreach (int definition in new[] { WellKnownTypes.Queue, WellKnownTypes.Stack })
            {
                var asWritten = ReadCount(definition, Unbounded, FixedCount(300));
                Assert.Equal(300, asWritten.Count);
                Assert.Equal(4, asWritten.Consumed);

                // The control, and the reason this is a hazard rather than an error: the *same four bytes*
                // read with the biased rule give a plausible different number instead of failing, so a
                // reader that picks the wrong encoding consumes the wrong number of bytes and everything
                // after it decodes from the wrong offset.
                var wrong = ReadCount(WellKnownTypes.List, Unbounded, FixedCount(300));
                Assert.NotEqual(300, wrong.Count);
                Assert.True(wrong.Count > 0, "and it is a *plausible* count, not a failure — which is the hazard");
                Assert.Equal(1, wrong.Consumed);
            }
        }

        [Theory]
        [InlineData(WellKnownTypes.List)]
        [InlineData(WellKnownTypes.Dictionary)]
        [InlineData(WellKnownTypes.HashSet)]
        [InlineData(WellKnownTypes.Array)]
        public void Every_Other_Collection_Count_Is_The_Biased_Varint_Form(int definition)
        {
            Assert.Equal(300, ReadCount(definition, Unbounded, BiasedCount(300)).Count);
        }

        [Fact]
        public void Only_The_Biased_Encodings_Express_A_Null_Collection()
        {
            // A queue or a stack cannot be written as null at all, so `-1` there is corruption rather than
            // a null — and the fixed form reads the biased encoding's `-1` byte as the low byte of a
            // four-byte int, which is a negative count either way.
            foreach (int definition in new[] { WellKnownTypes.List, WellKnownTypes.Dictionary, WellKnownTypes.HashSet, WellKnownTypes.Array })
            {
                Assert.Equal(-1, ReadCount(definition, Unbounded, BiasedCount(-1)).Count);
            }

            var corrupt = Assert.IsType<CerasDataCorruptionException>(
                Record.Exception(() => ReadCount(WellKnownTypes.Queue, Unbounded, FixedCount(-1))));
            Assert.Contains("a queue declares -1 elements", corrupt.Message);
            Assert.Contains("not a null collection", corrupt.Message);
        }

        [Fact]
        public void A_Count_Outside_The_Configured_Maximum_Is_Refused_For_Every_Encoding()
        {
            const uint max = 8;

            // The limit is passed in rather than read from a config, so a reader with no configuration can
            // still ask — and so the two shapes cannot drift into different bounds.
            foreach (int definition in new[] { WellKnownTypes.List, WellKnownTypes.Dictionary, WellKnownTypes.HashSet, WellKnownTypes.Array })
            {
                var biased = Assert.IsType<CerasDataCorruptionException>(
                    Record.Exception(() => ReadCount(definition, max, BiasedCount((int)max + 1))));
                Assert.Contains($"a collection declares {max + 1} elements", biased.Message);
                Assert.Contains($"outside the configured maximum of {max}", biased.Message);

                // At the limit is a legal count, not an off-by-one refusal.
                Assert.Equal((int)max, ReadCount(definition, max, BiasedCount((int)max)).Count);
            }

            foreach (int definition in new[] { WellKnownTypes.Queue, WellKnownTypes.Stack })
            {
                string shape = definition == WellKnownTypes.Queue ? "queue" : "stack";
                var fixedCount = Assert.IsType<CerasDataCorruptionException>(
                    Record.Exception(() => ReadCount(definition, max, FixedCount((int)max + 1))));
                Assert.Contains($"a {shape} declares {max + 1} elements", fixedCount.Message);
                Assert.Contains("plain 4-byte element count", fixedCount.Message);

                Assert.Equal((int)max, ReadCount(definition, max, FixedCount((int)max)).Count);
            }
        }

        [Fact]
        public void A_Negative_Count_Is_Refused_Even_Where_No_Limit_Is_Configured()
        {
            // The default limit is unbounded, so this row is about the *sign*, not the size: a count below
            // zero is a corrupt field in both encodings, and only the biased one has a legal negative.
            var fixedCount = Assert.IsType<CerasDataCorruptionException>(
                Record.Exception(() => ReadCount(WellKnownTypes.Stack, Unbounded, FixedCount(int.MinValue))));
            Assert.Contains("a stack declares -2147483648 elements", fixedCount.Message);

            // The biased side, where a negative count is anything below the null marker. It cannot be written
            // through the writer's own primitive — `count + bias` for a negative count is a huge unsigned
            // value, which is a *different* corrupt field — so the bytes are assembled directly: a varint
            // whose value is above int.MaxValue, which is what such a field looks like on the wire.
            var biased = Assert.IsType<CerasDataCorruptionException>(
                Record.Exception(() => ReadCount(WellKnownTypes.List, Unbounded, OversizedCount())));
            Assert.Contains("a collection declares", biased.Message);
            Assert.Contains("elements, outside the configured maximum of", biased.Message);
        }

        [Fact]
        public void The_Count_Encoding_Is_Chosen_By_The_Files_Definition_Not_By_The_Collection_It_Resolves_To()
        {
            // The interpreter's whole reason for carrying the count rule in the grammar: a rename, a
            // re-assigned id or a changed shape must still decode by the layout the *writer* used. The
            // entry's own definition id is the only input, and it is what decides the encoding.
            var entry = Generic(WellKnownTypes.Queue, WellKnownTypes.Int32);

            Assert.True(PayloadGrammar.IsCollectionPayload(entry.DefinitionTypeId));
            Assert.Equal(PayloadGrammar.PayloadShape.Elements, PayloadGrammar.ClassifyPayload(entry));

            // Read with the entry's own id: the fixed form, four bytes.
            var asWritten = ReadCount(entry.DefinitionTypeId, Unbounded, FixedCount(2));
            Assert.Equal(2, asWritten.Count);
            Assert.Equal(4, asWritten.Consumed);

            // And with the id of a *different* shape: the biased rule, one byte for these bytes. The
            // elements would then be read from the wrong offset, which is why this is asserted as a
            // difference rather than as an error — nothing here fails, the stream is just wrong.
            var misread = ReadCount(WellKnownTypes.List, Unbounded, FixedCount(2));
            Assert.NotEqual(2, misread.Count);
            Assert.Equal(1, misread.Consumed);
        }

        // ── The two reference-marker rules ───────────────────────────────────────────────────────────

        [Fact]
        public void A_File_Sided_Marker_Answer_Ignores_The_Current_Model_Where_The_Interpreter_Cannot()
        {
            // The interpreter's rule and the file-side rule, asked the same questions. They agree on
            // everything a single writer produces, which is the property that lets the file-side rule be
            // extracted without changing what the interpreter does.
            var view = View(
                Generic(WellKnownTypes.List, WellKnownTypes.Int32),
                Generic(WellKnownTypes.Nullable, WellKnownTypes.Int32),
                Generic(WellKnownTypes.VTuple2, WellKnownTypes.Int32, WellKnownTypes.String),
                Generic(WellKnownTypes.Tuple2, WellKnownTypes.Int32, WellKnownTypes.String),
                Generic(WellKnownTypes.Array, WellKnownTypes.Int32));

            void Agree(OldTypeSchema entry, Type currentType)
            {
                Assert.Equal(SchemaTraversalEngine.CarriesReferenceMarker(entry, currentType),
                             PayloadGrammar.FileCarriesReferenceMarker(entry, view));
            }

            Agree(Generic(WellKnownTypes.List, WellKnownTypes.Int32), typeof(List<int>));
            Agree(Generic(WellKnownTypes.Dictionary, WellKnownTypes.String, WellKnownTypes.Int32), typeof(Dictionary<string, int>));
            Agree(Generic(WellKnownTypes.Nullable, WellKnownTypes.Int32), typeof(int?));
            Agree(Generic(WellKnownTypes.KeyValuePair, WellKnownTypes.String, WellKnownTypes.Int32), typeof(KeyValuePair<string, int>));
            Agree(Generic(WellKnownTypes.VTuple2, WellKnownTypes.Int32, WellKnownTypes.String), typeof(ValueTuple<int, string>));
            Agree(Generic(WellKnownTypes.Tuple2, WellKnownTypes.Int32, WellKnownTypes.String), typeof(Tuple<int, string>));
            Agree(Class("PayloadGrammarClass"), typeof(Box<int>));
            Agree(Struct("PayloadGrammarStruct"), typeof(Point));

            // The opaque branch, which both rules answer from the file's own recorded flag and which no
            // current type can override.
            Agree(Opaque("PayloadGrammarOpaque", inline: true), typeof(string));
            Agree(Opaque("PayloadGrammarOpaque", inline: false), typeof(string));

            // And the case where the two rules part company because of what a null current type means: an
            // identity this build cannot resolve. The current-model rule has nothing but the null to go on
            // and answers "behind a reference" — which is the pre-existing interpreter behaviour and is
            // deliberately *not* changed here, because the interpreter does have a plan for such a value
            // and a different answer would move a byte for every such migration. The file-side rule is not
            // in that position: `Nullable` is a struct in the format's own definition table, so the answer
            // is a fact rather than a default. Asserted on both sides because the divergence is the reason
            // the two rules exist, and an unasserted divergence is one a later reader would "fix" by
            // merging them.
            var nullable = Generic(WellKnownTypes.Nullable, WellKnownTypes.Int32);
            Assert.True(SchemaTraversalEngine.CarriesReferenceMarker(nullable, null));
            Assert.False(PayloadGrammar.FileCarriesReferenceMarker(nullable, view));
        }

        [Fact]
        public void A_User_Generic_Takes_Its_Value_Or_Reference_Nature_From_The_Files_Definition_Entry()
        {
            // The one input on which the two rules are *meant* to differ, and the reason the file-side rule
            // exists: a build that changed `Box<T>` from a struct into a class. The file still says the
            // open type was a struct, so the payload is its member list with no marker in front of it; a
            // rule that asked this build's types would read a marker out of the first member's bytes and
            // shift everything after it.
            //
            // Milestone 1 does not change the interpreter: it still asks the current model, because it
            // reads into the current model and has to be right about what this build declares. The
            // file-side rule is what a file-faithful decoder with no current model will ask.
            var fileSaysStruct = View(
                UserInstantiation(definitionId: UserDefinitionId),
                Struct(UserDefinitionName, typeId: UserDefinitionId));
            var fileSaysClass = View(
                UserInstantiation(definitionId: UserDefinitionId),
                Class(UserDefinitionName, typeId: UserDefinitionId));

            var entry = UserInstantiation(definitionId: UserDefinitionId);

            Assert.False(PayloadGrammar.FileCarriesReferenceMarker(entry, fileSaysStruct));
            Assert.True(PayloadGrammar.FileCarriesReferenceMarker(entry, fileSaysClass));

            // The same entry, the same current type, both rules — the divergence, stated.
            Assert.True(SchemaTraversalEngine.CarriesReferenceMarker(entry, typeof(Box<int>)));
            Assert.False(PayloadGrammar.FileCarriesReferenceMarker(entry, fileSaysStruct));
        }

        [Fact]
        public void A_User_Generic_Whose_Definition_Entry_Is_An_Opaque_Frame_Takes_That_Frames_Own_Flag()
        {
            // The same question asked of a definition the file records as a custom formatter's block: the
            // entry records inline-ness as its own fact, so the instantiation inherits it rather than
            // re-deciding it.
            var entry = UserInstantiation(definitionId: UserDefinitionId);

            Assert.False(PayloadGrammar.FileCarriesReferenceMarker(entry,
                View(entry, Opaque(UserDefinitionName, inline: true, typeId: UserDefinitionId))));
            Assert.True(PayloadGrammar.FileCarriesReferenceMarker(entry,
                View(entry, Opaque(UserDefinitionName, inline: false, typeId: UserDefinitionId))));
        }

        [Fact]
        public void A_Definition_Nothing_Describes_Is_Refused_Rather_Than_Guessed()
        {
            // A definition that is neither one of the format's own structural definitions nor an entry the
            // file's section describes: nothing says how the value is written, and a reader that went on
            // would have to guess whether a marker stands in front of it.
            var entry = Generic(UndescribedId, WellKnownTypes.Int32);
            var view = View(entry);   // the entry itself is described; its *definition* is not

            var error = Record.Exception(() => PayloadGrammar.FileCarriesReferenceMarker(entry, view));
            var schema = Assert.IsType<CerasSchemaException>(error);

            // The shared invariant and its shared wording, so this door cannot describe the same defect
            // differently from the section parser, the forge or the plan builder.
            Assert.Contains(UserInstantiationName, schema.Message);
            Assert.Contains($"definition type id {UndescribedId}", schema.Message);
            Assert.Contains("neither one of the format's structural definitions", schema.Message);
        }

        [Fact]
        public void A_Definition_That_Is_Itself_An_Instantiation_Is_Refused()
        {
            // A definition is an *open* type, so an entry the file describes as a closed instantiation
            // says nothing about whether the value it stands for was written inline. Refused rather than
            // recursed into: the answer would have to come from somewhere the file does not record.
            var entry = UserInstantiation(definitionId: UserDefinitionId);

            // The definition entry is a *closed* instantiation of the very id it is asked about, so the
            // section describes the identity and says nothing about whether the value is a value type. A
            // distinct name, because a section indexes by name as well as by id and two entries under one
            // name would be one entry.
            var view = View(entry, UserInstantiation(definitionId: UserDefinitionId,
                name: UserDefinitionName, typeId: UserDefinitionId));

            var error = Record.Exception(() => PayloadGrammar.FileCarriesReferenceMarker(entry, view));
            var schema = Assert.IsType<CerasSchemaException>(error);

            Assert.Contains($"definition type id {UserDefinitionId}", schema.Message);
            Assert.Contains("a definition is an open type", schema.Message);
        }

        [Fact]
        public void The_File_Sided_Rule_Requires_Both_Its_Arguments()
        {
            var entry = Generic(WellKnownTypes.List, WellKnownTypes.Int32);
            var view = View(entry);

            Assert.Throws<ArgumentNullException>(() => PayloadGrammar.FileCarriesReferenceMarker(null!, view));
            Assert.Throws<ArgumentNullException>(() => PayloadGrammar.FileCarriesReferenceMarker(entry, null!));
        }

        [Fact]
        public void An_Opaque_Entry_Takes_Its_Marker_Answer_From_The_Recorded_Flag_Either_Way()
        {
            // The flag is the file's own record and is not overridable from either side, because a value
            // behind a marker and a value written inline begin at different bytes.
            var behindMarker = Opaque("PayloadGrammarOpaque", inline: false);
            var inline = Opaque("PayloadGrammarOpaqueInline", inline: true);
            var view = View(behindMarker, inline);

            Assert.True(PayloadGrammar.FileCarriesReferenceMarker(behindMarker, view));
            Assert.False(PayloadGrammar.FileCarriesReferenceMarker(inline, view));
            Assert.True(SchemaTraversalEngine.CarriesReferenceMarker(behindMarker, typeof(string)));
            Assert.False(SchemaTraversalEngine.CarriesReferenceMarker(inline, typeof(string)));
        }

        // ── The Type value: structural decode with nothing resolved, and its bounds ──────────────────

        /// <summary>A decoder whose manifest resolver answers null for every id — a build that has none of
        /// the file's types. Everything structural still decodes, which is the property a file-faithful
        /// decoder needs and the reason its resolver may legitimately resolve nothing.</summary>
        static TypeValueShapes Decoder() => new(new FileSchemaView(new Dictionary<string, OldTypeSchema>()), _ => null);

        static TypeShape Decode(Action<IBufferWriter<byte>> writeTypeValue, TypeValueShapes decoder = null)
        {
            decoder ??= Decoder();
            var bytes = new ArrayBufferWriter<byte>();
            writeTypeValue(bytes);
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(bytes.WrittenMemory));
            int tag = SchemaSection.ReadTypeIdLeb128(ref reader, "the test's tag");
            return decoder.Decode(tag, ref reader);
        }

        static void WriteGeneric(IBufferWriter<byte> w, uint definition, params uint[] arguments)
        {
            SchemaSection.WriteLeb128(w, definition);
            SchemaSection.WriteLeb128(w, (uint)arguments.Length);
            foreach (uint argument in arguments) SchemaSection.WriteLeb128(w, argument);
        }

        [Fact]
        public void A_Type_Value_Decodes_Structurally_With_No_Manifest_Resolver()
        {
            // `List<int>` over a resolver that answers null for every manifest id. The reserved id space
            // needs no resolver at all — a well-known definition and a scalar argument are facts of the
            // format — so the whole structure is readable by a build that has none of the file's types, and
            // the closed CLR type is reconstructed from the reserved table alone.
            var decoder = Decoder();
            var shape = Decode(w => WriteGeneric(w, WellKnownTypes.List, WellKnownTypes.Int32), decoder);

            Assert.True(shape.DescribesPayload);
            Assert.Equal(WellKnownTypes.List, shape.DefinitionId);
            Assert.Single(shape.Arguments);
            Assert.Equal(WellKnownTypes.Int32, shape.Arguments[0].DefinitionId);
            Assert.Equal(typeof(List<int>), shape.ClrType);

            // And the grammar classifies the interned entry exactly as it would any other described shape,
            // so the payload that follows is read by the same rules.
            var entry = decoder.InternRoot(shape);
            Assert.Equal(PayloadGrammar.PayloadShape.Elements, PayloadGrammar.ClassifyPayload(entry));
            Assert.True(PayloadGrammar.IsCollectionPayload(entry.DefinitionTypeId));
            Assert.False(PayloadGrammar.UsesFixedElementCount(entry.DefinitionTypeId));
            Assert.Equal(new[] { WellKnownTypes.Int32 }, entry.ArgTypeIds);

            // A queue over the same empty resolver: the one shape whose count encoding is not the biased
            // one, so the structural decode and the encoding have to be asked separately.
            var queueShape = Decode(w => WriteGeneric(w, WellKnownTypes.Queue, WellKnownTypes.Int32), decoder);
            var queueEntry = decoder.InternRoot(queueShape);
            Assert.Equal(PayloadGrammar.PayloadShape.Elements, PayloadGrammar.ClassifyPayload(queueEntry));
            Assert.True(PayloadGrammar.UsesFixedElementCount(queueEntry.DefinitionTypeId));
        }

        [Fact]
        public void A_Type_Value_Nests_And_Keeps_Its_Argument_Order_With_Nothing_Resolved()
        {
            // `List<Dictionary<string, int>>`: two levels, no manifest id involved at every level, argument
            // order significant. A decoder that lost the nesting would read the inner entries as elements of
            // the outer; one that lost the order would read each entry's key as its value.
            var decoder = Decoder();
            var shape = Decode(w =>
            {
                SchemaSection.WriteLeb128(w, WellKnownTypes.List);
                SchemaSection.WriteLeb128(w, 1);
                WriteGeneric(w, WellKnownTypes.Dictionary, WellKnownTypes.String, WellKnownTypes.Int32);
            }, decoder);

            Assert.Equal(WellKnownTypes.List, shape.DefinitionId);
            var inner = shape.Arguments[0];
            Assert.Equal(WellKnownTypes.Dictionary, inner.DefinitionId);
            Assert.Equal(new[] { WellKnownTypes.String, WellKnownTypes.Int32 }, inner.Arguments.Select(a => a.DefinitionId));

            // A `List` is elements whatever its element is, so the outer shape is elements and only the
            // inner one is entries — which is what makes the nested path the same path a declared
            // `List<Dictionary<..>>` member takes, one level of grammar per level of the type value.
            var outer = decoder.InternRoot(shape);
            Assert.Equal(PayloadGrammar.PayloadShape.Elements, PayloadGrammar.ClassifyPayload(outer));
            var innerEntry = decoder.InternRoot(inner);
            Assert.Equal(PayloadGrammar.PayloadShape.Entries, PayloadGrammar.ClassifyPayload(innerEntry));
        }

        [Theory]
        [InlineData(WellKnownTypes.List, 0)]
        [InlineData(WellKnownTypes.List, 2)]
        [InlineData(WellKnownTypes.Dictionary, 1)]
        [InlineData(WellKnownTypes.Nullable, 2)]
        public void A_Type_Value_Naming_A_Definition_With_The_Wrong_Arity_Is_Refused(int definition, int declaredCount)
        {
            // Asked before the argument array is sized: a wrong count names no concrete type at all, so the
            // payload that follows it cannot be counted. This is the same invariant the section parser
            // holds, at the one door reachable after a section has already been parsed.
            var decoder = Decoder();
            var error = Record.Exception(() => Decode(w =>
            {
                SchemaSection.WriteLeb128(w, (uint)definition);
                SchemaSection.WriteLeb128(w, (uint)declaredCount);
                foreach (uint _ in Enumerable.Range(0, declaredCount)) SchemaSection.WriteLeb128(w, WellKnownTypes.Int32);
            }, decoder));

            var corrupt = Assert.IsType<CerasDataCorruptionException>(error);
            Assert.Contains("Corrupt System.Type payload", corrupt.Message);
            Assert.Contains("declares", corrupt.Message);
            Assert.Contains("does not name a type at all", corrupt.Message);
        }

        [Fact]
        public void A_Type_Value_Nesting_Past_The_Depth_Bound_Is_Refused()
        {
            // 64 levels are allowed; the 65th is refused before the walk can exhaust the stack. The bound is
            // the format's own, so it is stated as a number rather than derived from an internal constant.
            Assert.Equal(WellKnownTypes.Array, DecodeNestedArrays(64).DefinitionId);

            var error = Record.Exception(() => DecodeNestedArrays(65));
            var corrupt = Assert.IsType<CerasDataCorruptionException>(error);
            Assert.Contains("nest more than 64 levels deep", corrupt.Message);
        }

        /// <summary>A type value of <paramref name="levels"/> nested <c>Array</c> tags around an
        /// <c>Int32</c> element type, decoded by a decoder that resolves nothing.</summary>
        static TypeShape DecodeNestedArrays(int levels)
        {
            var bytes = new ArrayBufferWriter<byte>();
            for (int i = 0; i < levels; i++) SchemaSection.WriteLeb128(bytes, WellKnownTypes.Array);
            SchemaSection.WriteLeb128(bytes, WellKnownTypes.Int32);

            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(bytes.WrittenMemory));
            int tag = SchemaSection.ReadTypeIdLeb128(ref reader, "the test's tag");
            return Decoder().Decode(tag, ref reader);
        }

        [Fact]
        public void A_Type_Value_Naming_More_Types_Than_The_Node_Bound_Is_Refused()
        {
            // A node is one named type in the value, so the bound is about how many *types* one value may
            // name rather than how many bytes it occupies: a handful of tags can describe a great many types
            // if the grammar is recursive, and each one costs a TypeShape. 256 are allowed, 257 are refused.
            //
            // The shape below is a full binary tree of dictionaries, so its node count doubles with every
            // level while its depth grows by one — which is what makes it the right instrument: a chain
            // would hit the *depth* bound first (the test above) and never reach this one. One level of
            // difference is the whole margin between the two bounds' verdicts, which is why both shapes are
            // named here rather than just the failing one.
            DecodeBinaryTree(depth: 7);       // 2^8 - 1 = 255 nodes, 8 levels: the control, and a legal value

            var error = Record.Exception(() => DecodeBinaryTree(depth: 8));   // 511 nodes, still only 8 deep
            var corrupt = Assert.IsType<CerasDataCorruptionException>(error);
            Assert.Contains("names more than 256 types", corrupt.Message);
        }

        /// <summary>A type value of <paramref name="depth"/> levels of <c>Dictionary&lt;T,T&gt;</c> whose
        /// arguments are themselves the same shape, down to <c>Int32</c> leaves: <c>2^(depth+1) - 1</c> named
        /// types in <c>depth + 1</c> levels. Decoded by a decoder that resolves nothing.</summary>
        static void DecodeBinaryTree(int depth)
        {
            var bytes = new ArrayBufferWriter<byte>();
            WriteBinaryTree(bytes, depth);

            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(bytes.WrittenMemory));
            int tag = SchemaSection.ReadTypeIdLeb128(ref reader, "the test's tag");
            Decoder().Decode(tag, ref reader);
        }

        static void WriteBinaryTree(IBufferWriter<byte> w, int depth)
        {
            if (depth == 0)
            {
                SchemaSection.WriteLeb128(w, WellKnownTypes.Int32);
                return;
            }

            SchemaSection.WriteLeb128(w, WellKnownTypes.Dictionary);
            SchemaSection.WriteLeb128(w, 2);
            WriteBinaryTree(w, depth - 1);       // the key
            WriteBinaryTree(w, depth - 1);       // the value
        }

        [Fact]
        public void A_Type_Value_Tag_Nothing_Is_Assigned_To_Is_Refused()
        {
            var error = Record.Exception(() => Decode(w => SchemaSection.WriteLeb128(w, (uint)(WellKnownTypes.DateTimeOffset + 1))));

            var corrupt = Assert.IsType<CerasDataCorruptionException>(error);
            Assert.Contains("no type is assigned to it", corrupt.Message);
        }

        [Fact]
        public void A_Type_Value_That_Is_Not_A_Type_Id_Is_Refused_Before_It_Is_Read_As_A_Small_One()
        {
            // A five-byte LEB128 above int.MaxValue narrows to a negative id, which would then match a small
            // well-known type — so a load could succeed on a value the file does not describe. It is
            // refused while it is still a LEB128.
            var error = Record.Exception(() => Decode(w =>
                w.Write(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x08 })));

            var corrupt = Assert.IsType<CerasDataCorruptionException>(error);
            Assert.Contains("is not a type id", corrupt.Message);
        }

        [Fact]
        public void A_Type_Value_Naming_A_Manifest_Id_Nothing_Describes_Is_Refused()
        {
            // A terminal tag that is a manifest id defers to the file's own schema section for how the
            // payload is written, and a section that does not contain it says nothing. So the value is
            // refused rather than read as a value of no bytes, which would put every following field out of
            // step without failing.
            //
            // The refusal is reached when the identity is resolved into an entry the payload is read
            // through, not by the decode: decoding a manifest id only records that the file named one, and a
            // build that has no such type legitimately cannot name it back. It is reached here one level in,
            // as the *element* of a collection — the position where a terminal argument becomes something
            // the payload grammar must read — which is the same route the load takes.
            var decoder = Decoder();
            var error = Record.Exception(() =>
            {
                var shape = Decode(w => WriteGeneric(w, WellKnownTypes.List, 9999u), decoder);
                decoder.InternRoot(shape);
            });

            var schema = Assert.IsType<CerasSchemaException>(error);
            Assert.Contains("9999", schema.Message);
            Assert.Contains("the save's own schema section does not describe it", schema.Message);
        }

        // ── Byte-precise agreement with the checked-in old files ──────────────────────────────────────

        /// <summary>Every fixture whose bytes a previous build's writer produced and whose current model
        /// this assembly still has, with the root to read it as. The models belong to the two historical
        /// fixture suites, which is why they are used rather than re-declared here: a second copy of the
        /// same v1 model would be a second thing to keep in step with the first.</summary>
        public static IEnumerable<object[]> HistoricalFiles()
        {
            // Ceras.Test.HistoricalFixtures, context "HistoricalCurrent".
            yield return HistoricalFile("SaveGame.bin", "HistoricalCurrent", typeof(Ceras.Test.HistoricalFixtures.SaveGame));
            yield return HistoricalFile("PlayerCycle.bin", "HistoricalCurrent", typeof(Ceras.Test.HistoricalFixtures.Actor));
            yield return HistoricalFile("Inventory.bin", "HistoricalCurrent", typeof(Ceras.Test.HistoricalFixtures.Inventory));

            // Ceras.Test.HistoricalPolymorphicFixtures, context "HistoricalPolyCurrent". The polymorphic
            // rows are the ones where the *Type value* grammar supplies the payload description, so they
            // are the strongest check that the extracted rules still reach the values a writer wrote.
            yield return HistoricalFile("PolyNestedList.bin", "HistoricalPolyCurrent", typeof(Ceras.Test.HistoricalPolymorphicFixtures.Crate));
            yield return HistoricalFile("PolyJagged.bin", "HistoricalPolyCurrent", typeof(Ceras.Test.HistoricalPolymorphicFixtures.Crate));
            yield return HistoricalFile("PolyQueueOfQueue.bin", "HistoricalPolyCurrent", typeof(Ceras.Test.HistoricalPolymorphicFixtures.Crate));
            yield return HistoricalFile("PolyMixedShapes.bin", "HistoricalPolyCurrent", typeof(Ceras.Test.HistoricalPolymorphicFixtures.Crate));
            yield return HistoricalFile("PolyUserType.bin", "HistoricalPolyCurrent", typeof(Ceras.Test.HistoricalPolymorphicFixtures.Crate));
            yield return HistoricalFile("PolyObjectKept.bin", "HistoricalPolyCurrent", typeof(Ceras.Test.HistoricalPolymorphicFixtures.Crate));
            yield return HistoricalFile("PolyRemovedSlot.bin", "HistoricalPolyCurrent", typeof(Ceras.Test.HistoricalPolymorphicFixtures.EmptyCrate));
            yield return HistoricalFile("PolyRemovedNested.bin", "HistoricalPolyCurrent", typeof(Ceras.Test.HistoricalPolymorphicFixtures.MixedCrate));
            yield return HistoricalFile("PolyRemovedObjectMid.bin", "HistoricalPolyCurrent", typeof(Ceras.Test.HistoricalPolymorphicFixtures.MixedCrate));
        }

        static object[] HistoricalFile(string fixture, string context, Type root)
            => new object[] { fixture, context, root };

        /// <summary>
        /// The interpreter, driven directly over real previous-writer bytes, consumes **exactly** the
        /// region the header declares — the root's payload and nothing else.
        /// <para>
        /// This is the byte-precise claim about the extracted grammar, and it is executable without
        /// re-implementing a writer: the bytes are read from the checked-in fixtures, and the position the
        /// reader stops at is compared with the region the file itself declares. A count read with the
        /// wrong encoding, a marker decided from the wrong side, or one fixed width off would all move the
        /// stop, so a single off-by-one anywhere in the grammar fails here.
        /// </para>
        /// <para>
        /// The root type id is read separately because that is the envelope, not the payload: it is what
        /// <c>Load</c> does, and the assertion is about what the root's own payload occupies.
        /// </para>
        /// </summary>
        [Theory]
        [MemberData(nameof(HistoricalFiles))]
        public void The_Interpreter_Consumes_Exactly_The_Declared_Data_Section(string fixture, string context, Type root)
        {
            var bytes = ReadFixture(fixture);
            var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters(context));
            serializer.Save(Activator.CreateInstance(root)!, 1);   // seal and bake, as a load does

            var section = ParseSection(bytes, serializer);
            var declared = DeclaredDataSectionLength(bytes);

            // The declared region, and nothing else: a reader over more of the file could satisfy "stopped
            // at the end" by running into the string table, which is a different thing from having read the
            // whole payload.
            var payload = new byte[declared];
            Array.Copy(bytes, DeclaredDataSectionOffset(bytes), payload, 0, payload.Length);

            var engine = new SchemaTraversalEngine(
                section.Schemas,
                section.PayloadStrings,
                BitConverter.ToUInt32(bytes, RobustHeader.GameVersionOffset),
                serializer,
                MigrationPlanBuilder.BuildAll(section.Schemas, serializer.Config.SchemaRegistry,
                    serializer.Config.AllowDroppingCustomPayloads));

            // The root type id is the envelope, not the payload, so it is read here exactly as `Load` does
            // and the assertion is about what is left for the root's own value.
            var head = new SequenceReader<byte>(new ReadOnlySequence<byte>(payload));
            int storedRootId = (int)SchemaSection.ReadLeb128(ref head);
            Assert.True(storedRootId >= WellKnownTypes.UserTypeBase,
                $"{fixture}: the stored root type id {storedRootId} is not a manifest id, so this file is not what the theory claims");

            int offset = (int)head.Consumed;
            int payloadStart = offset;

            var idToType = CerasRobustSerializer.BuildIdToType(section.Schemas, serializer.Config.SchemaRegistry, serializer.Config.TypeBinder);
            object value;
            using (RobustContext.BeginRead(section.PayloadStrings, idToType, serializer.Config.SizeLimits.MaxStringLength))
            {
                value = ReadRoot(engine, root, payload, ref offset);
            }

            // Not null: a reader that consumed the whole region by producing nothing would satisfy the
            // arithmetic, so the value itself is the other half of the claim.
            Assert.NotNull(value);
            Assert.Equal(payload.Length, offset);

            // And the payload alone is the rest of the region after the root id, so it was not empty.
            Assert.True(offset > payloadStart, $"{fixture}: the root's payload is empty, so nothing was read");

            // A few of the fixtures carry nothing but markers and fixed-width fields, so the region is short
            // by design; a reader that stopped one byte early would still be "close". Stating the exact stop
            // rather than a tolerance is what makes the row meaningful, and this says the region was not a
            // single byte to begin with.
            Assert.True(payload.Length > 1, $"{fixture}: the declared data section is one byte, so there is nothing to be precise about");
        }

        /// <summary>The grammar's own answers on the file's section are the ones the engine used.</summary>
        [Theory]
        [MemberData(nameof(HistoricalFiles))]
        public void The_Grammar_Agrees_With_The_Engine_On_Every_Entry_Of_A_Real_Old_File(string fixture, string context, Type root)
        {
            var bytes = ReadFixture(fixture);
            var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters(context));
            serializer.Save(Activator.CreateInstance(root)!, 1);   // seal and bake

            var section = ParseSection(bytes, serializer);
            var view = new FileSchemaView(section.Schemas);
            var registry = serializer.Config.SchemaRegistry;

            var disagreements = new List<string>();
            int compared = 0;
            int collections = 0;

            foreach (var entry in section.Schemas.Values.OrderBy(s => s.TypeId))
            {
                // The classifier and the count rule, cross-checked against each other rather than against
                // a restatement of either: an entry whose definition is a built-in collection always
                // describes a counted payload, and exactly one of the two count encodings is not the
                // biased one. Both are the same file-side decision about the same definition id, so a
                // shape that disagreed with its own count rule would be a grammar with two answers.
                if (entry.Kind == SchemaTypeKind.GenericInstantiation && PayloadGrammar.IsCollectionPayload(entry.DefinitionTypeId))
                {
                    var shape = PayloadGrammar.ClassifyPayload(entry);
                    Assert.True(shape is PayloadGrammar.PayloadShape.Elements or PayloadGrammar.PayloadShape.Entries,
                        $"{fixture}: {entry.StableName} has definition {entry.DefinitionTypeId}, which is a collection, " +
                        $"but classifies as {shape}");

                    // Queue/Stack are the fixed-count pair; nothing else is. Asserted as a set membership
                    // so the rule is stated once rather than as two literals in a row.
                    bool fixedCount = PayloadGrammar.UsesFixedElementCount(entry.DefinitionTypeId);
                    bool isQueueOrStack = entry.DefinitionTypeId is WellKnownTypes.Queue or WellKnownTypes.Stack;
                    Assert.True(fixedCount == isQueueOrStack,
                        $"{fixture}: {entry.StableName} (definition {entry.DefinitionTypeId}) uses " +
                        $"{(fixedCount ? "the fixed" : "the biased")} count encoding");
                    collections++;
                }

                // The reference-marker rules, asked side by side. The engine's answer is the current model's,
                // so it is only comparable where this build has the type: an entry whose name resolves to
                // nothing is a removed type, and the file-side rule is then the only one with an answer.
                bool currentHasIt = registry.TryResolveType(entry.StableName, out var currentType);
                if (!currentHasIt) continue;

                compared++;
                if (SchemaTraversalEngine.CarriesReferenceMarker(entry, currentType)
                    != PayloadGrammar.FileCarriesReferenceMarker(entry, view))
                {
                    disagreements.Add($"{entry.StableName} (id {entry.TypeId}, {entry.Kind}, " +
                                      $"definition {entry.DefinitionTypeId}, current {currentType})");
                }
            }

            // Neither loop may be vacuous. A section whose entries all failed to resolve would compare
            // nothing and pass, and a section with no collection entry would check no count rule — so both
            // counts are asserted, which is what turns "no disagreement" into "the same answer, asked".
            Assert.True(compared > 0, $"{fixture}: no entry resolved to a current type, so nothing was compared");
            Assert.True(collections > 0 || !HasAnyCollectionDefinition(section.Schemas),
                $"{fixture}: a collection definition is in the section but no count rule was checked");

            Assert.True(disagreements.Count == 0,
                $"{fixture}: the file-side and current-model marker rules disagree on " +
                string.Join("; ", disagreements));
        }

        /// <summary>Whether any entry in a section is a described instance of one of the built-in
        /// collections. The control for the count rule's coverage assertion above: a file that has none must
        /// not be reported as a file whose count rule went unchecked.</summary>
        static bool HasAnyCollectionDefinition(Dictionary<string, OldTypeSchema> schemas)
            => schemas.Values.Any(s => s.Kind == SchemaTypeKind.GenericInstantiation
                                    && PayloadGrammar.IsCollectionPayload(s.DefinitionTypeId));

        // ── The fixture's own section, read (never written) ─────────────────────────────────────────

        static byte[] ReadFixture(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "HistoricalV1", name);
            Assert.True(File.Exists(path), $"missing golden fixture '{name}' at '{path}'. " +
                "Run fixtures/Ceras.HistoricalWriter to regenerate it (see its README.md).");
            return File.ReadAllBytes(path);
        }

        static long DeclaredDataSectionOffset(byte[] file)
            => BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(RobustHeader.DataSectionOffsetOffset));

        static long DeclaredStringTableOffset(byte[] file)
            => BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(RobustHeader.StringTableOffsetOffset));

        static long DeclaredDataSectionLength(byte[] file)
            => DeclaredStringTableOffset(file) - DeclaredDataSectionOffset(file);

        readonly struct ParsedFile
        {
            public readonly Dictionary<string, OldTypeSchema> Schemas;
            public readonly string[] PayloadStrings;

            public ParsedFile(Dictionary<string, OldTypeSchema> schemas, string[] payloadStrings)
            {
                Schemas = schemas;
                PayloadStrings = payloadStrings;
            }
        }

        /// <summary>The file's own two regions, decoded with the format's own readers — the same two calls
        /// <c>Load</c> makes on the slow path, so what the grammar is asked about here is exactly what the
        /// interpreter was asked about.</summary>
        static ParsedFile ParseSection(byte[] file, CerasRobustSerializer serializer)
        {
            // The file's own compression flag for the payload table, as `Load` reads it. The schema string
            // table is never compressed, which is why it is loaded with `Disabled` whatever the flag says.
            var compression = (StringTableCompression)file[RobustHeader.FlagsOffset];

            int schemaStringOffset = (int)BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(RobustHeader.SchemaStringOffsetOffset));
            int dataSectionOffset = (int)DeclaredDataSectionOffset(file);
            int stringTableOffset = (int)DeclaredStringTableOffset(file);

            var schemaStrings = StringTable.Load(file, schemaStringOffset, dataSectionOffset - schemaStringOffset,
                StringTableCompression.Disabled,
                serializer.Config.SizeLimits.MaxStringLength,
                serializer.Config.SizeLimits.MaxStringTableSize);

            int schemaStart = RobustHeader.Size;
            var schemas = SchemaSection.Read(file, ref schemaStart, schemaStrings);

            var payloadStrings = StringTable.Load(file, stringTableOffset, file.Length - stringTableOffset,
                compression,
                serializer.Config.SizeLimits.MaxStringLength,
                serializer.Config.SizeLimits.MaxStringTableSize);

            return new ParsedFile(schemas, payloadStrings);
        }

        /// <summary>
        /// <c>SchemaTraversalEngine.ReadRoot&lt;T&gt;</c> is generic and the root type is only known at
        /// runtime here, so it is invoked through a closed generic helper reached by reflection.
        /// <para>
        /// The byte-array overload is the one called, and deliberately: it takes the offset by reference and
        /// reports the consumed count, and it builds its sequence from <c>offset</c> to the end of the array
        /// it is given — which is how the consumption is measured against the declared data section and
        /// nothing beyond it. The <c>ref SequenceReader</c> overload cannot be invoked this way at all,
        /// since a ref struct cannot be boxed.
        /// </para>
        /// </summary>
        static object ReadRoot(SchemaTraversalEngine engine, Type root, byte[] data, ref int offset)
        {
            var helper = typeof(PayloadGrammarTests)
                .GetMethod(nameof(ReadRootOf), BindingFlags.NonPublic | BindingFlags.Static)
                .MakeGenericMethod(root);

            object[] args = { engine, data, offset };
            RootRead result;
            try
            {
                result = (RootRead)helper.Invoke(null, args);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                // The refusal the load would have raised, not the reflection wrapper around it.
                throw e.InnerException;
            }

            offset = result.Offset;
            return result.Value;
        }

        /// <summary>The read value and the offset the reader stopped at. The offset travels back out of the
        /// closed generic because a <c>ref</c> argument cannot be observed through <c>Invoke</c>.</summary>
        static RootRead ReadRootOf<T>(SchemaTraversalEngine engine, byte[] data, int offset)
        {
            var value = engine.ReadRoot<T>(data, ref offset);
            return new RootRead(value, offset);
        }

        readonly struct RootRead
        {
            public readonly object Value;
            public readonly int Offset;

            public RootRead(object value, int offset)
            {
                Value = value;
                Offset = offset;
            }
        }

        // ── Entry builders ────────────────────────────────────────────────────────────────────────────

        /// <summary>A file's schema section, indexed the way a reader indexes it.</summary>
        static FileSchemaView View(params OldTypeSchema[] entries)
        {
            var byName = new Dictionary<string, OldTypeSchema>(StringComparer.Ordinal);
            foreach (var entry in entries) byName[entry.StableName] = entry;
            return new FileSchemaView(byName);
        }

        static OldTypeSchema Generic(int definitionTypeId, params int[] argTypeIds)
            => new(WellKnownTypes.UserTypeBase + definitionTypeId + 100,
                UserInstantiationName,
                Array.Empty<string>(),
                SchemaTypeKind.GenericInstantiation,
                Array.Empty<OldFieldSchema>(),
                0,
                definitionTypeId,
                argTypeIds);

        /// <summary>A closed instantiation of the user generic, carrying the two members its payload really
        /// has — so a reader that mistakes it for a count reads one member as a count and then that many
        /// elements' worth of bytes, which is the failure the member list is the answer to.</summary>
        static OldTypeSchema UserInstantiation(int definitionId, string name = null, int typeId = 0)
            => new(typeId != 0 ? typeId : UserInstantiationId,
                name ?? UserInstantiationName,
                Array.Empty<string>(),
                SchemaTypeKind.GenericInstantiation,
                new[]
                {
                    new OldFieldSchema("Tag", Array.Empty<string>(), WellKnownTypes.Int32, true),
                    new OldFieldSchema("Value", Array.Empty<string>(), WellKnownTypes.Int32, true),
                },
                0,
                definitionId,
                new[] { WellKnownTypes.Int32 });

        /// <summary>The entry describing the <em>open</em> user generic, at the id an instantiation points
        /// its definition at — so the two are found by each other exactly as a real section's are.</summary>
        static OldTypeSchema Opaque(string name, bool inline, int typeId = 0)
            => new(typeId != 0 ? typeId : OpaqueId,
                name,
                Array.Empty<string>(),
                SchemaTypeKind.Opaque,
                Array.Empty<OldFieldSchema>(),
                0,
                0,
                Array.Empty<int>(),
                Array.Empty<OldEnumValue>(),
                "Ceras.Test.PayloadGrammarRules.SomeFormatter",
                inline);

        static OldTypeSchema Class(string name, int typeId = 0)
            => new(typeId != 0 ? typeId : ClassId,
                name,
                Array.Empty<string>(),
                SchemaTypeKind.Class,
                new[] { new OldFieldSchema("Tag", Array.Empty<string>(), WellKnownTypes.Int32, true) },
                0,
                0,
                Array.Empty<int>());

        static OldTypeSchema Struct(string name, int typeId = 0)
            => new(typeId != 0 ? typeId : StructId,
                name,
                Array.Empty<string>(),
                SchemaTypeKind.Struct,
                new[] { new OldFieldSchema("X", Array.Empty<string>(), WellKnownTypes.Int32, true) },
                sizeof(int),
                0,
                Array.Empty<int>());

        // ── Byte builders ─────────────────────────────────────────────────────────────────────────────

        /// <summary>What the configured default is, so the count rows below state the unbounded case as a
        /// number rather than reaching for a config object.</summary>
        const uint Unbounded = uint.MaxValue;

        /// <summary>
        /// One element count, and how many bytes it occupied.
        /// <para>
        /// Wrapped rather than passed a <c>ref SequenceReader</c> by the tests, because a ref struct cannot
        /// be captured by the lambda <c>Record.Exception</c> needs — and the byte count is part of what is
        /// being asserted, so it has to come back out of the call rather than be read off the reader
        /// afterwards.
        /// </para>
        /// </summary>
        static CountRead ReadCount(int definitionTypeId, uint maxCollectionSize, byte[] bytes)
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(bytes));
            int count = PayloadGrammar.ReadCollectionCount(definitionTypeId, maxCollectionSize, ref reader);
            return new CountRead(count, (int)reader.Consumed);
        }

        readonly struct CountRead
        {
            public readonly int Count;
            public readonly int Consumed;

            public CountRead(int count, int consumed)
            {
                Count = count;
                Consumed = consumed;
            }
        }

        static SequenceReader<byte> ReaderOver(params byte[] bytes)
            => new(new ReadOnlySequence<byte>(bytes));

        /// <summary>The fixed, unbiased four-byte element count a queue or a stack writes.</summary>
        static byte[] FixedCount(int count)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, count);
            return bytes;
        }

        /// <summary>The shared bias-<c>+1</c> varint every other collection writes, written through the
        /// format's own primitive rather than restated here.</summary>
        static byte[] BiasedCount(int count)
        {
            var writer = new ArrayBufferWriter<byte>();
            SerializerBinary.WriteUInt32Bias(writer, count, Protocol.CollectionBias);
            return writer.WrittenSpan.ToArray();
        }

        /// <summary>
        /// A biased count field that decodes to a negative number: five continuation bytes, so the value
        /// exceeds <c>int.MaxValue</c> and narrows to a negative count on the way in.
        /// <para>
        /// Assembled by hand because no writer produces it, which is the point — a count this large is
        /// corruption, and the only way to reach the reader with one is to write the bytes a corrupt field
        /// would be. The alternative, asking the writer's own primitive for a negative count, produces a
        /// different corrupt field entirely (an enormous unsigned varint) and would test something else.
        /// </para>
        /// </summary>
        static byte[] OversizedCount() => new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x7F };
    }
}
