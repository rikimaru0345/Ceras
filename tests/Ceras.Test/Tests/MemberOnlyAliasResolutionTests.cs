// The invariant behind a renamed type that only the *bake* can register.
//
// A shape that is reachable only as a member of a declared type is not part of the generated
// manifest: the bake walks every field/element/argument name and registers what it finds there, and
// SchemaRegistry.Register is also where a [PreviousTypeName] alias is indexed. So until the manifest
// is baked, a *renamed* member-only type is invisible by its old name — while its *current* name
// still resolves, because the context's configured type binder can name a type this manifest does not
// hold (SchemaRegistry.TryResolveType step 3, and the binder is installed by CerasRobustConfig.Seal).
// That asymmetry is what made the missing bake invisible: the rename worked, but only because Load
// happened to seal the registry before it built the file's id map, and that ordering was nowhere but a
// comment. The asymmetry survived the removal of the old implicit loaded-assembly scan, but the reason
// it survives is now an explicit, configured door rather than something that happened to be in the
// process: which types a name stands for is now a fact about the context (its manifest, Type.GetType,
// its binder), not about whatever assemblies happen to be loaded.
//
// The Banned namespace below is that shape, reached the way the generator produces it: CollectMinimalTypes
// leaves enums to the runtime ("runtime derives enum schemas"), so a member enum the generator has no
// entry for gets no registration at all and only the bake can register it. The blacklist is how a
// member type ends up in that state without leaving the context — an engine type with a custom
// formatter is the real-world case. The pattern matches only this namespace.
//
// What is pinned here:
//   * the asymmetry itself, for a manifest the generator actually produced: with a binder configured
//     the current name resolves before the bake and the old name does not;
//   * the other half of it, with the binder removed: neither name resolves, and the bake cannot even
//     discover the member - so the missing bake is now impossible to miss;
//   * that the asymmetry no longer rests on an implicit loaded-assembly scan, so the current name's
//     pre-bake resolvability is now a consequence of Config.TypeBinder rather than of the process;
//   * the guard: CerasRobustSerializer.BuildIdToType — the one place a file's ids are mapped onto the
//     current model — calls SchemaRegistry.RequireBakedManifest() before it resolves anything, so
//     reordering the seal fails loudly instead of silently mis-resolving a renamed member-only type;
//   * the golden fixture through both that boundary and the real reader (Load), so the rename is
//     proved on the path a save actually takes;
//   * what the guard is *not*: a non-root type the file describes and this build no longer has stays
//     unmapped (an old file may reference a removed type, and its graph is skipped structurally),
//     while a stored root that is not the requested one is still rejected by the existing root check.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;
using Ceras.Test.HistoricalFixtures;
using Xunit;

// A member type the generator must not register; only the bake can.
[assembly: CerasGeneratorBlacklist("Ceras.Test.Banned*")]

namespace Ceras.Test.Banned
{
    /// <summary>
    /// A renamed enum that is reachable only as <see cref="Holder"/>'s member. The generated manifest
    /// has no entry for it, so the bake registers it — and, with it, the rename alias below.
    /// </summary>
    [PreviousTypeName("Ceras.Test.Banned.OldTier")]
    public enum Tier
    {
        Bronze = 0,
        Gold = 1,
    }

    [CerasContext("Banned")]
    [CerasSerializable]
    public class Holder
    {
        public Tier Level;
        public int Count;
    }
}

namespace Ceras.Test
{
    public class MemberOnlyAliasResolutionTests
    {
        const string HistoricalContext = "HistoricalCurrent";

        // v1's name for the enum the current model calls Rank, and v1's name for the declaring type.
        const string OldRankName = "Ceras.HistoricalWriter.V1.Rank";
        const string OldPlayerName = "Ceras.HistoricalWriter.V1.Player";
        const string OldTierName = "Ceras.Test.Banned.OldTier";

        static string CurrentTierName => TypeIdentity.GetCanonicalName(typeof(Banned.Tier));

        [Fact]
        public void A_Renamed_Member_Only_Type_Resolves_Its_Old_Name_Only_After_The_Bake()
        {
            var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Banned"));
            var registry = serializer.Config.SchemaRegistry;

            // The generated manifest did not register the enum: the declaring type is there, its
            // member-only enum is not.
            Assert.True(registry.IsDeclared(typeof(Banned.Holder)));
            Assert.False(registry.IsDeclared(typeof(Banned.Tier)));
            Assert.False(registry.TryGetId(typeof(Banned.Tier), out _));

            // With the context's binder taken away, *neither* name resolves before the bake, and that
            // is the change the removed assembly scan made. The old implicit scan used to answer the
            // current name - a name the manifest does not hold and Type.GetType does not know - so the
            // two names used to differ. With no binder, there is no third door: the enum is not in the
            // manifest, so nothing stands for it under either spelling. The old name still cannot
            // resolve, because it exists only as the alias a *registration* indexes and only the bake
            // registers a member-only shape.
            registry.SetTypeBinder(null);
            Assert.False(registry.TryResolveType(CurrentTierName, out _));
            Assert.False(registry.TryResolveType(OldTierName, out _));

            // Re-installing the configured binder brings the current name back, through the one
            // explicit door that is left: Config.TypeBinder, installed by CerasRobustConfig.Seal. So
            // the asymmetry survives, but it is now a fact about what the owner configured rather than
            // about whatever assemblies happened to be loaded - and the old name still has no such
            // door, which is why only the bake can ever answer for it.
            registry.SetTypeBinder(serializer.Config.TypeBinder);
            Assert.True(registry.TryResolveType(CurrentTierName, out var byCurrentName));
            Assert.Equal(typeof(Banned.Tier), byCurrentName);
            Assert.False(registry.TryResolveType(OldTierName, out _));

            registry.Seal();

            // The bake registered the member-only shape, and Register indexed its rename alias.
            Assert.True(registry.TryGetId(typeof(Banned.Tier), out int bakedId));
            Assert.True(bakedId >= WellKnownTypes.UserTypeBase);
            Assert.True(registry.TryResolveType(OldTierName, out var byOldName));
            Assert.Equal(typeof(Banned.Tier), byOldName);

            Assert.True(registry.TryGetInfo(typeof(Banned.Tier), out var info));
            Assert.Equal(SchemaTypeKind.Enum, info.Kind);
            Assert.Contains(OldTierName, info.PreviousTypeNames);
        }

        [Fact]
        public void With_No_Binder_The_Current_Name_Resolves_Neither_Before_Nor_During_The_Bake()
        {
            // The complement of the asymmetry above, stated on its own. Take the binder away and the
            // manifest is the only door left, so the current name - which no manifest entry holds and
            // Type.GetType cannot know, because it is a bare user name in an assembly the runtime does
            // not search by default - resolves to nothing. Before the scan was removed this name
            // resolved anyway, which is exactly what let a missing bake go unnoticed.
            var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Banned"));
            var registry = serializer.Config.SchemaRegistry;
            registry.SetTypeBinder(null);

            Assert.False(registry.TryResolveType(CurrentTierName, out _));
            Assert.False(registry.TryResolveType(OldTierName, out _));

            // The bake needs the same answer, for the same reason: it walks the field name
            // 'Ceras.Test.Banned.Tier' in order to register the shape it describes, and with no binder
            // nothing resolves that name. So this is not a manifest that bakes with a hole in it - the
            // member-only shape cannot be discovered at all, and the bake refuses it by name rather
            // than writing a schema section that omits the field.
            var ex = Assert.Throws<CerasSchemaException>(() => registry.Seal());
            Assert.Contains(CurrentTierName, ex.Message, StringComparison.Ordinal);
            Assert.Contains("does not resolve", ex.Message, StringComparison.Ordinal);

            // With the binder back, the very same manifest bakes: the binder names the member, the bake
            // registers the shape, and Register then claims both of the type's names, so the manifest
            // itself answers for it from then on.
            registry.SetTypeBinder(serializer.Config.TypeBinder);
            registry.Seal();

            Assert.True(registry.TryResolveType(CurrentTierName, out var byCurrentName));
            Assert.Equal(typeof(Banned.Tier), byCurrentName);
            Assert.True(registry.TryResolveType(OldTierName, out var byOldName));
            Assert.Equal(typeof(Banned.Tier), byOldName);
        }

        [Fact]
        public void The_Shape_Itself_Still_Round_Trips()
        {
            // The member-only enum is an ordinary value in an ordinary save; the point of the tests
            // around it is only about *identities*, so the payload path is pinned as well.
            var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Banned"));

            var bytes = serializer.Save(new Banned.Holder { Level = Banned.Tier.Gold, Count = 3 }, 1);
            var loaded = serializer.Load<Banned.Holder>(bytes, 1);

            Assert.Equal(Banned.Tier.Gold, loaded.Level);
            Assert.Equal(3, loaded.Count);
        }

        [Fact]
        public void Resolving_A_Files_Ids_Requires_A_Baked_Manifest()
        {
            // This is what a reordered Load would hit: the file's ids are resolved before the registry
            // is sealed. BuildIdToType guards that itself, so the boundary reports the cause instead of
            // handing back a map with a hole in it and failing somewhere else entirely.
            var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Banned"));
            var registry = serializer.Config.SchemaRegistry;

            var ex = Assert.Throws<CerasSchemaException>(() => CerasRobustSerializer.BuildIdToType(
                FileSchemaNamingTheOldEnum(), registry, serializer.Config.TypeBinder));

            Assert.Contains("manifest is not baked", ex.Message);
            Assert.Contains("Seal()", ex.Message);           // the fix
            Assert.Contains("member", ex.Message);           // why the bake matters
            Assert.Contains("[PreviousTypeName]", ex.Message); // what silently breaks without it

            registry.Seal();

            // Sealed and baked: the same call succeeds, and the file's id maps onto the current type.
            var idToType = CerasRobustSerializer.BuildIdToType(
                FileSchemaNamingTheOldEnum(), registry, serializer.Config.TypeBinder);

            Assert.Equal(typeof(Banned.Tier), idToType[FileOldTierId]);
        }

        [Fact]
        public void The_Guard_Only_Passes_After_The_Seal()
        {
            var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Banned"));
            var registry = serializer.Config.SchemaRegistry;

            Assert.Throws<CerasSchemaException>(() => registry.RequireBakedManifest());

            registry.Seal();

            registry.RequireBakedManifest();   // no exception, and idempotent
            registry.RequireBakedManifest();
        }

        [Fact]
        public void The_Golden_Fixtures_Renamed_Enum_Resolves_On_The_Real_Reader_Path()
        {
            var fileSchemas = ReadFileSchema("PlayerCycle.bin");
            Assert.True(fileSchemas.ContainsKey(OldRankName));

            // The reader path first: a real Load of the checked-in bytes, where v1's Rank value
            // (ulong.MaxValue, spelled Max) has to come back as the current Highest.
            var loaded = Reader().Load<Actor>(FixtureBytes("PlayerCycle.bin"), 1);
            Assert.Equal(Rank.Highest, loaded.Rank);

            // Then the boundary underneath it, with the file's own schema read out of those same bytes:
            // v1's Rank id maps to the current Rank, v1's Player id to the current Actor, and every id
            // the file declares is accounted for, so the map cannot be quietly incomplete.
            var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters(HistoricalContext));
            var registry = serializer.Config.SchemaRegistry;
            registry.Seal();

            var idToType = CerasRobustSerializer.BuildIdToType(fileSchemas, registry, serializer.Config.TypeBinder);

            Assert.Equal(typeof(Rank), idToType[fileSchemas[OldRankName].TypeId]);
            Assert.Equal(typeof(Actor), idToType[fileSchemas[OldPlayerName].TypeId]);
            foreach (var schema in fileSchemas.Values)
                Assert.True(idToType.ContainsKey(schema.TypeId),
                    $"file type id {schema.TypeId} ('{schema.StableName}') did not resolve");
        }

        [Fact]
        public void An_Unmapped_NonRoot_Type_Is_Tolerated_While_The_Stored_Root_Is_Still_Checked()
        {
            // The guard is about the bake order, not about strictness: an old file may describe a type this
            // build no longer has, and that identity is left unmapped so the engine can skip its graph
            // structurally. Take the real fixture's schema and add one entry for a type that does not
            // exist here, with a binder that reports "no such type" the way a binder legitimately can
            // (see SchemaTraversalEngine.ResolveFullName).
            var fileSchemas = ReadFileSchema("PlayerCycle.bin");
            const string removedName = "Ceras.Test.HistoricalFixtures.TypeRemovedInThisBuild";
            const int removedId = 200;
            fileSchemas[removedName] = new OldTypeSchema(
                typeId: removedId, stableName: removedName, previousNames: Array.Empty<string>(),
                kind: SchemaTypeKind.Class, fields: Array.Empty<OldFieldSchema>(), fixedByteSize: 0,
                definitionTypeId: 0, argTypeIds: null, enumValues: null);

            var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters(HistoricalContext));
            var registry = serializer.Config.SchemaRegistry;
            registry.Seal();

            var idToType = CerasRobustSerializer.BuildIdToType(fileSchemas, registry, new NullReturningBinder());

            // The removed type is simply absent - not an error, and not something that disturbs the rest.
            Assert.False(idToType.ContainsKey(removedId));
            // Everything the file can still name resolved, rename aliases included.
            Assert.Equal(typeof(Rank), idToType[fileSchemas[OldRankName].TypeId]);
            Assert.Equal(typeof(Actor), idToType[fileSchemas[OldPlayerName].TypeId]);

            // The root is the identity that must match, and that check is unchanged: this file's stored
            // root is a v1 Player, so asking for an Inventory is refused rather than misread.
            var ex = Assert.Throws<CerasSchemaException>(
                () => Reader().Load<HistoricalFixtures.Inventory>(FixtureBytes("PlayerCycle.bin"), 1));
            Assert.Contains("does not match the requested load type", ex.Message);
        }

        /// <summary>
        /// A binder that answers "no such type" with null. Returning null and throwing are the two ways an
        /// <see cref="ITypeBinder"/> reports it, and both are normalized by the readers.
        /// </summary>
        sealed class NullReturningBinder : ITypeBinder
        {
            public string GetBaseName(Type type) => type.FullName;
            public Type GetTypeFromBase(string baseTypeName) => null;
            public Type GetTypeFromBaseAndArguments(string baseTypeName, params Type[] genericTypeArguments) => null;
        }

        const int FileOldTierId = 140;

        /// <summary>A file's schema section naming v1's enum, shaped like the one the writer wrote.</summary>
        static Dictionary<string, OldTypeSchema> FileSchemaNamingTheOldEnum()
        {
            return new Dictionary<string, OldTypeSchema>(StringComparer.Ordinal)
            {
                [OldTierName] = new OldTypeSchema(
                    typeId: FileOldTierId,
                    stableName: OldTierName,
                    previousNames: Array.Empty<string>(),
                    kind: SchemaTypeKind.Enum,
                    fields: null!,
                    fixedByteSize: 0,
                    definitionTypeId: WellKnownTypes.Int32,
                    argTypeIds: null!,
                    enumValues: new[] { new OldEnumValue("Bronze", 0), new OldEnumValue("Gold", 1) }),
            };
        }

        static CerasRobustSerializer Reader() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(HistoricalContext));

        static byte[] FixtureBytes(string fixtureName)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "HistoricalV1", fixtureName);
            Assert.True(File.Exists(path), $"missing golden fixture '{fixtureName}' at '{path}'");
            return File.ReadAllBytes(path);
        }

        /// <summary>
        /// The file's own schema section, read out of a checked-in golden fixture exactly as Load does.
        /// Reading it here rather than hand-building it keeps the ids and names real, so the resolution
        /// below is the one the load path performs.
        /// </summary>
        static Dictionary<string, OldTypeSchema> ReadFileSchema(string fixtureName)
        {
            byte[] data = FixtureBytes(fixtureName);

            Ceras.RobustHeader.Read(data, out _, out _, out long schemaStringOffset,
                out long dataSectionOffset, out _, out _);

            var schemaStrings = StringTable.Load(data, (int)schemaStringOffset,
                (int)dataSectionOffset - (int)schemaStringOffset, StringTableCompression.Disabled);

            var sequence = new ReadOnlySequence<byte>(data, Ceras.RobustHeader.Size,
                (int)schemaStringOffset - Ceras.RobustHeader.Size);
            var reader = new SequenceReader<byte>(sequence);
            return SchemaSection.Read(ref reader, schemaStrings);
        }
    }
}
