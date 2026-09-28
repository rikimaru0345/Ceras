// A monomorphic member of the *reflected* schema is one whose declared type Ceras knows cannot
// vary at run time: a value type, or a sealed class. The generated formatter treats such a member
// as "no type id" — and the reflection fallback has to produce the same bytes, which is the whole
// reason ReflectionFallbackFormatter carries an IsMonomorphic flag at all.
//
// The bug this file pins: "no type id" was read as "no reference wrapper", so the fallback bound the
// *specific* formatter for a monomorphic member. For a value type that is the same object the
// reference wrapper would have delegated to, so nothing showed. For a sealed *class* it is not: the
// generated code binds ReferenceFormatter_KnownSealedType<T> there, which still writes the null /
// new-value marker, and the specific formatter has no concept of a null member at all. Handed null it
// tried to read the members off a null instance, and FieldInfo.GetValue(null) threw
// TargetException("Non-static field requires a target") — so a null sealed member could not be
// serialized at all, and a non-null one was written without the marker the reader expects.
//
// So this file pins three things, in order:
//   * a null sealed-class member round-trips through the reflection fallback (the crash);
//   * the fallback's bytes for a sealed-class member are the *generated* bytes, for both null and
//     non-null — the byte-identity claim the code makes, checked against the generated formatter
//     rather than against a recorded literal, so it cannot rot;
//   * the robust format still round-trips a null sealed-class member on both the matching
//     (generated) reader and the migration interpreter, i.e. the fix stayed where it belonged.
//
// The non-sealed control runs through the same assertions: a non-sealed member was already on the
// reference-wrapper path, so it must be unchanged.
using System;
using System.Buffers;
using Ceras;
using Ceras.Exceptions;
using Ceras.Formatters;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test
{
    /// <summary>Declared, so the generator emits a formatter: the byte-identity reference.</summary>
    [CerasContext("BatchB")]
    [CerasSerializable]
    public partial class MonoGenHolder
    {
        public int Number;
        public MonoGenSealedChild Child;
    }

    [CerasContext("BatchB")]
    [CerasSerializable]
    public sealed partial class MonoGenSealedChild
    {
        public int Number;
    }

    /// <summary>Same shape, undeclared: only the reflection fallback can build a formatter.</summary>
    public class MonoReflHolder
    {
        public int Number;
        public MonoReflSealedChild Child;
    }

    public sealed class MonoReflSealedChild
    {
        public int Number;
    }

    /// <summary>Not sealed: already on the reference-wrapper path, and must stay there.</summary>
    public class MonoReflOpenChild
    {
        public int Number;
    }

    public class MonoReflOpenHolder
    {
        public int Number;
        public MonoReflOpenChild Child;
    }
}

namespace Ceras.Test.BatchB
{
    using Ceras.Test;

    public class MonomorphicSealedMemberTests : TestBase
    {
        /// <summary>
        /// The fixtures above are declared in a context of their own, so a
        /// [CerasSerializable] type added anywhere else in the test assembly cannot move the
        /// bytes these tests compare.
        /// </summary>
        public const string BatchBContext = "BatchB";

        static CerasFastSerializer Fast() => new CerasFastSerializer(c => { });

        static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

        // ── the crash ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A sealed-class member is monomorphic, so the member's formatter used to be the
        /// *specific* one, and a null member was handed straight to it. It has no null handling, so
        /// it read the members off a null instance and reflection refused. This is the regression.
        /// </summary>
        [Fact]
        public void A_Null_Sealed_Member_Serializes_And_Round_Trips()
        {
            var ceras = Fast();
            var original = new MonoReflHolder { Number = 1, Child = null };

            var bytes = ceras.Serialize(original);
            var clone = ceras.Deserialize<MonoReflHolder>(bytes);

            Assert.Equal(1, clone.Number);
            Assert.Null(clone.Child);

            // ...and a null member is encoded, not dropped: a non-null one is a longer payload,
            // because the marker that distinguishes them is exactly what the fix restored.
            var withChild = ceras.Serialize(new MonoReflHolder { Number = 1, Child = new MonoReflSealedChild { Number = 2 } });
            Assert.True(bytes.Length < withChild.Length, $"null payload ({bytes.Length}B) must be shorter than the present one ({withChild.Length}B)");
        }

        /// <summary>
        /// The control. A non-sealed member was already written through the reference wrapper, so it
        /// never hit the bug — and it must produce exactly the same bytes as a sealed one does for
        /// the same shape, which is what "monomorphic changes nothing about the wrapper" means.
        /// </summary>
        [Fact]
        public void A_Null_Open_Member_Still_Round_Trips_And_Encodes_A_Null()
        {
            var ceras = Fast();

            var clone = ceras.Deserialize<MonoReflOpenHolder>(ceras.Serialize(new MonoReflOpenHolder { Number = 1, Child = null }));
            Assert.Equal(1, clone.Number);
            Assert.Null(clone.Child);

            // A null member of a sealed type and of an open type encode identically. Before the fix
            // the sealed case wrote the *present* payload (or threw), which is what makes this
            // assertion a regression test rather than a tautology.
            Assert.Equal(
                Hex(ceras.Serialize(new MonoReflHolder { Number = 1, Child = null })),
                Hex(ceras.Serialize(new MonoReflOpenHolder { Number = 1, Child = null })));
        }

        // ── byte identity with the generated formatter ────────────────────────────────────────

        /// <summary>
        /// The claim the code makes about monomorphic members is byte identity with the generated
        /// formatter, so it is checked against the generated formatter itself rather than against a
        /// recorded literal: if either side's encoding changes, this fails and says so, and if they
        /// change *together* it stays correct.
        /// <para>
        /// Both graphs are the same shape with the same values, so any difference is the encoding of
        /// the sealed member and nothing else. Before the fix this differed by one byte for the
        /// present case and threw for the null one.
        /// </para>
        /// </summary>
        [Fact]
        public void The_Fallback_Matches_The_Generated_Bytes_For_A_Sealed_Member()
        {
            var generated = Generated();
            var fallback = Fast();

            foreach (var (label, genChild, reflChild) in new (string, MonoGenSealedChild, MonoReflSealedChild)[]
            {
                ("null", null, null),
                ("present", new MonoGenSealedChild { Number = 2 }, new MonoReflSealedChild { Number = 2 }),
            })
            {
                var expected = Hex(generated.Serialize(new MonoGenHolder { Number = 1, Child = genChild }));
                var actual = Hex(fallback.Serialize(new MonoReflHolder { Number = 1, Child = reflChild }));

                Assert.True(expected == actual,
                    $"sealed member, {label}: the fallback wrote {actual} but the generated formatter wrote {expected}");
            }
        }

        /// <summary>A serializer whose formatters for the declared graph are the generated ones.</summary>
        static CerasFastSerializer Generated()
        {
            var config = new CerasRawConfig();
            config.ConfigType<MonoGenHolder>().CustomFormatter = new Ceras.Generated.Ceras_Test_MonoGenHolderFormatter();
            config.ConfigType<MonoGenSealedChild>().CustomFormatter = new Ceras.Generated.Ceras_Test_MonoGenSealedChildFormatter();
            return new CerasFastSerializer(config);
        }

        // ── the robust format is untouched ────────────────────────────────────────────────────

        /// <summary>
        /// The robust format carries a type id per member from the *generated* manifest, so it never
        /// went through the faulty branch. The pin is here to say so: a null monomorphic member
        /// still round-trips on the matching reader, and — the part that could regress — through the
        /// migration interpreter too, since both must agree on the same file.
        /// </summary>
        [Fact]
        public void Robust_Round_Trips_A_Null_Sealed_Member_On_Both_Readers()
        {
            var ceras = new CerasRobustSerializer(c => c.UseGeneratedFormatters(BatchBContext));

            foreach (var (label, child) in new (string, MonoGenSealedChild)[]
            {
                ("null", null),
                ("present", new MonoGenSealedChild { Number = 2 }),
            })
            {
                var bytes = ceras.Save(new MonoGenHolder { Number = 1, Child = child }, 1);

                var generated = ceras.Load<MonoGenHolder>(bytes, 1);
                Assert.Equal(1, generated.Number);
                if (child == null) Assert.Null(generated.Child);
                else Assert.Equal(2, generated.Child.Number);

                var interpreted = ceras.Load<MonoGenHolder>(SlowPath(bytes), 1);
                Assert.Equal(1, interpreted.Number);
                if (child == null) Assert.Null(interpreted.Child);
                else Assert.Equal(2, interpreted.Child.Number);

                Assert.True(true, label);
            }
        }

    }
}
