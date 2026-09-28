// The `System.Type` value grammar has *two* decoders, and only one of them held it to the format.
//
// A `Type` value is the writer's record of the concrete runtime type of a polymorphic value, and it is
// recursive: `List<int>` is (List, argument count, Int32) and each argument is itself a `Type` value.
// `TypeValueShapes` decodes it for the migration interpreter; `RobustTypeFormatter.DeserializeCore`
// decodes the very same bytes for the matching-layout reader. The interpreter's decoder has always asked
// of the two fields that follow the tag — the argument count is read as a *type id* (so a five-byte LEB128
// above `int.MaxValue` is refused instead of becoming a negative number), it is bounded before it sizes
// anything, and it is required to equal the definition's own arity — and it refuses a tag that names no
// type. The matching-layout decoder did none of that: it read both fields as bare `uint`s and allocated
// `new Type[count]` straight away, so a doctored count escaped either as a bare BCL exception out of the
// array machinery — an `OverflowException` above `int.MaxValue`, an `ArgumentException` out of
// `MakeGenericType` for a merely wrong count, both wrapped only by the reference formatter's error
// context — or, worst of all, succeeded in allocating first: 100 million declared arguments is 800 MB from
// a single byte in a file. And a doctored *tag* was worse still in kind, if not in size: a value above
// `int.MaxValue` narrowed to a negative id, matched no table, and came back as `Type`'s `null` — a
// *successful* load of a value the file does not describe, indistinguishable from a type value that
// really is null.
//
// The bytes and the wire format are identical on both paths, so which of them reads a file is decided only
// by whether its header fingerprint matches — which is why every malformed-collection test in the suite
// missed all of this: those files are hand-built with fingerprint 0, so they all take the interpreter.
//
// The files below are therefore built the other way round (the precedent is
// `HandWrittenFileWithArrayOfRank` in PolymorphicMultidimensionalArrayTests): a real save of this
// build's own schema section and layout fingerprint, with only the data section replaced by hand, so
// the CRC and the fingerprint both hold and the *generated* reader is the one that runs. The builder
// asserts the fingerprint in every file it produces, because a silent fall back to the interpreter would
// make every assertion below pass for the wrong reason.
using System;
using System.Buffers;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.MatchedTypeValues
{
    /// <summary>An <c>object</c> member, so the file can put a collection behind a <c>Type</c> value
    /// rather than in a declared member — the whole of the polymorphic grammar here.</summary>
    [CerasContext("MatchedTypeValues")]
    [CerasSerializable]
    public class MtvHolder
    {
        public object Value;
        public int After;
    }

    /// <summary>A user type, so a polymorphic slot can also carry the other kind of valid tag: a
    /// <em>manifest</em> id (128 and up) rather than a well-known one.</summary>
    [CerasContext("MatchedTypeValues")]
    [CerasSerializable]
    public class MtvItem
    {
        public int Number;
        public string Name;
    }

    public class TypeValueArgumentCountTests : TestBase
    {
        const string Ctx = "MatchedTypeValues";

        static CerasRobustSerializer New() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));

        /// <summary>How much a refused load may allocate over the control's, in bytes. A declared count
        /// that sizes an array costs eight bytes per element, so this is a factor of hundreds below the
        /// smallest count any row below declares, and orders of magnitude above what decoding this file
        /// needs (see <see cref="AllocatedBy"/>).</summary>
        const int AllocationSlack = 1024 * 1024;

        // ── The refusals ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The definition fixes how many arguments it takes, so a <c>Type</c> value naming any other
        /// number does not name a type at all — and the payload that follows it cannot be counted. On this
        /// path the answer used to be a bare <see cref="ArgumentException"/> out of <c>MakeGenericType</c>
        /// ("the number of generic arguments provided doesn't equal the arity of the generic type
        /// definition"), which names neither the definition nor either count.
        /// </summary>
        [Theory]
        [InlineData(0u)]
        [InlineData(2u)]
        public void ATypeValueNamingADefinitionWithTheWrongArgumentCountIsRefused(uint declaredCount)
        {
            var s = New();
            long controlBytes = AllocatedBy(s, FileWithArgumentCount(s, 1));
            var file = FileWithArgumentCount(s, declaredCount);

            var ex = Record.Exception(() => s.Load<MtvHolder>(file, 1));

            // Exact, on the innermost failure: that is where the reading happens, and it is the whole
            // point of the assertion — the BCL exceptions this replaced escaped from exactly here.
            var corrupt = Assert.IsType<CerasDataCorruptionException>(Innermost(ex));
            AssertNoBclExceptionAnywhere(ex);
            Assert.Contains("Corrupt System.Type payload", corrupt.Message);
            Assert.Contains("'List`1' declares", corrupt.Message);
            Assert.Contains($"declares {declaredCount} generic argument{(declaredCount == 1 ? "" : "s")}", corrupt.Message);
            Assert.Contains("takes exactly 1", corrupt.Message);

            // The count sizes an array, so it is a resource question too, and the question is asked
            // before the array exists.
            AssertWithinSlack(controlBytes, AllocatedBy(s, file));

            // The other decoder of the same grammar refuses the same bytes with the same reason, so a
            // file is not a corrupt file on one path and a readable one on the other. That agreement is
            // the point; the interpreter already had it.
            var slow = Record.Exception(() => New().Load<MtvHolder>(SlowPath(file), 1));
            Assert.IsType<CerasDataCorruptionException>(Innermost(slow));
            Assert.Contains("takes exactly 1", slow!.Message);
        }

        /// <summary>
        /// A count is a LEB128 field, so it can be any <c>uint</c> and <c>ReadLeb128</c> accepts five
        /// bytes — which is why the value is read where what it means is still known, as a type id. This
        /// used to escape as a bare <see cref="OverflowException"/> ("arithmetic operation resulted in an
        /// overflow") out of the conversion of that <c>uint</c> to an array length.
        /// </summary>
        [Fact]
        public void ATypeValueWhoseArgumentCountIsNotEvenATypeIdIsRefused()
        {
            const uint declaredCount = 4_294_967_280u;   // 0xFFFFFFF0: five LEB128 bytes, above int.MaxValue
            var s = New();
            long controlBytes = AllocatedBy(s, FileWithArgumentCount(s, 1));
            var file = FileWithArgumentCount(s, declaredCount);

            var ex = Record.Exception(() => s.Load<MtvHolder>(file, 1));

            var corrupt = Assert.IsType<CerasDataCorruptionException>(Innermost(ex));
            AssertNoBclExceptionAnywhere(ex);
            Assert.Contains("argument count", corrupt.Message);
            Assert.Contains(declaredCount.ToString(), corrupt.Message);
            Assert.Contains("not a type id", corrupt.Message);

            AssertWithinSlack(controlBytes, AllocatedBy(s, file));
        }

        /// <summary>
        /// A count within <see cref="int"/> is still a count this file cannot honour, and it has to be
        /// refused before anything is allocated for it: <c>new Type[count]</c> is eight bytes per declared
        /// argument, so this row used to allocate 800 MB and only then complain about something else
        /// entirely (a null argument, once the reader ran off the end of the payload). Measured here,
        /// because "it threw a corruption error" is not the property — refusing the field <em>before</em>
        /// sizing anything with it is.
        /// </summary>
        [Fact]
        public void ATypeValueDeclaringAHugeArgumentCountIsRefusedBeforeTheArrayIsAllocated()
        {
            const uint declaredCount = 100_000_000u;   // 800 MB of Type[] if it is believed
            var s = New();
            long controlBytes = AllocatedBy(s, FileWithArgumentCount(s, 1));
            var file = FileWithArgumentCount(s, declaredCount);

            var ex = Record.Exception(() => s.Load<MtvHolder>(file, 1));

            var corrupt = Assert.IsType<CerasDataCorruptionException>(Innermost(ex));
            AssertNoBclExceptionAnywhere(ex);
            Assert.Contains("'List`1' declares 100000000 generic arguments", corrupt.Message);
            Assert.Contains("takes exactly 1", corrupt.Message);

            // The load-bearing assertion of this file: 800 MB is not a rounding error, so a pass here is
            // the only evidence the guard fired before the allocation rather than after it.
            AssertWithinSlack(controlBytes, AllocatedBy(s, file));
        }

        // ── The tag: the other field of the same grammar, and the same silent wrong value ─────────

        /// <summary>
        /// The leading tag is a LEB128 field too, so it can be any <c>uint</c> — and it used to be read as
        /// one. Above <see cref="int.MaxValue"/> that narrows to a <em>negative</em> id, which matches no
        /// well-known type and no manifest entry, and the value then fell out of the reader as
        /// <c>Type</c>'s <c>null</c>: no error, a successful load, and a value that is not what the file
        /// says — indistinguishable from a type value that really is null, which the format allows. Refused
        /// instead, at the field and with its own name, so the diagnosis is about the tag rather than
        /// about a value nobody can account for.
        /// </summary>
        [Fact]
        public void ATypeValueTagThatIsNotEvenATypeIdIsRefused()
        {
            const uint tag = 4_294_967_280u;   // 0xFFFFFFF0: five LEB128 bytes, above int.MaxValue
            var s = New();

            var ex = Record.Exception(() => s.Load<MtvHolder>(FileWithTypeValue(s, tag, 1), 1));

            // "It loaded and the value was null" is what this replaces, so the assertion is that nothing
            // was loaded at all, and that the diagnosis names the field rather than the value.
            var corrupt = Assert.IsType<CerasDataCorruptionException>(Innermost(ex));
            AssertNoBclExceptionAnywhere(ex);
            Assert.Contains("Corrupt data", corrupt.Message);
            Assert.Contains("a type value's tag", corrupt.Message);
            Assert.Contains(tag.ToString(), corrupt.Message);
            Assert.Contains("not a type id", corrupt.Message);

            // The interpreter reads this field the same way, so the two readers of this grammar refuse
            // these same bytes rather than one of them loading a null. (It names the field after the
            // position it reads it in, so the cross-check is the value both refusals quote, not the
            // wording around it.)
            var slow = Record.Exception(() => New().Load<MtvHolder>(SlowPath(FileWithTypeValue(s, tag, 1)), 1));
            Assert.IsType<CerasDataCorruptionException>(Innermost(slow));
            Assert.Contains(tag.ToString(), slow!.Message);
        }

        /// <summary>
        /// The same silent null one step further down the id space: a tag that <em>is</em> a number and
        /// reads as a perfectly ordinary one, but names nothing — a reserved well-known id (43-127, no
        /// type assigned to it) or a manifest-shaped id this context never handed out. No Ceras writer
        /// emits either, and a type the application has removed is <em>not</em> this case: that is
        /// answered by <c>BuildIdToType</c> on the migration path, where the ids come from the file's own
        /// section rather than from this build's manifest. A tag that describes no type has nothing to
        /// hand back, and a null here is a value the user can legitimately have — which is exactly why
        /// it must not be what an undescribed tag decodes to.
        /// </summary>
        [Theory]
        [InlineData(100u)]     // a reserved well-known id: in the well-known range, no type assigned
        [InlineData(4242u)]    // a manifest-shaped id this context never handed out
        public void ATypeValueTagThatNamesNoTypeIsRefusedRatherThanDecodedAsNull(uint tag)
        {
            var s = New();

            var ex = Record.Exception(() => s.Load<MtvHolder>(FileWithTypeValue(s, tag, 1), 1));

            var corrupt = Assert.IsType<CerasDataCorruptionException>(Innermost(ex));
            AssertNoBclExceptionAnywhere(ex);
            Assert.Contains("Corrupt System.Type payload", corrupt.Message);
            Assert.Contains($"names type id {tag}", corrupt.Message);
            Assert.Contains("No Ceras writer emits", corrupt.Message);

            // Stated rather than left implicit: a null here would have been a *successful* load, so
            // "no value comes back at all" is the whole assertion.
            Assert.Contains("Type's null", corrupt.Message);
        }

        // ── The controls ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every kind of tag a real writer emits, decoded on the very path the refusals above are about: a
        /// well-known scalar, a manifest id (a user class), a well-known generic definition with its own
        /// argument, and — through the hand-written builder — a type value whose tag and count are both
        /// the ones a writer emits. A read that is too eager to refuse would take one of these round trips
        /// with it, which is the other way this rule can be got wrong.
        /// </summary>
        [Theory]
        [InlineData("int")]          // a scalar well-known tag
        [InlineData("string")]       // another one, with a different payload width
        [InlineData("user class")]   // a manifest tag (128 and up), resolved through the baked id map
        [InlineData("list")]         // a well-known generic definition, plus its one argument
        public void AValidTypeValueTagStillDecodes(string kind)
        {
            var s = New();
            object value = kind switch
            {
                "int" => 42,
                "string" => "text",
                "user class" => new MtvItem { Number = 7, Name = "x" },
                _ => new List<int> { 11, 12 },
            };

            var loaded = s.Load<MtvHolder>(s.Save(new MtvHolder { Value = value, After = 4242 }, 1), 1);

            Assert.Equal(4242, loaded.After);
            if (kind == "user class")
            {
                var item = Assert.IsType<MtvItem>(loaded.Value);
                Assert.Equal(7, item.Number);
                Assert.Equal("x", item.Name);
            }
            else
            {
                Assert.Equal(value, loaded.Value);
            }
        }

        /// <summary>
        /// The same builder with the definition's own argument count, decoded on the path the refusals
        /// above are about: the elements come back, the trailing field holds, and the file's fingerprint
        /// is this context's own — so the generated reader is the one that ran, and the refusals above
        /// cannot be satisfied by a builder that simply produces unreadable files.
        /// </summary>
        [Fact]
        public void TheSameFileWithTheDefinitionsOwnArgumentCountStillDecodes()
        {
            var s = New();

            var loaded = s.Load<MtvHolder>(FileWithArgumentCount(s, 1), 1);

            Assert.Equal(new[] { 11, 12 }, Assert.IsType<List<int>>(loaded.Value));
            Assert.Equal(4242, loaded.After);
        }

        /// <summary>
        /// The premise the refusals rest on, said in one place: the file is a same-build save, so the
        /// <em>generated</em> reader is the one that decodes it. The interpreter is exercised by every
        /// other malformed-collection test in the suite, so a file that did not select the generated
        /// reader would only be re-testing the door that was never broken. (The builder asserts this for
        /// every file it builds; this test is where it is spelled out.)
        /// </summary>
        [Fact]
        public void TheFileSelectsTheMatchingLayoutReader()
        {
            var s = New();
            var file = FileWithArgumentCount(s, 1);
            RobustHeader.Read(file, out _, out _, out _, out _, out _, out ulong fileFingerprint);

            Assert.True(s.RobustCompatibility.IsFinal);
            Assert.Equal(s.RobustCompatibility.LayoutFingerprint, fileFingerprint);

            // And the file is not one only this reader accepts: the same bytes decode on the interpreter
            // too, so nothing above is decided by a file a second reader would have thrown away. The
            // refusals are about the bytes, and this is the reader that used not to refuse them.
            Assert.Null(Record.Exception(() => New().Load<MtvHolder>(SlowPath(file), 1)));
        }

        // ── The hand-written file ─────────────────────────────────────────────────────────────

        /// <summary>
        /// A real save of this build's own schema section, and therefore of this build's own layout
        /// fingerprint, with the data section replaced by hand-written bytes: <c>root id | root marker |
        /// After | Value marker | tag | argument count | Int32 | the collection's own payload</c>. The
        /// header is re-finalized, so the CRC covers the replacement and the fingerprint is the one the
        /// fast path selects; the root's reference marker is read out of the real save rather than
        /// assumed, since that is the one byte whose value the layout fixes.
        /// <para>
        /// The two fields are separate parameters because they are two separate defects: a
        /// <c>List</c> definition with a count that is not its arity, and a tag that is not a type id at
        /// all. The bytes after them are the valid ones, so what each test refuses is exactly the field it
        /// doctored.
        /// </para>
        /// </summary>
        static byte[] FileWithTypeValue(CerasRobustSerializer s, uint tag, uint argumentCount)
        {
            var good = s.Save(new MtvHolder { Value = new List<int> { 11, 12 }, After = 4242 }, 1);
            RobustHeader.Read(good, out _, out _, out long schemaStringOffset, out long dataSectionOffset, out _, out ulong fingerprint);

            // The premise of every test in this file, asserted here so that it is asserted in all of them
            // and not only in the one that talks about it: this file has to select the generated reader.
            // Without it, a file that fell back to the interpreter would be refused by the door that was
            // never broken, and every assertion below would pass for the wrong reason.
            Assert.True(s.RobustCompatibility.IsFinal);
            Assert.Equal(s.RobustCompatibility.LayoutFingerprint, fingerprint);

            int rootIdOffset = (int)dataSectionOffset;
            int rootId = (int)SchemaSection.ReadLeb128(good, ref rootIdOffset);
            Assert.Equal(Protocol.RefNewValueSameType + Protocol.RefBias, good[rootIdOffset]);

            var payload = new ArrayBufferWriter<byte>();
            SchemaSection.WriteLeb128(payload, (uint)rootId);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias); // the root
            SerializerBinary.WriteInt32Fixed(payload, 4242);                                         // After
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValue, Protocol.RefBias);         // Value: not declared as object
            SchemaSection.WriteLeb128(payload, tag);                                                  // the definition
            SchemaSection.WriteLeb128(payload, argumentCount);                                        // ... with this many arguments
            SchemaSection.WriteLeb128(payload, (uint)WellKnownTypes.Int32);                           // Int32, the element type
            SerializerBinary.WriteUInt32Bias(payload, 2, Protocol.CollectionBias);                   // the collection's own payload
            SerializerBinary.WriteInt32Fixed(payload, 11);
            SerializerBinary.WriteInt32Fixed(payload, 12);

            var data = payload.WrittenSpan.ToArray();
            var buffer = new byte[(int)dataSectionOffset + data.Length + 16];
            good.AsSpan(0, (int)dataSectionOffset).CopyTo(buffer);   // header + schema + schema strings
            data.CopyTo(buffer, (int)dataSectionOffset);
            int offset = (int)dataSectionOffset + data.Length;

            int stringTableStart = offset;
            new StringInternTable().Flush(ref buffer, ref offset, StringTableCompression.Disabled);
            RobustHeader.FinalizeHeader(buffer, offset, (byte)StringTableCompression.Disabled,
                schemaStringOffset, dataSectionOffset, stringTableStart, fingerprint);
            return buffer[..offset];
        }

        /// <summary>The same file with the definition a writer emits (<c>List</c>) and only the argument
        /// count doctored.</summary>
        static byte[] FileWithArgumentCount(CerasRobustSerializer s, uint argumentCount)
            => FileWithTypeValue(s, (uint)WellKnownTypes.List, argumentCount);

        // ── Assertions and measurement ────────────────────────────────────────────────────────

        /// <summary>
        /// The failure a caller has to act on. A refusal raised while reading a member of the root object
        /// is wrapped by the enclosing object's error context (<see cref="Formatters.ReferenceFormatter{T}"/>
        /// adds the declaring type), so the reading that failed is the innermost one — which is exactly
        /// where the BCL exceptions used to escape from.
        /// </summary>
        static Exception Innermost(Exception e)
        {
            while (e?.InnerException != null) e = e.InnerException;
            return e!;
        }

        /// <summary>
        /// No bare BCL exception anywhere in the chain. This is the defect in one line: the two doors of
        /// this grammar have to report a corrupt file as Ceras says so, and neither an
        /// <see cref="ArgumentException"/> out of <c>MakeGenericType</c> nor an
        /// <see cref="OverflowException"/> out of an array length says which field of which file was
        /// wrong.
        /// </summary>
        static void AssertNoBclExceptionAnywhere(Exception e)
        {
            for (var current = e; current != null; current = current.InnerException)
                Assert.False(current is ArgumentException or OverflowException or OutOfMemoryException,
                    $"the load escaped a bare BCL exception: {current.GetType().FullName}: {current.Message}");
        }

        /// <summary>
        /// What <paramref name="file"/> costs to load, in bytes, measured twice so the one-off work (the
        /// context's bake, the per-serializer caches) is not charged to the number under test: the first
        /// load is a warm-up and only the second is measured.
        /// </summary>
        static long AllocatedBy(CerasRobustSerializer s, byte[] file)
        {
            Record.Exception(() => s.Load<MtvHolder>(file, 1));
            long before = GC.GetAllocatedBytesForCurrentThread();
            Record.Exception(() => s.Load<MtvHolder>(file, 1));
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        static void AssertWithinSlack(long controlBytes, long actualBytes)
            => Assert.True(actualBytes <= controlBytes + AllocationSlack,
                $"loading the file allocated {actualBytes} bytes, more than {AllocationSlack} above the {controlBytes} that loading the " +
                "same file with a sane argument count costs: a declared count is sizing an array, which the reader has to refuse before it does.");
    }
}
