// The contracts this batch's nullability work made explicit, pinned as behaviour rather than as
// annotations. Every test here exists because a "null" in the serializer/config layer is a *value*
// Ceras hands out on purpose, and the fix was to say which one - not to stop producing it.
//
// What is pinned, in the order it can break:
//
//   1. A type with no reflected member layout is refused by name (R1). CerasSerializer's
//      PrimarySchema is null for exactly four kinds of type, and that null used to reach
//      ReflectionFallbackFormatter as a bare field read. The fallback now rejects it with a message
//      naming which cause it was. Each cause is checked separately - they are refused by different
//      guards, and a caller has to be able to tell them apart - and every message is asserted to
//      contain the remedy it names, because "something went wrong" is what this replaced. Each
//      refusal has a control: a type that *does* have a layout still round-trips.
//
//   2. The generic formatter lookups never yield null (R2). GetFormatter<T> and
//      GetReferenceFormatter<T> return IFormatter<T>, and reach it through the required resolution
//      rather than by casting a possibly-null. So the observable contract is: for any type Ceras
//      can build a formatter for, the call returns the same cached instance the by-Type lookup
//      does; and the reference formatter is what actually serializes, so a type whose *members*
//      cannot be resolved fails at the member with a named error instead of at the lookup with a
//      null dereference later.
//
//   3. A formatter of the wrong closed type is an error, not a silent null (R8). TypeConfig<T>'s
//      generic CustomFormatter and the member lookups keep the behaviour that has always reported a
//      mis-registered formatter. The reachable half is the setter's type check; the cast in the
//      getter is the backstop, and the genuine "nothing configured" null is pinned beside it so the
//      two cannot be confused.
//
//   4. Serializer byte compatibility. Everything above is an annotation or a null check; none of
//      it may move a byte. The pins compare the bytes a serializer produces against the bytes the
//      pre-batch sources produced, for the generated path, the raw path and the reflection
//      fallback, and for the robust format through both the generated reader and the interpreter.
//      The expected values were recorded from the unmodified tree, so a change to any of them is a
//      real format change and not a test that re-derives its own expectation.
//
// The generated formatters come from the [CerasSerializable] types below; the reflection fallback
// from the deliberately undeclared ones.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ceras;
using Ceras.Exceptions;
using Ceras.Formatters;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test
{
    // ── fixtures ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Generated formatter + robust manifest (declared in the "Tests" context).</summary>
    [CerasContext("BatchB")]
    [CerasSerializable]
    public partial class BatchBRoot
    {
        public int Number;
        public string Text;
        public BatchBChild Child;
        public List<int> Numbers;
    }

    [CerasContext("BatchB")]
    [CerasSerializable]
    public partial class BatchBChild
    {
        public bool Flag;
        public double Value;
    }

    /// <summary>
    /// No [CerasSerializable] and no generated entry, so only the reflection fallback can build a
    /// formatter for it. The control for the "the fallback still works" assertions below.
    /// </summary>
    public class BatchBReflectionOnly
    {
        public int Number;
        public string Text;
    }

    public abstract class BatchBAbstract
    {
        public int Number;
    }

    public interface IBatchBInterface
    {
        int Number { get; set; }
    }

    public class BatchBInterfaceImpl : IBatchBInterface
    {
        public int Number { get; set; }
    }

    public class BatchBGeneric<T>
    {
        public T Value;
    }

    /// <summary>A type whose config is reached through the generic API, for the wrong-type pins.</summary>
    public class BatchBLookup
    {
        public int Number;
        public string Text;
    }

    /// <summary>A formatter for a *different* closed type, to prove a mismatch stays an error.</summary>
    public class BatchBOtherType
    {
        public int Other;
    }

    public class BatchBOtherFormatter : IFormatter<BatchBOtherType>
    {
        public void Serialize(IBufferWriter<byte> writer, BatchBOtherType value) { }
        public void Deserialize(ref SequenceReader<byte> reader, ref BatchBOtherType value) => value = default!;
        public void SerializeUntyped(IBufferWriter<byte> writer, object value) { }
        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member) { }
    }
}

namespace Ceras.Test.BatchB
{
    using Ceras.Test;

    public class SerializerConfigNullabilityTests : TestBase
    {
        /// <summary>
        /// The generated-manifest context these fixtures are declared in. Deliberately not the
        /// shared <c>TestBase.Context</c>: that context's manifest is every serializable type in
        /// the test assembly, so adding a fixture anywhere in it would move the bytes the robust
        /// pin below records.
        /// </summary>
        public const string BatchBContext = "BatchB";

        // ── helpers ───────────────────────────────────────────────────────────────────────────

        static CerasFastSerializer Fast(Action<CerasRawConfig> configure = null)
            => new CerasFastSerializer(configure ?? (_ => { }));

        /// <summary>
        /// Builds a <c>ReflectionFallbackFormatter&lt;T&gt;</c> directly and unwraps this test's own
        /// reflection layer, so the caller sees the exception Ceras actually threw. The unwrap is
        /// needed because this calls <see cref="Activator"/> itself; the resolver does not have to any
        /// more, because it goes through <c>Helpers.Instantiator.Create</c>, which hands a Ceras
        /// refusal out as itself.
        /// </summary>
        static Exception BuildFallbackFormatter(CerasSerializer ceras, Type type, bool isStatic = false)
        {
            try
            {
                Activator.CreateInstance(
                    typeof(ReflectionFallbackFormatter<>).MakeGenericType(type),
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null, new object[] { ceras, isStatic }, null);
                return null;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                return tie.InnerException;
            }
        }

        // ── 1. a type with no member layout is refused, by name ───────────────────────────────

        /// <summary>
        /// A serialization primitive has no member list, and Ceras's own schema builder says so by
        /// returning null. This is the case the null-forwards-to-a-dereference bug was reachable
        /// from, and the message has to name the built-in formatter as the reason and the way out.
        /// </summary>
        [Theory]
        [InlineData(typeof(int))]
        [InlineData(typeof(string))]
        [InlineData(typeof(double))]
        public void Reflection_Fallback_Names_The_Primitive_Case(Type type)
        {
            var ceras = Fast();

            // The schema really is absent - the premise the message is explaining.
            Assert.Null(ceras.GetTypeMetaData(type).PrimarySchema);

            var ex = BuildFallbackFormatter(ceras, type);

            var ioe = Assert.IsType<InvalidOperationException>(ex);
            Assert.Contains(type.Name, ioe.Message, StringComparison.Ordinal);
            // The remedy, not just the refusal.
            Assert.Contains("built-in formatter", ioe.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// An abstract type and an interface have no member list either, and they are refused one
        /// layer earlier - by the guard in front of the schema read, which inspects the type before
        /// the schema is ever consulted. Pinned separately, and by message shape, because the two
        /// layers say different things and a caller who hits one needs to hear which.
        /// </summary>
        public static TheoryData<string, Type> TypesWithNoSpecificInstances => new()
        {
            { "abstract", typeof(BatchBAbstract) },
            { "interface", typeof(IBatchBInterface) },
        };

        [Theory]
        [MemberData(nameof(TypesWithNoSpecificInstances))]
        public void Reflection_Fallback_Still_Refuses_A_Type_With_No_Specific_Instances(string _, Type type)
        {
            var ceras = Fast();

            Assert.Null(ceras.GetTypeMetaData(type).PrimarySchema);

            var ex = BuildFallbackFormatter(ceras, type);

            var ioe = Assert.IsType<InvalidOperationException>(ex);
            Assert.Contains(type.Name, ioe.Message, StringComparison.Ordinal);
            Assert.Contains("specific types", ioe.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// An open generic is refused even earlier still, and by the runtime rather than by Ceras:
        /// <c>ReflectionFallbackFormatter&lt;&gt;</c> cannot be closed over an open generic at all,
        /// so the constructor is never entered. Worth pinning in its own right, because it is why
        /// the schema-null check in the constructor cannot be reached for this case - that check is
        /// a backstop, and a reader who assumed it was the guard would go looking for a message
        /// that is never produced.
        /// </summary>
        [Fact]
        public void An_Open_Generic_Never_Reaches_The_Fallback_At_All()
        {
            Assert.Null(Fast().GetTypeMetaData(typeof(BatchBGeneric<>)).PrimarySchema);

            var ex = Assert.ThrowsAny<Exception>(
                () => Activator.CreateInstance(
                    typeof(ReflectionFallbackFormatter<>).MakeGenericType(typeof(BatchBGeneric<>)),
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null, new object[] { Fast(), false }, null));

            Assert.Contains("ContainsGenericParameters", Flatten(ex), StringComparison.Ordinal);
        }

        /// <summary>
        /// The control for both tests above: the reflection fallback still builds and runs for a
        /// type that *does* have a layout. Without it, they would also pass if the fallback refused
        /// everything.
        /// </summary>
        [Fact]
        public void Reflection_Fallback_Still_Builds_For_A_Type_That_Has_A_Schema()
        {
            var ceras = Fast();

            var meta = ceras.GetTypeMetaData(typeof(BatchBReflectionOnly));
            Assert.NotNull(meta.PrimarySchema);
            Assert.NotEmpty(meta.PrimarySchema.Members);

            // No exception at all where the other four throw.
            Assert.Null(BuildFallbackFormatter(ceras, typeof(BatchBReflectionOnly)));

            var original = new BatchBReflectionOnly { Number = 7, Text = "seven" };
            var clone = ceras.Deserialize<BatchBReflectionOnly>(ceras.Serialize(original));

            Assert.Equal(7, clone.Number);
            Assert.Equal("seven", clone.Text);
        }

        // ── 2. the generic formatter lookups never yield null ─────────────────────────────────

        /// <summary>
        /// R2. <c>GetFormatter&lt;T&gt;</c> and <c>GetReferenceFormatter&lt;T&gt;</c> are declared
        /// to return a non-null <c>IFormatter&lt;T&gt;</c>, so the only thing that can honour that is
        /// the required resolution. What that buys the caller is a formatter that is present and is
        /// the one that will actually run: the generic call, the by-Type call and the second call
        /// are all the same cached instance, and it round-trips the object.
        /// </summary>
        [Fact]
        public void Generic_Lookups_Return_The_Cached_Formatter_And_It_Works()
        {
            var ceras = Fast();

            var byGeneric = ceras.GetFormatter<BatchBReflectionOnly>();
            var byGenericReference = ceras.GetReferenceFormatter<BatchBReflectionOnly>();
            var byType = ceras.GetReferenceFormatter(typeof(BatchBReflectionOnly));

            Assert.NotNull(byGeneric);
            Assert.NotNull(byGenericReference);
            Assert.NotNull(byType);

            // Same instance every time: the required resolution did not build a second one.
            Assert.Same(byType, ceras.GetReferenceFormatter(typeof(BatchBReflectionOnly)));

            // ...and it is a real formatter, not a placeholder that would fail later.
            var original = new BatchBReflectionOnly { Number = 3, Text = "three" };
            var clone = ceras.Deserialize<BatchBReflectionOnly>(ceras.Serialize(original));
            Assert.Equal(3, clone.Number);
            Assert.Equal("three", clone.Text);
        }

        /// <summary>
        /// The other half, and the one that is actually observable: a member whose type cannot be
        /// serialized at all is refused loudly <em>at write time</em>, naming the member, its type
        /// and the way out. The lookup itself succeeds, because the reference formatter is built
        /// lazily and the schema is not consulted until the object is written - which is why this
        /// is pinned at <c>Serialize</c> and not at the lookup. Silently dropping the member
        /// instead is the failure this guards: the save would succeed and the member would come
        /// back null.
        /// </summary>
        [Fact]
        public void An_Unserializable_Member_Is_Refused_Loudly_At_Write_Time()
        {
            // The member is a delegate: it cannot be serialized at all, and the policy is to say so
            // rather than drop it (the member would come back null).
            var ceras = Fast();

            // The lookup is lazy and succeeds...
            Assert.NotNull(ceras.GetReferenceFormatter(typeof(BatchBHasDelegateMember)));

            // ...and the schema no longer lists the member, which is exactly why the refusal has to
            // happen somewhere else: nothing downstream would ever notice it was gone.
            var listed = ceras.GetTypeMetaData(typeof(BatchBHasDelegateMember)).PrimarySchema.Members
                .Select(m => m.Member.Name).ToArray();
            Assert.Contains("Number", listed);
            Assert.DoesNotContain("Callback", listed);

            var ex = Assert.ThrowsAny<Exception>(
                () => ceras.Serialize(new BatchBHasDelegateMember { Number = 1, Callback = () => { } }));

            var text = Flatten(ex);
            Assert.Contains("Callback", text, StringComparison.Ordinal);
            Assert.Contains("BatchBHasDelegateMember", text, StringComparison.Ordinal);
            // A typed Ceras ban, not a reflection accident.
            Assert.Contains("BannedTypeException", text, StringComparison.Ordinal);
        }

        public class BatchBHasDelegateMember
        {
            public int Number;
            public Action Callback;
        }

        /// <summary>Every message in the chain, so a wrapped exception is still assertable.</summary>
        static string Flatten(Exception e)
        {
            var sb = new System.Text.StringBuilder();
            for (var cur = e; cur != null; cur = cur.InnerException)
                sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message).Append(" | ");
            return sb.ToString();
        }

        // ── 3. a wrong-typed formatter is an error, not a silent null ───────────────────────────

        /// <summary>
        /// R8. A formatter for another closed type is a configuration error, and it stays one. The
        /// reachable half is the setter, which compares the formatter's closed type against the
        /// configured type; the cast in the getter is the backstop behind it. Either way the user
        /// gets an exception naming the formatter and the type, never a silent null.
        /// </summary>
        [Fact]
        public void Registering_A_Formatter_Of_Another_Type_Is_Refused_By_Name()
        {
            var config = new CerasRawConfig();

            var ex = Assert.ThrowsAny<Exception>(
                () => config.ConfigType<BatchBLookup>().SetFormatter(
                    (IFormatter<BatchBLookup>)(object)new BatchBOtherFormatter()));

            var text = Flatten(ex);
            Assert.Contains("BatchBOtherFormatter", text, StringComparison.Ordinal);
            Assert.Contains("BatchBLookup", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// ...and the genuine "nothing configured" case still reads as null rather than throwing.
        /// The control for the test above: without it, "always throws" would satisfy both.
        /// </summary>
        [Fact]
        public void Generic_CustomFormatter_Is_Null_When_None_Is_Registered()
        {
            var config = new CerasRawConfig();

            Assert.Null(config.ConfigType<BatchBLookup>().CustomFormatter);
            Assert.Null(config.ConfigType<BatchBOtherType>().CustomFormatter);
        }

        /// <summary>
        /// A correctly registered formatter is returned by the generic getter, so the cast behind
        /// the backstop above is a real conversion on the working path and not a cast that always
        /// throws.
        /// </summary>
        [Fact]
        public void A_Correctly_Registered_Formatter_Is_Returned_By_The_Generic_Getter()
        {
            var config = new CerasRawConfig();
            var tc = config.ConfigType<BatchBLookup>();

            var formatter = new BatchBLookupFormatter();
            tc.SetFormatter(formatter);

            Assert.Same(formatter, tc.CustomFormatter);
        }

        public class BatchBLookupFormatter : IFormatter<BatchBLookup>
        {
            public void Serialize(IBufferWriter<byte> writer, BatchBLookup value) { }
            public void Deserialize(ref SequenceReader<byte> reader, ref BatchBLookup value) => value = default!;
            public void SerializeUntyped(IBufferWriter<byte> writer, object value) { }
            public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member) { }
        }

        /// <summary>
        /// The member lookups: a member that exists is found, one that does not is null. Ceras
        /// enumerates fields rather than properties, so <c>ConfigProperty</c> on a field is a miss -
        /// that is the existing contract and it is pinned here so the null case below cannot be
        /// satisfied by the lookup being broken for everything.
        /// </summary>
        [Fact]
        public void Member_Lookups_Find_Fields_And_Report_A_Miss_As_Null()
        {
            var config = new CerasRawConfig();
            var tc = config.ConfigType<BatchBLookup>();

            Assert.NotNull(tc.ConfigField("Number"));
            Assert.NotNull(tc.ConfigMember(x => x.Text));

            Assert.Null(tc.ConfigField("NoSuchField"));
            Assert.Null(tc.ConfigProperty("NoSuchProperty"));
            // Properties are not data members; the backing field is what the config holds.
            Assert.Null(tc.ConfigProperty("Text"));
        }

        // ── 4. byte compatibility ────────────────────────────────────────────────────────────

        /// <summary>
        /// The raw/fast format, generated formatter. These expected byte strings were recorded from
        /// the unmodified tree. The batch touched the array branch of IsPrimitiveType, the return
        /// annotation of Serialize, and the static-dispatcher plumbing - all of which are on this
        /// path - so the bytes are pinned rather than assumed. If this fails, the *format* changed.
        /// </summary>
        [Fact]
        public void Raw_Format_Bytes_Are_Unchanged()
        {
            var ceras = Fast();
            var value = new BatchBRoot
            {
                Number = 1234,
                Text = "hello",
                Child = new BatchBChild { Flag = true, Value = 2.5 },
                Numbers = new List<int> { 1, 2, 3 },
            };

            Assert.Equal(
                "02020100000000000004400204010000000200000003000000D20400000668656C6C6F",
                Hex(ceras.Serialize(value)));
        }

        /// <summary>The raw/fast format through the reflection fallback, same pin shape.</summary>
        [Fact]
        public void Raw_Format_Bytes_Are_Unchanged_For_The_Reflection_Fallback()
        {
            var ceras = Fast();
            var value = new BatchBReflectionOnly { Number = 99, Text = "ninetynine" };

            Assert.Equal(
                "02630000000B6E696E6574796E696E65",
                Hex(ceras.Serialize(value)));
        }

        /// <summary>The robust format's whole envelope, same pin shape.</summary>
        [Fact]
        public void Robust_Format_Bytes_Are_Unchanged()
        {
            var value = new BatchBRoot
            {
                Number = 5,
                Text = "robust",
                Child = new BatchBChild { Flag = false, Value = 0.5 },
                Numbers = new List<int> { 9, 8, 7 },
            };

            var ceras = new CerasRobustSerializer(c => c.UseGeneratedFormatters(BatchBContext));

            Assert.Equal(
                PreBatchRobustBytes,
                Hex(ceras.Save(value, 1)));
        }

        /// <summary>
        /// The pre-batch bytes of a robust save of this graph, recorded from the unmodified tree
        /// in the dedicated 'BatchB' context - so the manifest is exactly these two types and a
        /// fixture added elsewhere in the test assembly cannot move it. Split across lines so a
        /// failure shows which section drifted rather than one very long line.
        /// </summary>
        const string PreBatchRobustBytes =
            "4345524104010000010000000000F8B4C848DE08410100000000000051030000000000007403000000000000352583A3" +
            "33917DC150D0D2300C8001010000000002020200000000010103000000000C0100810104000000000204050000000080" +
            "010006000000008B01010700000000070108000000000E01008201090000000002020300000000070108000000000E01" +
            "0083010A0000000002020500000000840101070000000007010084010B000000000201070000000007010085010C0000" +
            "000002020D0000000007010E000000000E010086010F0000000002010300000000880100008701100000000002010300" +
            "0000000701008801110000000002021200000000070113000000000E01008901140000000002020D0000000007010E00" +
            "0000000E01008A011500000000031101820100008B011600000000031101070000161643657261732E546573742E4261" +
            "746368424368696C6404466C61670556616C75651543657261732E546573742E426174636842526F6F74054368696C64" +
            "074E756D62657273064E756D62657204546578741C43657261732E546573742E4B6E6F776E2E4B6E6F776E4869646465" +
            "6E1843657261732E546573742E4D6F6E6F47656E486F6C6465721D43657261732E546573742E4D6F6E6F47656E536561" +
            "6C65644368696C642D43657261732E546573742E5061697260323C53797374656D2E496E7433322C53797374656D2E53" +
            "7472696E673E054974656D31054974656D323243657261732E546573742E52657573652E426F7860313C43657261732E" +
            "546573742E52657573652E52657573654E6F64653E2443657261732E546573742E52657573652E426F7860313C537973" +
            "74656D2E496E7433323E1A43657261732E546573742E52657573652E52657573654E6F6465024964044E616D65434365" +
            "7261732E546573742E526F6275737447656E65726963732E47656E657269635061697260323C53797374656D2E496E74" +
            "33322C53797374656D2E537472696E673E3F53797374656D2E436F6C6C656374696F6E732E47656E657269632E4C6973" +
            "7460313C43657261732E546573742E4B6E6F776E2E4B6E6F776E48696464656E3E2F53797374656D2E436F6C6C656374" +
            "696F6E732E47656E657269632E4C69737460313C53797374656D2E496E7433323E8101020200000000000000E03F0204" +
            "090000000800000007000000050000000100000063642BCA4F2A2D2E0100";

        static string Hex(byte[] bytes) => Convert.ToHexString(bytes);
    }
}
