// A member whose declared type is a *sealed* collection — the immutable ones, and a user type the
// author declared `sealed` and made an `ICollection<T>` — is the one shape the generator named the
// wrong formatter class for.
//
// Everything else the generator binds a reference member through a reference *wrapper*, and the
// wrapper is a field type: `PrepareFormatter` looks the member's type up with
// `GetReferenceFormatterRequired` and hands the result to that field. Which wrapper the runtime hands
// over is decided in one place, `CerasSerializer.GetReferenceFormatter`:
//
//     var refFormatterType = (type.IsSealed && !isType)
//         ? typeof(ReferenceFormatter_KnownSealedType<>).MakeGenericType(type)
//         : typeof(ReferenceFormatter<>).MakeGenericType(type);
//
// and `ReferenceFormatter_KnownSealedType<T>` does **not** derive from `ReferenceFormatter<T>` — it is
// a sibling, both implementing `IFormatter<T>`. So a field declared as the wrong one of the two cannot
// receive the right one, and injection ends at
//
//     throw new InvalidOperationException($"Injection mismatch for {fmtType.Name}: ...");
//
// The generator's two collection branches named `ReferenceFormatter<T>` unconditionally, so a save of
// any context holding an `ImmutableList<int>` member failed at injection before a byte was written.
// Every other reference member got this right, because the `IsSealed` check is already at
// `RobustCodeEmitter.GetConcreteFormatterType`'s monomorphic branch and its `[CerasSerializable]`
// branch; the collection branches were written without it. The fix is the one word the runtime's own
// rule already says.
//
// This file therefore pins:
//   * the round trip on the robust fast path, for `ImmutableList<int>`, `ImmutableHashSet<int>`, an
//     `ImmutableDictionary`, a null member and a user-defined sealed collection;
//   * that the two wrappers write the *same* bytes, so choosing the sealed one is not a format change;
//   * that a mismatched (migration) read agrees with what the fast path wrote;
//   * that the raw/transient path is untouched;
//   * that the manifest entry stays a structural `GENERIC` one rather than becoming `OPAQUE`;
//   * and the generator's own output, so the field type is asserted where it is decided and not only
//     where it happens to be observed.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Ceras.Formatters;
using Ceras.Generated;
using Ceras.ImmutableCollections;
using Xunit;

namespace Ceras.Test.SealedCollection
{
    /// <summary>
    /// The root of this file's context, holding one member per shape that matters: sealed immutable
    /// collections, a value-type collection, unsealed collections as the controls, and a sealed
    /// collection the user declared. Declared in a context of its own so a `[CerasSerializable]` type
    /// added anywhere else in the test assembly cannot move the bytes these tests compare.
    /// </summary>
    [CerasContext("SealedCollectionMember")]
    [CerasSerializable]
    public class SealedCollectionHolder
    {
        public int Sentinel;

        /// <summary>Sealed, and an <c>ICollection&lt;int&gt;</c>: the shape that used to throw.</summary>
        public ImmutableList<int> List;

        /// <summary>The set flavour, which reaches the same branch by the same rule.</summary>
        public ImmutableHashSet<int> Set;

        /// <summary>Sealed and an <c>IDictionary</c>: the other half of the same rule.</summary>
        public ImmutableDictionary<string, int> Map;

        /// <summary>
        /// A value-type collection, which is a <c>sealed</c> type as well and must therefore still be
        /// bound through the interface rather than through a reference wrapper — a reference wrapper is
        /// not available for a value type at all.
        /// </summary>
        public ImmutableArray<int> Frozen;

        /// <summary>Not sealed: already on the plain reference wrapper, and must stay there.</summary>
        public List<int> Open;

        /// <summary>A sealed collection the *user* wrote, which the rule has to cover too.</summary>
        public SealedBag<int> Bag;
    }

    /// <summary>
    /// A user-declared sealed collection: a mutable <c>ICollection&lt;T&gt;</c> with a public
    /// parameterless constructor, which is what the built-in <c>CollectionFormatter</c> needs in order
    /// to build one and add the elements to. Sealed on purpose — the defect is about the declared
    /// type's sealed-ness, and a BCL type is not the only thing that can have it.
    /// <para>
    /// The storage is created on first use rather than by a field initializer, because Ceras does not
    /// promise to run a constructor: a robust context's default construction is "uninitialized", and
    /// <c>CollectionFormatter</c> clears the instance it was handed before filling it. A fixture that
    /// needed its initializer to have run would be testing the construction mode rather than the
    /// wrapper choice, and would fail for a reason this file has nothing to say about.
    /// </para>
    /// </summary>
    public sealed class SealedBag<T> : ICollection<T>
    {
        List<T> _items;

        List<T> Items => _items ??= new List<T>();

        public int Count => Items.Count;
        public bool IsReadOnly => false;

        public void Add(T item) => Items.Add(item);
        public void Clear() => Items.Clear();
        public bool Contains(T item) => Items.Contains(item);
        public void CopyTo(T[] array, int arrayIndex) => Items.CopyTo(array, arrayIndex);
        public bool Remove(T item) => Items.Remove(item);
        public IEnumerator<T> GetEnumerator() => Items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => Items.GetEnumerator();
    }

    /// <summary>The same bag, unsealed: the control for the rule's other side.</summary>
    public class OpenBag<T> : ICollection<T>
    {
        List<T> _items;

        List<T> Items => _items ??= new List<T>();

        public int Count => Items.Count;
        public bool IsReadOnly => false;

        public void Add(T item) => Items.Add(item);
        public void Clear() => Items.Clear();
        public bool Contains(T item) => Items.Contains(item);
        public void CopyTo(T[] array, int arrayIndex) => Items.CopyTo(array, arrayIndex);
        public bool Remove(T item) => Items.Remove(item);
        public IEnumerator<T> GetEnumerator() => Items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => Items.GetEnumerator();
    }

    /// <summary>
    /// The controls, in a root of their own. A separate graph so the non-sealed cases can be pinned
    /// without a sealed member in the picture: with both in one class, a failure in either could be
    /// attributed to the other.
    /// </summary>
    [CerasContext("SealedCollectionMember")]
    [CerasSerializable]
    public class OpenCollectionHolder
    {
        public int Sentinel;
        public List<int> Open;
        public OpenBag<int> Bag;
    }
}

namespace Ceras.Test.Tests
{
    using Ceras.Test.Generator;
    using Ceras.Test.SealedCollection;

    public class SealedCollectionMemberTests : TestBase
    {
        /// <summary>
        /// This file's own context. Named apart from <see cref="TestBase.Context"/> — the test
        /// assembly's shared "Tests" context — because a manifest is per context, and a member of this
        /// shape in the shared one would put a sealed collection into every other file's fingerprint.
        /// </summary>
        public const string SealedCollectionContext = "SealedCollectionMember";

        static CerasRobustSerializer Robust(Action<CerasRobustConfig> configure = null)
            => new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(SealedCollectionContext);
                c.UseImmutableFormatters();
                configure?.Invoke(c);
            });

        static CerasFastSerializer Raw()
            => new CerasFastSerializer(c =>
            {
                c.AllowReflectionFallback = true;
                c.UseImmutableFormatters();
            });

        static SealedCollectionHolder Model() => new SealedCollectionHolder
        {
            Sentinel = 0x2BADF00D,
            List = ImmutableList.Create(1, 2, 3),
            Set = ImmutableHashSet.Create(4, 5, 6),
            Map = ImmutableDictionary.CreateRange(new[]
            {
                new KeyValuePair<string, int>("a", 7),
                new KeyValuePair<string, int>("b", 8),
            }),
            Frozen = ImmutableArray.Create(12, 13),
            Open = new List<int> { 9, 10 },
            Bag = new SealedBag<int> { 11 },
        };

        // ── the precondition: the runtime really does hand over the sealed wrapper ────────────

        /// <summary>
        /// The reason the generator's field type has to match. <c>GetReferenceFormatter</c> picks the
        /// wrapper for a sealed reference type, and the two wrappers are siblings rather than one
        /// deriving from the other — so a field typed to the other one cannot be filled. This asserts
        /// both halves: the answer the runtime gives, and that the generated field's type would not
        /// have accepted it.
        /// </summary>
        [Fact]
        public void TheRuntimePicksTheSealedWrapperForASealedCollectionAndTheFieldCannotTakeIt()
        {
            var serializer = Robust();

            var reference = ReferenceFormatterOf(serializer, typeof(ImmutableList<int>));

            Assert.Equal(typeof(ReferenceFormatter_KnownSealedType<ImmutableList<int>>), reference.GetType());
            // Not a subtype of the plain wrapper, which is the whole reason the field type matters.
            Assert.False(typeof(ReferenceFormatter<ImmutableList<int>>).IsAssignableFrom(reference.GetType()));

            // And the control: an unsealed collection is a normal reference slot.
            Assert.Equal(typeof(ReferenceFormatter<List<int>>), ReferenceFormatterOf(serializer, typeof(List<int>)).GetType());

            // A value-type collection has no reference wrapper at all — `GetReferenceFormatter` hands
            // one of those straight to the specific formatter — so its member field can only be the
            // interface, whatever the type's own modifiers say.
            Assert.Equal(
                typeof(Ceras.ImmutableCollections.ImmutableArrayFormatter<int>),
                serializer.GetSpecificFormatter(typeof(ImmutableArray<int>)).GetType());
        }

        static IFormatter ReferenceFormatterOf(CerasSerializer serializer, Type type)
        {
            var method = typeof(CerasSerializer).GetMethod("GetReferenceFormatter",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(Type) }, null);
            return (IFormatter)method.Invoke(serializer, new object[] { type });
        }

        // ── the defect ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A save of a context holding a sealed collection member. It never wrote a byte: injection of
        /// the member's formatter field refused, because the generated field was typed to the wrapper
        /// the runtime does not hand over for a sealed type.
        /// </summary>
        [Fact]
        public void ASaveOfASealedCollectionMemberSucceeds()
        {
            var serializer = Robust();

            byte[] bytes = serializer.Save(Model(), 1);

            Assert.NotEmpty(bytes);
        }

        // ── the round trips ───────────────────────────────────────────────────────────────────

        [Fact]
        public void ASealedCollectionMemberRoundTripsOnTheMatchingReader()
        {
            var serializer = Robust();
            var original = Model();

            var clone = serializer.Load<SealedCollectionHolder>(serializer.Save(original, 1), 1);

            Assert.Equal(original.Sentinel, clone.Sentinel);
            Assert.Equal(original.List, clone.List);
            Assert.Equal(original.Set, clone.Set);
            Assert.Equal(original.Map, clone.Map);
            // Compared as a sequence: two `ImmutableArray<int>` are two boxed structs, and xUnit's
            // collection comparison rejects the pair on that alone.
            Assert.Equal(new[] { 12, 13 }, clone.Frozen.ToArray());
            Assert.Equal(original.Open, clone.Open);
            Assert.Equal(original.Bag, clone.Bag);
        }

        /// <summary>
        /// A null sealed member. The null marker is written by the wrapper, so this is the case that
        /// separates the two wrappers most sharply: only <c>ReferenceFormatter_KnownSealedType&lt;T&gt;</c>
        /// writes one for a sealed type, and a member bound to the specific formatter instead would
        /// have had no concept of a null member at all.
        /// </summary>
        [Fact]
        public void ANullSealedCollectionMemberRoundTripsAsNullAndIsEncoded()
        {
            var serializer = Robust();
            var model = new SealedCollectionHolder { Sentinel = 3, Set = ImmutableHashSet.Create(1) };

            byte[] nulls = serializer.Save(model, 1);
            var clone = serializer.Load<SealedCollectionHolder>(nulls, 1);

            Assert.Equal(3, clone.Sentinel);
            Assert.Null(clone.List);
            Assert.Null(clone.Map);
            Assert.Null(clone.Bag);
            Assert.Equal(ImmutableHashSet.Create(1), clone.Set);
            Assert.Null(clone.Open);

            // A null is a value, not an omission: the payload carrying one is strictly longer,
            // because the marker that tells them apart is exactly what the wrapper writes.
            byte[] present = serializer.Save(Model(), 1);
            Assert.True(nulls.Length < present.Length,
                $"mostly-null payload ({nulls.Length}B) must be shorter than the populated one ({present.Length}B)");
        }

        /// <summary>
        /// The mismatched reader. The bytes the fast path wrote must be readable by the migration
        /// interpreter as well, which resolves its member formatters through the same
        /// <c>GetReferenceFormatterRequired</c> — so the two readers agreeing here is what says the
        /// generator now agrees with the runtime rather than merely with itself. Both null markers and
        /// both collection shapes are read here, on the same file the fast path wrote.
        /// <para>
        /// The <c>Immutable*</c> members are null in this graph, and that is a limit of the
        /// interpreter rather than a dodge: it builds a value by a rule of its own
        /// (<c>CurrentObjectFactory.CreateCollection</c> — a public parameterless constructor, a
        /// wrapping constructor, or a <c>Clear</c> to refill) and no immutable collection has any of
        /// the three, so a present one is refused before this change and after it. That is a question
        /// about construction rather than about which wrapper wrote the member, it is pinned on its own
        /// below, and it is why the sealed collection the interpreter <em>can</em> build — the user's
        /// own, which has a <c>Clear</c> — is the one carrying a value here.
        /// </para>
        /// </summary>
        [Fact]
        public void ASealedCollectionMemberRoundTripsOnTheMismatchedSlowPathToo()
        {
            var serializer = Robust();
            var original = new SealedCollectionHolder
            {
                Sentinel = 0x2BADF00D,
                List = null,
                Set = null,
                Map = null,
                Frozen = default,
                Open = new List<int> { 9, 10 },
                Bag = new SealedBag<int> { 11, 12 },
            };

            byte[] bytes = serializer.Save(original, 1);
            var interpreted = serializer.Load<SealedCollectionHolder>(SlowPath(bytes), 1);

            Assert.Equal(original.Sentinel, interpreted.Sentinel);
            // The sealed member the interpreter can construct, through the wrapper the fast path used.
            Assert.Equal(original.Bag, interpreted.Bag);
            Assert.Equal(new[] { 11, 12 }, interpreted.Bag.ToArray());
            Assert.Equal(original.Open, interpreted.Open);
            // ...and the null markers the sealed wrapper wrote, read back through it.
            Assert.Null(interpreted.List);
            Assert.Null(interpreted.Set);
            Assert.Null(interpreted.Map);
            // The value-type collection comes back empty either way; it is not `ImmutableArray<int>.Empty`
            // specifically, because a default instance and an empty one are the same on the wire.
            Assert.True(interpreted.Frozen.IsDefaultOrEmpty,
                $"expected an empty or default ImmutableArray, got {interpreted.Frozen}");

            // The all-null case, which is a different payload shape and not a shorter variant of the
            // same one.
            var sparseClone = serializer.Load<SealedCollectionHolder>(
                SlowPath(serializer.Save(new SealedCollectionHolder { Sentinel = 9 }, 1)), 1);
            Assert.Equal(9, sparseClone.Sentinel);
            Assert.Null(sparseClone.List);
            Assert.Null(sparseClone.Set);
            Assert.Null(sparseClone.Map);
            Assert.Null(sparseClone.Bag);
        }

        // ── the bytes did not change ──────────────────────────────────────────────────────────

        /// <summary>
        /// The two wrappers differ in how they find a value's formatter, not in what they write: a
        /// value in a slot declared as <c>T</c> is, when <c>T</c> is sealed, always of type <c>T</c>,
        /// so both emit the same-type marker and then hand the value to the same specific formatter.
        /// Naming the sealed wrapper is therefore not a format change, and this asserts the claim at
        /// the only place it is a claim rather than a consequence — the two writers, side by side.
        /// <para>
        /// Two separately created but equal values, so neither is a back-reference to the other: the
        /// object-cache id is sequence-based, and a cached instance would write that id instead of
        /// the marker and make the comparison meaningless.
        /// </para>
        /// </summary>
        [Fact]
        public void TheTwoReferenceWrappersWriteTheSameBytesForASealedTypesOwnValue()
        {
            var serializer = Robust();

            var plain = new ReferenceFormatter<ImmutableList<int>>(serializer);
            var knownSealed = new ReferenceFormatter_KnownSealedType<ImmutableList<int>>(serializer);

            byte[] viaPlain = Write(plain, ImmutableList.Create(1, 2, 3));
            byte[] viaSealed = Write(knownSealed, ImmutableList.Create(1, 2, 3));

            // Not vacuous: two empty writers would satisfy the comparison below.
            Assert.NotEmpty(viaSealed);
            Assert.Equal(Convert.ToHexString(viaPlain), Convert.ToHexString(viaSealed));

            // A null, which is the other value a slot like this can hold. The plain wrapper has to
            // decide what to write for "nothing" on its own, and it agrees with the sealed one.
            byte[] nullViaPlain = Write(plain, null);
            byte[] nullViaSealed = Write(knownSealed, null);
            Assert.Equal(Convert.ToHexString(nullViaPlain), Convert.ToHexString(nullViaSealed));
        }

        static byte[] Write<T>(IFormatter<T> formatter, T value) where T : class
        {
            var buffer = new ArrayBufferWriter<byte>();
            formatter.Serialize(buffer, value);
            return buffer.WrittenSpan.ToArray();
        }

        // ── the raw path is untouched ────────────────────────────────────────────────────────

        /// <summary>
        /// A raw/transient context describes nothing and has no generated formatter to be wrong, so
        /// nothing here is a change: the same graph through <c>CerasFastSerializer</c>, with the
        /// immutable formatter resolver installed, still round-trips in both directions.
        /// </summary>
        [Fact]
        public void TheRawPathRoundTripsASealedCollectionMemberUnchanged()
        {
            var serializer = Raw();
            var original = Model();

            var clone = serializer.Deserialize<SealedCollectionHolder>(serializer.Serialize(original));

            Assert.Equal(original.Sentinel, clone.Sentinel);
            Assert.Equal(original.List, clone.List);
            Assert.Equal(original.Set, clone.Set);
            Assert.Equal(original.Map, clone.Map);
            Assert.Equal(new[] { 12, 13 }, clone.Frozen.ToArray());
            Assert.Equal(original.Open, clone.Open);
            Assert.Equal(original.Bag, clone.Bag);
        }

        // ── the schema is still structural ────────────────────────────────────────────────────

        /// <summary>
        /// The manifest entry for a collection member is a structural <c>GENERIC</c> one — a shape with
        /// its argument types — because that is what its payload is. It must stay that way: the
        /// immutable collections' formatters are approved selections, not opaque ones, and a save that
        /// started framing them would be a different file format rather than a fix to this one.
        /// </summary>
        [Fact]
        public void TheSchemaEntryForASealedCollectionStaysStructuralAndIsNotOpaque()
        {
            var serializer = Robust();

            string dump = CerasRobustSerializer.DumpSchema(serializer.Save(Model(), 1));

            Assert.Contains("GENERIC", dump, StringComparison.Ordinal);
            Assert.DoesNotContain("OPAQUE", dump, StringComparison.Ordinal);
        }

        // ── where the decision is made ────────────────────────────────────────────────────────

        /// <summary>
        /// The generated field's type, asserted on the generated class itself. Everything above shows
        /// the consequence; this shows the cause, so a change that altered the outcome for some other
        /// reason (a resolver, a policy) could not quietly satisfy them.
        /// </summary>
        [Fact]
        public void TheGeneratedFormatterNamesTheSealedWrapperForSealedCollectionsAndThePlainOneOtherwise()
        {
            var fieldTypes = FormatterFieldTypes<SealedCollectionHolder>();

            Assert.Contains(typeof(ReferenceFormatter_KnownSealedType<ImmutableList<int>>), fieldTypes);
            Assert.Contains(typeof(ReferenceFormatter_KnownSealedType<ImmutableHashSet<int>>), fieldTypes);
            Assert.Contains(typeof(ReferenceFormatter_KnownSealedType<ImmutableDictionary<string, int>>), fieldTypes);
            Assert.Contains(typeof(ReferenceFormatter_KnownSealedType<SealedBag<int>>), fieldTypes);

            // The control: a non-sealed collection is a polymorphic slot and keeps the wrapper that
            // records the concrete type behind the marker.
            Assert.Contains(typeof(ReferenceFormatter<List<int>>), fieldTypes);
            Assert.DoesNotContain(typeof(ReferenceFormatter<ImmutableList<int>>), fieldTypes);
            Assert.DoesNotContain(typeof(ReferenceFormatter<SealedBag<int>>), fieldTypes);

            // A value-type collection is still bound through the interface, so a formatter the
            // application installs at runtime can take its place (the same contract as any other
            // value-type member) — and it cannot be a reference wrapper, which has no value-type form.
            Assert.Contains(typeof(IFormatter<ImmutableArray<int>>), fieldTypes);
            Assert.DoesNotContain(typeof(IFormatter<ImmutableList<int>>), fieldTypes);

            Assert.Contains(typeof(ReferenceFormatter<OpenBag<int>>), FormatterFieldTypes<OpenCollectionHolder>());
        }

        /// <summary>The declared types of the formatter fields the generator emitted for a root.</summary>
        static List<Type> FormatterFieldTypes<T>() where T : class
            => typeof(T).Assembly
                .GetType(GeneratedFormatterName(typeof(T)))
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Select(f => f.FieldType)
                .ToList();

        /// <summary>
        /// The generated class for a type of this assembly. Named through the same rule the generator
        /// uses rather than spelled out twice, so a rename in the generator shows up here.
        /// </summary>
        static string GeneratedFormatterName(Type type)
            => "Ceras.Generated.Ceras_Test_SealedCollection_"
               + type.Name.Replace("`", "_") + "Formatter";

        /// <summary>
        /// The same decision, read off the generator's output rather than off a compiled class, so it
        /// holds for a type this assembly does not declare — a <c>sealed</c> collection the generator
        /// has never seen is the case that matters here, since the rule is about the declared type.
        /// </summary>
        [Fact]
        public void TheGeneratorEmitsTheSealedWrapperForAnySealedCollectionMember()
        {
            const string source = """
                using System.Collections.Generic;
                using System.Collections.Immutable;

                namespace Gen
                {
                    [Ceras.CerasContext("Probe")]
                    [Ceras.CerasSerializable]
                    public class Root
                    {
                        public ImmutableList<int> SealedList;
                        public ImmutableDictionary<string, int> SealedMap;
                        public List<int> Open;
                        public System.Collections.Generic.ICollection<int> ByInterface;
                    }
                }
                """;

            string generated = GeneratorTestHarness.GeneratedSourceContaining(source, "Root");
            Assert.NotNull(generated);

            Assert.Contains(
                "global::Ceras.Formatters.ReferenceFormatter_KnownSealedType<global::System.Collections.Immutable.ImmutableList<int>>",
                generated, StringComparison.Ordinal);
            Assert.Contains(
                "global::Ceras.Formatters.ReferenceFormatter_KnownSealedType<global::System.Collections.Immutable.ImmutableDictionary<string, int>>",
                generated, StringComparison.Ordinal);
            Assert.Contains(
                "global::Ceras.Formatters.ReferenceFormatter<global::System.Collections.Generic.List<int>>",
                generated, StringComparison.Ordinal);

            // An interface-typed member is never sealed, and an interface can hold a subtype — so it
            // stays on the polymorphic wrapper. The second collection branch, which only an
            // interface (or an abstract collection) reaches, is pinned here.
            Assert.Contains(
                "global::Ceras.Formatters.ReferenceFormatter<global::System.Collections.Generic.ICollection<int>>",
                generated, StringComparison.Ordinal);
        }

        // ── the controls, on their own ────────────────────────────────────────────────────────

        /// <summary>
        /// A context whose only collection members are unsealed. This is the case the change must not
        /// touch, and it is pinned by round-tripping it rather than by asserting a field type, so it
        /// fails on any behavioural difference too.
        /// </summary>
        [Fact]
        public void AContextOfOnlyNonSealedCollectionsIsUnchanged()
        {
            var serializer = Robust();
            var original = new OpenCollectionHolder
            {
                Sentinel = 5,
                Open = new List<int> { 1, 2 },
                Bag = new OpenBag<int> { 3 },
            };

            var clone = serializer.Load<OpenCollectionHolder>(serializer.Save(original, 1), 1);

            Assert.Equal(original.Sentinel, clone.Sentinel);
            Assert.Equal(original.Open, clone.Open);
            Assert.Equal(original.Bag, clone.Bag);

            var sparse = serializer.Load<OpenCollectionHolder>(
                SlowPath(serializer.Save(new OpenCollectionHolder { Sentinel = 5 }, 1)), 1);
            Assert.Equal(5, sparse.Sentinel);
            Assert.Null(sparse.Open);
            Assert.Null(sparse.Bag);
        }

        // ── a neighbouring limit, stated rather than worked around ───────────────────────────

        /// <summary>
        /// The migration interpreter cannot rebuild <em>any</em> immutable collection: it has no public
        /// parameterless constructor, no wrapping constructor, and no <c>Clear</c> to refill, and that
        /// is <c>CurrentObjectFactory.CreateCollection</c>'s rule about construction rather than
        /// anything about the wrapper this file is about. Pinned so the exclusion in the slow-path test
        /// above reads as a known limit rather than as a shape quietly left out, and so a change to
        /// that rule shows up here instead of being mistaken for a regression — or, worse, being
        /// "fixed" by editing a test.
        /// </summary>
        [Theory]
        [InlineData("ImmutableList")]
        [InlineData("ImmutableHashSet")]
        [InlineData("ImmutableDictionary")]
        public void TheMigrationInterpreterRefusesToRebuildAPresentImmutableCollection(string which)
        {
            var serializer = Robust();
            var model = new SealedCollectionHolder
            {
                Sentinel = 1,
                List = which == nameof(ImmutableList) ? ImmutableList.Create(1) : null,
                Set = which == nameof(ImmutableHashSet) ? ImmutableHashSet.Create(1) : null,
                Map = which == nameof(ImmutableDictionary)
                    ? ImmutableDictionary.CreateRange(new[] { new KeyValuePair<string, int>("a", 7) })
                    : null,
            };

            byte[] bytes = serializer.Save(model, 1);

            // The matching reader has no such problem: it builds the value through the member's own
            // formatter. So the file is fine and only the interpreter's construction rule is not.
            var fast = serializer.Load<SealedCollectionHolder>(bytes, 1);
            Assert.Equal(1, which == nameof(ImmutableList) ? fast.List.Count
                      : which == nameof(ImmutableHashSet) ? fast.Set.Count
                      : fast.Map.Count);

            var error = Assert.Throws<Ceras.Exceptions.CerasSchemaException>(
                () => serializer.Load<SealedCollectionHolder>(SlowPath(bytes), 1));

            Assert.Contains(which, error.Message, StringComparison.Ordinal);
        }
    }
}
