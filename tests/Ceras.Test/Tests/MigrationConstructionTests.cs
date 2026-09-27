using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Generated;

// Construction contract (TODO 0.8.11), checked as a *same-layout cross-check*: both readers are handed
// identical bytes of the current schema, and flipping the layout fingerprint is what selects between them
// (see TestBase.SlowPath). It is therefore not evidence of cross-version behavior — a changed-schema file
// is required for that, and lives in HistoricalConstructionFixtureTests (a real v1 fixture).
//
// The rule both readers share, in full:
//   * the reference wrapper is picked from the *declared* type of the slot. A slot declared as its own
//     sealed type is read through ReferenceFormatter_KnownSealedType<T>, which asks the configured
//     TypeConstruction for the instance even when Ceras only picked a default for it; every other slot
//     (an open class, an interface, an abstract base, `object`) is read through ReferenceFormatter<T>, which
//     asks only when the application selected a construction (TypeConfig.IsConstructionExplicit) or when
//     nothing else builds the instance.
//   * so: an explicitly configured factory or pool is honored for every reference shape, in a fresh object
//     only — an exact-type reuse is neither re-constructed nor pooled.
//   * a construction that produces no instance hands the allocation to the type's own formatter, exactly as
//     the generated reader does when its wrapper returns null.
namespace Ceras.Test.MigrationConstruction
{
    /// <summary>
    /// Construction events, in order. Only this file's models write to it, and xunit runs one test class
    /// sequentially, so a reset per load is enough.
    /// </summary>
    public static class ConstructionEvents
    {
        public static readonly List<string> Entries = new List<string>();
        public static void Reset() => Entries.Clear();
        public static void Record(string name) => Entries.Add(name);
        public static int CountOf(string name)
        {
            int count = 0;
            foreach (var e in Entries) if (e == name) count++;
            return count;
        }
        public static string Dump() => string.Join(",", Entries);
    }

    [CerasContext("MigrationConstruction")]
    [CerasSerializable]
    public sealed class SealedLeaf
    {
        public int Id;
        public string Name = InitName();
        public SealedLeaf Next;

        /// <summary>Not serialized: only a configured factory sets it, so it identifies the instance.</summary>
        [Exclude]
        public string FromFactory;

        static string InitName() { ConstructionEvents.Record("sealed.field-init"); return "from-field-init"; }
        public SealedLeaf() { ConstructionEvents.Record("sealed.ctor"); }

        /// <summary>A stand-in for a pool: it produces the instance and marks it.</summary>
        public static SealedLeaf FromPool()
        {
            ConstructionEvents.Record("sealed.factory");
            return new SealedLeaf { FromFactory = "pool" };
        }

        public static SealedLeaf EmptyPool() => null!;   // declared to return one, but produces none

        /// <summary>Parameterized: the arguments would have come from the removed fixup system.</summary>
        public static SealedLeaf WithArgument(int id) => new SealedLeaf { Id = id };
    }

    [CerasContext("MigrationConstruction")]
    [CerasSerializable]
    public class SealedRoot
    {
        public SealedLeaf Leaf;
        public SealedLeaf Alias;
        public int Tail;
    }

    [CerasContext("MigrationConstruction")]
    [CerasSerializable]
    public class OpenLeaf
    {
        public int Id;
        [Exclude] public string FromFactory;
        public OpenLeaf() { ConstructionEvents.Record("open.ctor"); }
        public static OpenLeaf FromPool()
        {
            ConstructionEvents.Record("open.factory");
            return new OpenLeaf { FromFactory = "pool" };
        }
    }

    [CerasContext("MigrationConstruction")]
    [CerasSerializable]
    public class OpenRoot
    {
        public OpenLeaf Leaf;
        public int Tail;
    }

    [CerasContext("MigrationConstruction")]
    [CerasSerializable]
    public abstract class BranchBase
    {
        public int Id;
    }

    /// <summary>Sealed, with a factory, but only ever read through a base-typed slot here.</summary>
    [CerasContext("MigrationConstruction")]
    [CerasSerializable]
    public sealed class SealedBranch : BranchBase
    {
        public SealedBranch() { ConstructionEvents.Record("branch.ctor"); }
        public static SealedBranch FromPool()
        {
            ConstructionEvents.Record("branch.factory");
            return new SealedBranch();
        }
    }

    [CerasContext("MigrationConstruction")]
    [CerasSerializable]
    public class OtherBranch : BranchBase
    {
        public string Tag;
    }

    /// <summary>Every slot shape a value can arrive through.</summary>
    [CerasContext("MigrationConstruction")]
    [CerasSerializable]
    public class ShapeRoot
    {
        public OpenLeaf Open;              // open class, declared as itself
        public SealedLeaf Sealed;          // sealed, declared as itself
        public object AsObject;            // sealed behind the least specific declared type
        public BranchBase AsBase;          // sealed behind an abstract base
        public int Tail;
    }

    [CerasContext("MigrationConstruction")]
    [CerasSerializable]
    public sealed class CallbackLeaf
    {
        public int Id;
        public CallbackLeaf Next;

        [Exclude] public int ObservedInAfter = -1;
        [Exclude] public bool ObservedCycleInAfter;

        public CallbackLeaf() { ConstructionEvents.Record("callback.ctor"); }
        public static CallbackLeaf FromPool()
        {
            ConstructionEvents.Record("callback.factory");
            return new CallbackLeaf();
        }

        [OnBeforeDeserialize]
        private void Before() => ConstructionEvents.Record("callback.before");

        /// <summary>Deferred until the whole root graph is populated, so it can see the members and the
        /// back-reference: the instance has to be registered before any callback runs.</summary>
        [OnAfterDeserialize]
        private void After()
        {
            ConstructionEvents.Record("callback.after");
            ObservedInAfter = Id;
            ObservedCycleInAfter = ReferenceEquals(this, Next?.Next);
        }
    }

    [CerasContext("MigrationConstruction")]
    [CerasSerializable]
    public class CallbackRoot
    {
        public CallbackLeaf A;
        public CallbackLeaf B;
        public int Tail;
    }

    /// <summary>A parameterized <c>[CerasConstructor]</c>: the supported way to construct with arguments.</summary>
    [CerasContext("MigrationConstruction")]
    [CerasSerializable]
    public sealed class SealedCtorLeaf
    {
        public int Value;
        public int Doubled;

        [CerasConstructor]
        public SealedCtorLeaf(int value)
        {
            ConstructionEvents.Record("sealed-ctor.ctor");
            Value = value;
            Doubled = value * 2;
        }
    }

    /// <summary>The two mechanisms at once: a factory supplies the instance and a parameterized
    /// <c>[CerasConstructor]</c> initializes it.</summary>
    [CerasContext("MigrationConstruction")]
    [CerasSerializable]
    public sealed class PooledCtorLeaf
    {
        public int Value;
        public int Doubled;

        [Exclude] public string FromFactory;

        [CerasConstructor]
        public PooledCtorLeaf(int value)
        {
            ConstructionEvents.Record("pooled-ctor.ctor");
            Value = value;
            Doubled = value * 2;
        }

        public static PooledCtorLeaf FromPool()
        {
            ConstructionEvents.Record("pooled.factory");
            return new PooledCtorLeaf(0) { FromFactory = "pool" };
        }
    }

    [CerasContext("MigrationConstruction")]
    [CerasSerializable]
    public class CtorRoot
    {
        public SealedCtorLeaf Plain;   // constructor arguments only
        public PooledCtorLeaf Pooled;  // constructor arguments and a factory
        public int Tail;
    }

    public class MigrationConstructionTests : TestBase
    {
        const string ContextName = "MigrationConstruction";

        static CerasRobustSerializer Configure(params Action<CerasConfig>[] extra) => new CerasRobustSerializer(c =>
        {
            c.UseGeneratedFormatters(ContextName);
            foreach (var step in extra) step(c);
        });

        static void PoolSealed(CerasConfig c) => c.ConfigType<SealedLeaf>().ConstructBy(() => SealedLeaf.FromPool());
        static void PoolCallback(CerasConfig c) => c.ConfigType<CallbackLeaf>().ConstructBy(() => CallbackLeaf.FromPool());
        static void PoolEveryShape(CerasConfig c)
        {
            c.ConfigType<SealedLeaf>().ConstructBy(() => SealedLeaf.FromPool());
            c.ConfigType<OpenLeaf>().ConstructBy(() => OpenLeaf.FromPool());
            c.ConfigType<SealedBranch>().ConstructBy(() => SealedBranch.FromPool());
        }

        /// <summary>
        /// Loads through the generated reader and through the migration interpreter (the fingerprint flip in
        /// <see cref="TestBase.SlowPath"/> is what selects the reader) and applies the same assertions to both
        /// results. The construction log is reset per load, so a count means "per reader".
        /// </summary>
        static void BothPaths<T>(CerasRobustSerializer serializer, byte[] bytes, Action<T> assert) where T : class
        {
            ConstructionEvents.Reset();
            assert(serializer.Load<T>(bytes, 1));
            ConstructionEvents.Reset();
            assert(serializer.Load<T>(SlowPath(bytes), 1));
        }

        /// <summary>As <see cref="BothPaths{T}"/>, for a load that is not a plain fresh <c>Load&lt;T&gt;</c>.</summary>
        static void OnBothReaders(CerasRobustSerializer serializer, byte[] bytes,
            Action<CerasRobustSerializer, byte[]> load)
        {
            load(serializer, bytes);
            load(serializer, SlowPath(bytes));
        }

        /// <summary>
        /// The default construction of a type read through its own sealed declaration is "uninitialized", so
        /// neither the parameterless constructor nor the field initializer runs — on either reader. The
        /// interpreter used to run them, so the same save produced a different object per reader.
        /// </summary>
        [Fact]
        public void SealedType_DefaultConstruction_Agrees_On_Both_Readers()
        {
            var serializer = Configure();
            var rootBytes = serializer.Save(new SealedRoot { Leaf = new SealedLeaf { Id = 1, Name = "saved" }, Tail = 7 }, 1);
            var leafBytes = serializer.Save(new SealedLeaf { Id = 3, Name = "root" }, 1);

            BothPaths<SealedRoot>(serializer, rootBytes, loaded =>
            {
                Assert.Equal(1, loaded.Leaf.Id);
                Assert.Equal("saved", loaded.Leaf.Name);
                Assert.Equal(7, loaded.Tail);
                Assert.Null(loaded.Leaf.FromFactory);
                Assert.Equal("", ConstructionEvents.Dump());
            });

            BothPaths<SealedLeaf>(serializer, leafBytes, loaded =>
            {
                Assert.Equal(3, loaded.Id);
                Assert.Equal("root", loaded.Name);
                Assert.Null(loaded.FromFactory);
                Assert.Equal("", ConstructionEvents.Dump());
            });
        }

        /// <summary>
        /// A configured factory supplies the instance for a sealed declared type, on both readers: the
        /// members are written into the factory's instance, so a pooled object keeps its pool marker and
        /// gets the saved values.
        /// </summary>
        [Fact]
        public void SealedType_ConfiguredFactory_IsUsed_On_Both_Readers()
        {
            var serializer = Configure(PoolSealed);
            var rootBytes = serializer.Save(new SealedRoot { Leaf = new SealedLeaf { Id = 1, Name = "saved" }, Tail = 7 }, 1);
            var leafBytes = serializer.Save(new SealedLeaf { Id = 3, Name = "root" }, 1);

            BothPaths<SealedRoot>(serializer, rootBytes, loaded =>
            {
                Assert.Equal(1, loaded.Leaf.Id);
                Assert.Equal("saved", loaded.Leaf.Name);
                Assert.Equal(7, loaded.Tail);
                Assert.Equal("pool", loaded.Leaf.FromFactory);
                Assert.Equal(1, ConstructionEvents.CountOf("sealed.factory"));
            });

            BothPaths<SealedLeaf>(serializer, leafBytes, loaded =>
            {
                Assert.Equal(3, loaded.Id);
                Assert.Equal("pool", loaded.FromFactory);
                Assert.Equal(1, ConstructionEvents.CountOf("sealed.factory"));
            });
        }

        /// <summary>
        /// An explicitly configured factory runs for *every* reference shape, not only for a slot declared as
        /// its own sealed type: an open class, a sealed class behind <c>object</c> and a sealed class behind
        /// an abstract base all get the factory's instance.
        /// </summary>
        [Fact]
        public void A_ConfiguredFactory_Runs_For_Every_Slot_Shape_On_Both_Readers()
        {
            var serializer = Configure(PoolEveryShape);
            var bytes = serializer.Save(new ShapeRoot
            {
                Open = new OpenLeaf { Id = 1 },
                Sealed = new SealedLeaf { Id = 2, Name = "sealed" },
                AsObject = new SealedLeaf { Id = 3, Name = "object" },
                AsBase = new SealedBranch { Id = 4 },
                Tail = 5,
            }, 1);

            BothPaths<ShapeRoot>(serializer, bytes, loaded =>
            {
                Assert.Equal(1, loaded.Open.Id);
                Assert.Equal("pool", loaded.Open.FromFactory);
                Assert.Equal(1, ConstructionEvents.CountOf("open.ctor")); // once, inside the factory

                Assert.Equal(2, loaded.Sealed.Id);
                Assert.Equal("sealed", loaded.Sealed.Name);
                Assert.Equal("pool", loaded.Sealed.FromFactory);

                var viaObject = Assert.IsType<SealedLeaf>(loaded.AsObject);
                Assert.Equal(3, viaObject.Id);
                Assert.Equal("pool", viaObject.FromFactory);

                var viaBase = Assert.IsType<SealedBranch>(loaded.AsBase);
                Assert.Equal(4, viaBase.Id);
                Assert.Equal(1, ConstructionEvents.CountOf("branch.factory"));

                Assert.Equal(5, loaded.Tail);
                Assert.Equal(2, ConstructionEvents.CountOf("sealed.factory")); // the two sealed leaves
            });
        }

        /// <summary>A reused instance is neither re-constructed nor pooled: it keeps identity and state.</summary>
        [Fact]
        public void SealedType_ReusedInstance_Is_Not_Reconstructed_Or_Pooled_On_Both_Readers()
        {
            var serializer = Configure(PoolSealed);
            var bytes = serializer.Save(new SealedRoot { Leaf = new SealedLeaf { Id = 1, Name = "saved" }, Tail = 7 }, 1);

            OnBothReaders(serializer, bytes, (s, data) =>
            {
                var existingLeaf = new SealedLeaf { Id = 99, Name = "existing", FromFactory = "kept" };
                var existing = new SealedRoot { Leaf = existingLeaf, Tail = 0 };
                ConstructionEvents.Reset(); // building the destination is the test's doing, not the reader's

                s.Load(ref existing, data, 1);

                Assert.Same(existingLeaf, existing.Leaf);
                Assert.Equal("kept", existing.Leaf.FromFactory);
                Assert.Equal(1, existing.Leaf.Id);
                Assert.Equal("saved", existing.Leaf.Name);
                Assert.Equal(7, existing.Tail);
                Assert.Equal("", ConstructionEvents.Dump());
            });
        }

        /// <summary>
        /// A construction that produces no instance hands the allocation to the type's own formatter, the way
        /// the generated reader does when its wrapper returns null. Storing the null would break the identity
        /// cache slot the writer reserved for this object.
        /// </summary>
        [Fact]
        public void SealedType_Construction_Returning_Null_Falls_Back_To_The_Own_Formatter_On_Both_Readers()
        {
            var serializer = Configure(c => c.ConfigType<SealedLeaf>().ConstructBy(() => SealedLeaf.EmptyPool()));
            var bytes = serializer.Save(new SealedRoot { Leaf = new SealedLeaf { Id = 1, Name = "saved" }, Tail = 7 }, 1);

            BothPaths<SealedRoot>(serializer, bytes, loaded =>
            {
                Assert.NotNull(loaded.Leaf);
                Assert.Equal(1, loaded.Leaf.Id);
                Assert.Equal("saved", loaded.Leaf.Name);
                Assert.Equal(7, loaded.Tail);
                Assert.Equal(1, ConstructionEvents.CountOf("sealed.ctor"));
            });
        }

        /// <summary>
        /// Cycles through a factory-constructed type: the instance has to be in the identity cache before its
        /// members are read, so a back-reference resolves to the same object and the factory is asked exactly
        /// once per object.
        /// </summary>
        [Fact]
        public void SealedType_Cycle_Keeps_Identity_And_Pools_Each_Object_Once_On_Both_Readers()
        {
            var serializer = Configure(PoolSealed);
            var a = new SealedLeaf { Id = 1, Name = "a" };
            var b = new SealedLeaf { Id = 2, Name = "b" };
            a.Next = b;
            b.Next = a;
            var bytes = serializer.Save(new SealedRoot { Leaf = a, Alias = b, Tail = 7 }, 1);

            BothPaths<SealedRoot>(serializer, bytes, loaded =>
            {
                Assert.Same(loaded.Leaf.Next, loaded.Alias);
                Assert.Same(loaded.Alias.Next, loaded.Leaf);
                Assert.Equal(1, loaded.Leaf.Id);
                Assert.Equal(2, loaded.Alias.Id);
                Assert.Equal("a", loaded.Leaf.Name);
                Assert.Equal("b", loaded.Alias.Name);
                Assert.Equal("pool", loaded.Leaf.FromFactory);
                Assert.Equal("pool", loaded.Alias.FromFactory);
                Assert.Equal(2, ConstructionEvents.CountOf("sealed.factory")); // once per object, not per occurrence
            });
        }

        /// <summary>
        /// Callback order around a factory-constructed instance: construction, then
        /// <c>[OnBeforeDeserialize]</c>, then — deferred to the end of the graph — <c>[OnAfterDeserialize]</c>.
        /// </summary>
        [Fact]
        public void SealedType_Callbacks_Run_After_The_Factory_On_Both_Readers()
        {
            var serializer = Configure(PoolCallback);
            var a = new CallbackLeaf { Id = 1 };
            a.Next = a; // a self-cycle through the factory-constructed type
            var bytes = serializer.Save(new CallbackRoot { A = a, B = null, Tail = 7 }, 1);

            BothPaths<CallbackRoot>(serializer, bytes, loaded =>
            {
                Assert.Equal("callback.factory,callback.ctor,callback.before,callback.after", ConstructionEvents.Dump());
                Assert.Equal(1, loaded.A.Id);
                Assert.Same(loaded.A, loaded.A.Next);
                Assert.Equal(1, loaded.A.ObservedInAfter);
                Assert.True(loaded.A.ObservedCycleInAfter); // registered before the callbacks ran
                Assert.Null(loaded.B);
                Assert.Equal(7, loaded.Tail);
            });
        }

        /// <summary>
        /// Constructor arguments, with and without a factory. Without one, the instance is allocated
        /// uninitialized; with one, it is the factory's instance — and the generated reader invokes the
        /// constructor on *that* object, so the interpreter has to build the same one rather than its own.
        /// Either way the constructor runs with the payload's value and every member is assigned afterwards,
        /// so the stored value wins.
        /// </summary>
        [Fact]
        public void ConstructorArguments_With_And_Without_A_Factory_Agree_On_Both_Readers()
        {
            var serializer = Configure(c => c.ConfigType<PooledCtorLeaf>().ConstructBy(() => PooledCtorLeaf.FromPool()));
            var bytes = serializer.Save(new CtorRoot
            {
                Plain = new SealedCtorLeaf(4),
                Pooled = new PooledCtorLeaf(5),
                Tail = 7,
            }, 1);

            BothPaths<CtorRoot>(serializer, bytes, loaded =>
            {
                Assert.Equal(4, loaded.Plain.Value);
                Assert.Equal(8, loaded.Plain.Doubled);

                Assert.Equal("pool", loaded.Pooled.FromFactory);   // the factory's instance, not a fresh one
                Assert.Equal(5, loaded.Pooled.Value);
                Assert.Equal(10, loaded.Pooled.Doubled);
                Assert.Equal(7, loaded.Tail);

                // The pool built its object with the constructor, and the reader then invoked that
                // constructor again with the payload's value — the same sequence on both readers (the file
                // stores the pooled member first, whatever the current declaration order is).
                Assert.Equal("pooled.factory,pooled-ctor.ctor,pooled-ctor.ctor,sealed-ctor.ctor",
                    ConstructionEvents.Dump());
            });
        }

        /// <summary>
        /// Construction is a read-side concern: the same graph written with and without a configured
        /// construction has to produce the same bytes, or every existing save would stop matching.
        /// </summary>
        [Fact]
        public void A_Configured_Construction_Does_Not_Change_The_Written_Bytes()
        {
            static SealedRoot Graph() => new SealedRoot
            {
                Leaf = new SealedLeaf { Id = 1, Name = "saved", Next = new SealedLeaf { Id = 2, Name = "second" } },
                Alias = null,
                Tail = 7,
            };

            var withoutFactory = Configure().Save(Graph(), 1);
            var withFactory = Configure(PoolSealed).Save(Graph(), 1);

            Assert.Equal(withoutFactory, withFactory);
        }

        /// <summary>
        /// A configured construction that takes arguments cannot be honored by either reader: the arguments
        /// would have come from the removed fixup system. The interpreter must not silently ignore the
        /// selection and build the object some other way — that would make a save load on the migration path
        /// that cannot be loaded at all on the fast path.
        /// </summary>
        [Fact]
        public void ParameterizedConfiguredConstruction_Is_Refused_On_Both_Readers()
        {
            var bytes = Configure().Save(new SealedRoot { Leaf = new SealedLeaf { Id = 1 }, Tail = 7 }, 1);
            var serializer = Configure(c => c.ConfigType<SealedLeaf>().ConstructBy(() => SealedLeaf.WithArgument(3)));

            var fast = Assert.ThrowsAny<Exception>(() => serializer.Load<SealedRoot>(bytes, 1));
            var slow = Assert.ThrowsAny<Exception>(() => serializer.Load<SealedRoot>(SlowPath(bytes), 1));

            // The fast reader surfaces it wrapped (the wrapper is built through reflection); the cause is the
            // same deliberate "no longer supported" refusal either way.
            Assert.Contains("no longer supported", RootCause(fast).Message, StringComparison.Ordinal);
            Assert.Contains("no longer supported", RootCause(slow).Message, StringComparison.Ordinal);
        }

        static Exception RootCause(Exception exception)
        {
            var current = exception;
            while (current.InnerException != null) current = current.InnerException;
            return current;
        }
    }
}
