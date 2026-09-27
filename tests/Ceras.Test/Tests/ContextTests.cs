using System;
using Xunit;
using Ceras;
using Ceras.Generated;

// An assembly-level known type scoped to a single context (the optional [CerasKnownType] context
// argument). KnownOnlyType is otherwise hidden from member-walking, so it can only enter "SaveGame"
// through this declaration.
[assembly: CerasKnownType<System.Collections.Generic.List<Ceras.Test.Contexts.KnownOnlyType>>("SaveGame")]

namespace Ceras.Test.Contexts
{
    // ---------------------------------------------------------------------------------------------
    // Multi-context behavior (TODO Phase 2 "Contexts").
    //
    // Two self-contained contexts with different roots. A type is registered only in the context(s)
    // it is a root of (or reachable from), so the two contexts have independent manifests, type-ID
    // spaces, and layout fingerprints.
    // ---------------------------------------------------------------------------------------------

    [CerasContext("SaveGame")]
    [CerasSerializable]
    public class SaveRoot
    {
        public int Version;
        public SharedEntity Entity;
    }

    // A root of SaveGame only, and deliberately without [CerasSerializable].
    [CerasContext("SaveGame")]
    public class SaveOnlyType
    {
        public int Value;
    }

    [CerasContext("Network")]
    [CerasSerializable]
    public class NetworkRoot
    {
        public string Text;
        public SharedEntity Entity;
    }

    // A root of Network only.
    [CerasContext("Network")]
    public class NetworkOnlyType
    {
        public int Value;
    }

    // Declared as a root of BOTH contexts. Each context gets its own manifest entry for it.
    [CerasContext("SaveGame")]
    [CerasContext("Network")]
    [CerasSerializable]
    public class SharedEntity
    {
        public int Id;
        public string Name;
    }

    // Root of Network only, and deliberately without [CerasSerializable]: proves a [CerasContext]
    // root is serializable on its own.
    [CerasContext("Network")]
    public class ContextOnlyRoot
    {
        public int N;
        public SharedEntity Entity;
    }

    /// <summary>
    /// Not [CerasSerializable] and not referenced by any member; reachable only through the
    /// context-scoped <c>[assembly: CerasKnownType&lt;List&lt;KnownOnlyType&gt;&gt;("SaveGame")]</c>.
    /// </summary>
    public class KnownOnlyType
    {
        public int Value;
    }

    public class ContextTests
    {
        static CerasRobustSerializer SaveGame() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("SaveGame"));
        static CerasRobustSerializer Network() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Network"));

        [Fact]
        public void ContextNames_ListsEveryDeclaredContext()
        {
            Assert.Contains("Tests", RobustFormatters.ContextNames);
            Assert.Contains("SaveGame", RobustFormatters.ContextNames);
            Assert.Contains("Network", RobustFormatters.ContextNames);
        }

        [Fact]
        public void UnknownContext_ThrowsActionableErrorListingAvailableContexts()
        {
            var ex = Assert.Throws<ArgumentException>(
                () => new CerasRobustSerializer(c => c.UseGeneratedFormatters("DoesNotExist")));

            Assert.Contains("DoesNotExist", ex.Message);
            Assert.Contains("SaveGame", ex.Message);
            Assert.Contains("Network", ex.Message);
        }

        [Fact]
        public void ContextRoot_WithoutCerasSerializable_RoundTrips()
        {
            var serializer = Network();
            var bytes = serializer.Save(new ContextOnlyRoot { N = 7, Entity = new SharedEntity { Id = 1, Name = "e" } }, 1);

            var clone = serializer.Load<ContextOnlyRoot>(bytes, 1);
            Assert.Equal(7, clone.N);
            Assert.Equal(1, clone.Entity.Id);
            Assert.Equal("e", clone.Entity.Name);
        }

        [Fact]
        public void Contexts_HaveIndependentManifestsAndFingerprints()
        {
            var save = SaveGame();
            var net = Network();

            // Roots (and reachable types) are per context.
            Assert.True(save.Config.SchemaRegistry.TryGetInfo(typeof(SaveOnlyType), out _));
            Assert.False(net.Config.SchemaRegistry.TryGetInfo(typeof(SaveOnlyType), out _));

            Assert.True(net.Config.SchemaRegistry.TryGetInfo(typeof(NetworkOnlyType), out _));
            Assert.False(save.Config.SchemaRegistry.TryGetInfo(typeof(NetworkOnlyType), out _));

            // A type in both contexts appears in both.
            Assert.True(save.Config.SchemaRegistry.TryGetInfo(typeof(SharedEntity), out _));
            Assert.True(net.Config.SchemaRegistry.TryGetInfo(typeof(SharedEntity), out _));

            // Different type sets -> different layout fingerprints.
            Assert.NotEqual(
                save.Config.SchemaRegistry.ComputeFingerprint(),
                net.Config.SchemaRegistry.ComputeFingerprint());
        }

        [Fact]
        public void RootOnlyInOtherContext_Save_FailsWithUndeclaredTypeMessage()
        {
            // SaveOnlyType belongs to SaveGame, not Network.
            var saveEx = Assert.Throws<Ceras.Exceptions.CerasException>(
                () => Network().Save(new SaveOnlyType { Value = 1 }, 1));
            Assert.Contains("not declared", saveEx.Message);
            Assert.Contains("CerasContext", saveEx.Message);

            // NetworkOnlyType belongs to Network, not SaveGame.
            var netEx = Assert.Throws<Ceras.Exceptions.CerasException>(
                () => SaveGame().Save(new NetworkOnlyType { Value = 1 }, 1));
            Assert.Contains("not declared", netEx.Message);
            Assert.Contains("CerasContext", netEx.Message);
        }

        [Fact]
        public void KnownType_WithExplicitContext_IsRegisteredOnlyInThatContext()
        {
            // [assembly: CerasKnownType<List<KnownOnlyType>>("SaveGame")] adds the hidden closure to
            // SaveGame only, not to every context.
            Assert.True(SaveGame().Config.SchemaRegistry.TryGetInfo(typeof(KnownOnlyType), out _));
            Assert.False(Network().Config.SchemaRegistry.TryGetInfo(typeof(KnownOnlyType), out _));

            var bytes = SaveGame().Save(new KnownOnlyType { Value = 5 }, 1);
            Assert.Equal(5, SaveGame().Load<KnownOnlyType>(bytes, 1).Value);
        }

        [Fact]
        public void SharedType_RoundTripsUnderBothContexts()
        {
            var save = SaveGame();
            var net = Network();

            var saveBytes = save.Save(new SharedEntity { Id = 1, Name = "save" }, 1);
            var saveClone = save.Load<SharedEntity>(saveBytes, 1);
            Assert.Equal(1, saveClone.Id);
            Assert.Equal("save", saveClone.Name);

            var netBytes = net.Save(new SharedEntity { Id = 2, Name = "net" }, 1);
            var netClone = net.Load<SharedEntity>(netBytes, 1);
            Assert.Equal(2, netClone.Id);
            Assert.Equal("net", netClone.Name);
        }

        [Fact]
        public void SaveFromOtherContext_CannotBeLoaded()
        {
            var save = SaveGame();
            var bytes = save.Save(new SaveRoot
            {
                Version = 3,
                Entity = new SharedEntity { Id = 9, Name = "x" }
            }, 1);

            // SaveRoot is not part of the Network manifest.
            var ex = Assert.Throws<Ceras.Exceptions.CerasException>(
                () => Network().Load<SaveRoot>(bytes, 1));
            Assert.Contains("not declared", ex.Message);
            Assert.Contains("CerasContext", ex.Message);
        }
    }
}
