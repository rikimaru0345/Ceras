using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

// Deserialization callbacks must be consistent across the generated reader and
// the migration interpreter, run exactly once per instance, and be deferred until the whole root
// graph is populated so a callback can inspect parent/sibling/shared/cyclic references.
namespace Ceras.Test.DeserializationCallbacks
{
    // ── Order / exactly-once / signatures ───────────────────────────────────────────────────────
    public static class CbLog
    {
        public static readonly List<string> Entries = new List<string>();
        public static void Reset() => Entries.Clear();
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CbOrder
    {
        public static int BeforeCalls;
        public static int AfterCalls;

        public int Value;

        [OnBeforeDeserialize]
        private void Before()
        {
            BeforeCalls++;
            CbLog.Entries.Add("before:" + Value);
        }

        [OnAfterDeserialize]
        private void After()
        {
            AfterCalls++;
            CbLog.Entries.Add("after:" + Value);
        }
    }

    // ── Graph inspection (parent / sibling / shared / cycle) ────────────────────────────────────
    public static class CbGraphCounters
    {
        public static int ChildCallbacks;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CbParent
    {
        // Deliberately member-ordered so the child references are read before Name and Children,
        // which is what makes deferred execution observable.
        public CbChild First;
        public CbChild Second;
        public List<CbChild> Children = new List<CbChild>();
        public string Name;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CbChild
    {
        public string Name;
        public CbParent Parent;

        // Recorded by the callback; serialized so the load can overwrite them first.
        public string ObservedParentName;
        public int ObservedSiblingCount = -1;
        public bool ObservedCycle;

        [OnAfterDeserialize]
        private void After()
        {
            CbGraphCounters.ChildCallbacks++;
            ObservedParentName = Parent?.Name;
            ObservedSiblingCount = Parent?.Children?.Count ?? -1;
            ObservedCycle = Parent != null && ReferenceEquals(Parent.First, this);
        }
    }

    // ── Inheritance: protected inherited and overridden callbacks ───────────────────────────────
    public static class CbInheritCounters
    {
        public static int BaseCalls;
        public static int DerivedCalls;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CbInheritBase
    {
        public int Id;

        [OnAfterDeserialize]
        protected virtual void Restore()
        {
            CbInheritCounters.BaseCalls++;
        }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CbInheritDerived : CbInheritBase
    {
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CbInheritOverride : CbInheritBase
    {
        [OnAfterDeserialize]
        protected override void Restore()
        {
            CbInheritCounters.DerivedCalls++;
        }
    }

    // ── The game's BaseFactory owner/delegate restoration shape ─────────────────────────────────
    public static class CbFactoryCounters
    {
        public static int RestoreCalls;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CbOwner
    {
        public string Name;
        public CbFactoryBase OwnerRef;

        public void RestoreOwner(CbFactoryBase owner) => OwnerRef = owner;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public abstract class CbFactoryBase
    {
        public CbOwner Owner;

        // Private inherited callback that rewires the owner back-reference and a delegate, exactly
        // like FactoryGame's BaseFactory.OnAfterDeserialize. It must run after the whole graph is
        // populated so Owner and the sibling factory are available.
        [OnAfterDeserialize]
        private void Restore()
        {
            CbFactoryCounters.RestoreCalls++;
            Owner?.RestoreOwner(this);
            DelegatesRestored = true;
        }

        public bool DelegatesRestored;

        public CbFactoryBase Sibling;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CbFactory : CbFactoryBase
    {
        public string Name;
    }

    // ── MigrationContext / CerasSerializer arguments ────────────────────────────────────────────
    public static class CbContextCounters
    {
        public static bool SawNone;
        public static bool SawMigrated;
        public static int SerializerCalls;
        public static object SeenUserContext;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CbMigrationContextModel
    {
        public int Value;

        [OnAfterDeserialize]
        private void After(MigrationContext context)
        {
            if (context == null) return;
            if (context.WasMigrated) CbContextCounters.SawMigrated = true;
            else CbContextCounters.SawNone = true;
        }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CbSerializerArgModel
    {
        public int Value;

        [OnAfterDeserialize]
        private void After(CerasSerializer serializer)
        {
            CbContextCounters.SerializerCalls++;
            CbContextCounters.SeenUserContext = serializer.UserContext;
        }
    }

    // ── Value-type callback (runs inline; cannot be deferred) ───────────────────────────────────
    public static class CbStructCounters
    {
        public static int Calls;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public struct CbStruct
    {
        public int Value;

        [OnAfterDeserialize]
        private void After()
        {
            CbStructCounters.Calls++;
        }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CbStructRoot
    {
        public CbStruct Item;
    }

    // ── Generic type callback (open-formatter path) ─────────────────────────────────────────────
    [CerasContext("Tests")]
    [CerasSerializable]
    public class CbGeneric<T>
    {
        public static int Calls;

        public T Value;

        [OnAfterDeserialize]
        private void After()
        {
            Calls++;
        }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CbGenericRoot
    {
        public CbGeneric<int> Box;
    }

    public class DeserializationCallbackTests : TestBase
    {
        static CerasRobustSerializer Generated() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));

        [Fact]
        public void Generated_BeforeAndAfter_FireExactlyOnce_InOrder()
        {
            var bytes = Generated().Save(new CbOrder { Value = 42 }, 1);

            CbLog.Reset();
            CbOrder.BeforeCalls = 0;
            CbOrder.AfterCalls = 0;

            var loaded = Generated().Load<CbOrder>(bytes, 1);

            Assert.Equal(42, loaded.Value);
            Assert.Equal(1, CbOrder.BeforeCalls);
            Assert.Equal(1, CbOrder.AfterCalls);
            Assert.Equal(new[] { "before:0", "after:42" }, CbLog.Entries);
        }

        [Fact]
        public void Generated_CallbackSeesParentSiblingSharedAndCycle()
        {
            var child = new CbChild { Name = "child" };
            var parent = new CbParent { Name = "parent", First = child, Second = child };
            parent.Children.Add(child);
            child.Parent = parent;

            var bytes = Generated().Save(parent, 1);

            CbGraphCounters.ChildCallbacks = 0;
            var loaded = Generated().Load<CbParent>(bytes, 1);

            Assert.Equal("parent", loaded.First.ObservedParentName);
            Assert.Equal(1, loaded.First.ObservedSiblingCount);
            Assert.True(loaded.First.ObservedCycle, "callback should see the populated cycle");
            Assert.Same(loaded.First, loaded.Second);
            Assert.Same(loaded.First, loaded.Children[0]);
            Assert.Same(loaded, loaded.First.Parent);
            Assert.Equal(1, CbGraphCounters.ChildCallbacks); // shared reference: exactly once
        }

        [Fact]
        public void Generated_ReusedObject_CallbackFiresOnce()
        {
            var child = new CbChild { Name = "child" };
            var parent = new CbParent { Name = "parent", First = child, Second = child };
            parent.Children.Add(child);
            child.Parent = parent;
            var bytes = Generated().Save(parent, 1);

            var existingChild = new CbChild { Name = "stale" };
            var existing = new CbParent { Name = "stale", First = existingChild, Second = existingChild };
            existing.Children.Add(existingChild);
            existingChild.Parent = existing;

            CbGraphCounters.ChildCallbacks = 0;
            Generated().Load(ref existing, bytes, 1);

            Assert.Same(existingChild, existing.First);
            Assert.Equal("parent", existing.First.ObservedParentName);
            Assert.Equal(1, existing.First.ObservedSiblingCount);
            Assert.Equal(1, CbGraphCounters.ChildCallbacks);
        }

        [Fact]
        public void InheritedProtectedCallback_RunsOnce()
        {
            var bytes = Generated().Save(new CbInheritDerived { Id = 5 }, 1);
            CbInheritCounters.BaseCalls = 0;
            CbInheritCounters.DerivedCalls = 0;

            var loaded = Generated().Load<CbInheritDerived>(bytes, 1);

            Assert.Equal(5, loaded.Id);
            Assert.Equal(1, CbInheritCounters.BaseCalls);
            Assert.Equal(0, CbInheritCounters.DerivedCalls);
        }

        [Fact]
        public void OverriddenCallback_RunsOnce_AndShadowsBase()
        {
            var bytes = Generated().Save(new CbInheritOverride { Id = 5 }, 1);
            CbInheritCounters.BaseCalls = 0;
            CbInheritCounters.DerivedCalls = 0;

            var loaded = Generated().Load<CbInheritOverride>(bytes, 1);

            Assert.Equal(5, loaded.Id);
            Assert.Equal(0, CbInheritCounters.BaseCalls);
            Assert.Equal(1, CbInheritCounters.DerivedCalls);
        }

        [Fact]
        public void PrivateBaseCallback_RestoresOwnerAndSibling_BaseFactoryShape()
        {
            var ownerA = new CbOwner { Name = "a" };
            var ownerB = new CbOwner { Name = "b" };
            var factoryA = new CbFactory { Name = "A", Owner = ownerB };
            var factoryB = new CbFactory { Name = "B", Owner = ownerA };
            factoryA.Sibling = factoryB;
            factoryB.Sibling = factoryA;

            var bytes = Generated().Save(factoryA, 1);

            CbFactoryCounters.RestoreCalls = 0;
            var loaded = Generated().Load<CbFactory>(bytes, 1);

            Assert.True(loaded.DelegatesRestored);
            Assert.Same(loaded, loaded.Owner.OwnerRef);
            Assert.NotNull(loaded.Sibling);
            Assert.True(loaded.Sibling.DelegatesRestored);
            Assert.Same(loaded.Sibling, loaded.Sibling.Owner.OwnerRef);
            Assert.Equal(2, CbFactoryCounters.RestoreCalls); // both factories, once each
        }

        [Fact]
        public void MigrationContextArgument_FastPathIsNone_SlowPathIsMigrated()
        {
            var bytes = Generated().Save(new CbMigrationContextModel { Value = 1 }, 1);

            CbContextCounters.SawNone = false;
            CbContextCounters.SawMigrated = false;
            var fast = Generated().Load<CbMigrationContextModel>(bytes, 1);
            Assert.Equal(1, fast.Value);
            Assert.True(CbContextCounters.SawNone);
            Assert.False(CbContextCounters.SawMigrated);

            CbContextCounters.SawNone = false;
            CbContextCounters.SawMigrated = false;
            var slow = Generated().Load<CbMigrationContextModel>(SlowPath(bytes), 1);
            Assert.Equal(1, slow.Value);
            Assert.True(CbContextCounters.SawMigrated);
            Assert.False(CbContextCounters.SawNone);
        }

        [Fact]
        public void SerializerArgument_ReceivesSerializerAndUserContext()
        {
            var bytes = Generated().Save(new CbSerializerArgModel { Value = 7 }, 1);

            CbContextCounters.SerializerCalls = 0;
            CbContextCounters.SeenUserContext = null;

            var serializer = Generated();
            var userContext = new object();
            serializer.UserContext = userContext;
            var loaded = serializer.Load<CbSerializerArgModel>(bytes, 1);

            Assert.Equal(7, loaded.Value);
            Assert.Equal(1, CbContextCounters.SerializerCalls);
            Assert.Same(userContext, CbContextCounters.SeenUserContext);
        }

        [Fact]
        public void Interpreter_CallbackSeesFullyPopulatedGraph()
        {
            var child = new CbChild { Name = "child" };
            var parent = new CbParent { Name = "parent", First = child, Second = child };
            parent.Children.Add(child);
            child.Parent = parent;
            var bytes = SlowPath(Generated().Save(parent, 1));

            CbGraphCounters.ChildCallbacks = 0;
            var loaded = Generated().Load<CbParent>(bytes, 1);

            Assert.Equal("parent", loaded.First.ObservedParentName);
            Assert.Equal(1, loaded.First.ObservedSiblingCount);
            Assert.True(loaded.First.ObservedCycle);
            Assert.Same(loaded.First, loaded.Second);
            Assert.Equal(1, CbGraphCounters.ChildCallbacks);
        }

        [Fact]
        public void StructCallback_RunsInline()
        {
            var bytes = Generated().Save(new CbStructRoot { Item = new CbStruct { Value = 9 } }, 1);
            CbStructCounters.Calls = 0;

            var loaded = Generated().Load<CbStructRoot>(bytes, 1);

            Assert.Equal(9, loaded.Item.Value);
            Assert.Equal(1, CbStructCounters.Calls);
        }

        [Fact]
        public void GenericCallback_FreshAndReused_RunsOnce()
        {
            var bytes = Generated().Save(new CbGeneric<int> { Value = 3 }, 1);

            CbGeneric<int>.Calls = 0;
            var loaded = Generated().Load<CbGeneric<int>>(bytes, 1);
            Assert.Equal(3, loaded.Value);
            Assert.Equal(1, CbGeneric<int>.Calls);

            var existing = new CbGeneric<int> { Value = 9 };
            CbGeneric<int>.Calls = 0;
            Generated().Load(ref existing, bytes, 1);
            Assert.Equal(3, existing.Value);
            Assert.Equal(1, CbGeneric<int>.Calls);
        }
    }
}
