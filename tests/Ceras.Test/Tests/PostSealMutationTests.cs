// CLEANUP_TODO item 2: configuration that is still accepted after Seal().
//
// A serializer seals its config in its constructor and settles there what it will do with every type
// it will meet. A change made after that used to be accepted with two different outcomes - ignored for
// a type the serializer had already resolved, applied for one it had not - or, for the collections a
// config hands out, accepted into a live List that nothing consults any more. Both are the worst
// available outcome: the code reads as though it took effect, and nothing in the payload agrees.
//
// What is pinned here, in four groups:
//   1. CerasConfig.OnResolveFormatter refuses every change after the seal, including through a
//      reference the caller kept from before it (a read-only property would not have been enough -
//      the reference outlives the seal, and List<T> cannot be intercepted because its methods are
//      not virtual).
//   2. ConfigType / ConfigStaticType / ConfigType<T> still *return* an existing configuration after
//      the seal, so reading a serializer's configuration is not broken, and refuse to create one for
//      a type that has none. Every mutation of the configuration they return is refused.
//   3. TypeConfig<T>.MapParameters and the member list are refused too, and the member list is no
//      longer a List<MemberConfig> a caller can cast back to.
//   4. The two documented exceptions still work: OnConfigNewType still configures the one type it is
//      handed (and only that one, and only while the callback runs), and a robust context's
//      DeclareRoot* is unaffected - the manifest seals later, at the first Save/Load.
//
// Every door has a control: the same change made before the seal still works, because a refusal that
// also broke legal configuration would be a different bug.
//
// Nothing here is declared [CerasSerializable] or carries a context: every serializer below is built
// without a generated manifest and resolves through the reflection fallback, so this file cannot move
// the bytes any other test pins. The one robust case uses a declared array root, which needs no
// manifest entry either.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ceras;
using Ceras.Exceptions;
using Ceras.Formatters;
using Xunit;

namespace Ceras.Test.Tests
{
    // ── Models (no attributes: the reflection fallback builds these) ────────────────────────────

    public class SealDoorModel
    {
        public int Number;
        public string Text;
    }

    /// <summary>Only ever reached through <c>OnConfigNewType</c> after the seal, never configured up front.</summary>
    public class LateDiscoveredModel
    {
        public int Value;
    }

    public class LateDiscoveryHolder
    {
        public LateDiscoveredModel Inner;
    }

    /// <summary>Configured before the seal, so the post-seal callback must not be able to reach it.</summary>
    public class ConfiguredBeforeSealModel
    {
        public int Value;
    }

    /// <summary>
    /// Constructor parameters whose names match no member, so the automatic mapping cannot work and
    /// only an explicit <c>MapParameters</c> can construct it.
    /// </summary>
    public class ExplicitParameterMapModel
    {
        public int Number;
        public string Text;

        public ExplicitParameterMapModel(int n, string t)
        {
            Number = n;
            Text = t;
        }
    }

    /// <summary>For the member-list control: one member is dropped from the list before the seal.</summary>
    public class MemberListModel
    {
        public int Kept;
        public int Dropped;
    }

    /// <summary>Counts what a custom formatter did, so a test can tell a custom formatter from the built-in.</summary>
    public class CountingFormatter<T> : IFormatter<T>
    {
        public int Serializations;
        public int Deserializations;

        public void Serialize(IBufferWriter<byte> writer, T value)
        {
            Serializations++;
            SerializerBinary.WriteInt32Fixed(writer, 0x5A5A);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref T value)
        {
            Deserializations++;
            value = default!;
        }

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (T)value);
        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member) { }
    }

    public class PostSealMutationTests
    {
        // ── 1. OnResolveFormatter ────────────────────────────────────────────────────────────

        /// <summary>
        /// The serializer snapshots this list in its constructor, after sealing the config, so an
        /// entry added afterwards is not consulted for the rest of the process. Refused, not ignored.
        /// </summary>
        [Fact]
        public void ResolverList_Add_AfterSeal_Refuses()
        {
            var ceras = new CerasFastSerializer();

            var ex = Assert.Throws<InvalidOperationException>(
                () => ceras.Config.OnResolveFormatter.Add((s, t) => null));

            // Names the member the caller reached for, and says what to do instead.
            Assert.Contains(nameof(CerasConfig.OnResolveFormatter), ex.Message, StringComparison.Ordinal);
            Assert.Contains("configure", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Every mutating operation, not just <c>Add</c> - the point of the guarded list is that no
        /// path into it works, including the indexer and the ones that would be a no-op for an item
        /// that is not in the list.
        /// </summary>
        [Fact]
        public void ResolverList_EveryMutator_Refuses_AfterSeal_AndChangesNothing()
        {
            var config = new CerasRawConfig();
            config.OnResolveFormatter.Add((s, t) => null);   // one entry, so Remove/RemoveAt/indexer have a target
            var ceras = new CerasFastSerializer(config);

            var list = ceras.Config.OnResolveFormatter;
            Assert.Single(list);

            var attempts = new (string Name, Action Change)[]
            {
                ("Add", () => list.Add((s, t) => null)),
                ("Insert", () => list.Insert(0, (s, t) => null)),
                ("Remove(existing)", () => list.Remove(list[0])),
                ("Remove(missing)", () => list.Remove((s, t) => null)),
                ("RemoveAt", () => list.RemoveAt(0)),
                ("Clear", () => list.Clear()),
                ("indexer set", () => list[0] = (s, t) => null),
            };

            foreach (var (name, change) in attempts)
                Assert.Throws<InvalidOperationException>(change);

            // A refused change leaves the list exactly as it was - the refusal happens before the
            // change, not after undoing it.
            Assert.Single(list);
        }

        /// <summary>
        /// The case a read-only property would not have covered. A caller that read the property
        /// before the seal holds the very same object afterwards, so a guard the property applied once
        /// (or a copy) would leave this list fully mutable: a reference the caller kept is the door.
        /// </summary>
        [Fact]
        public void ResolverList_ReferenceKeptBeforeSeal_CannotChangeAfterSeal()
        {
            var config = new CerasRawConfig();
            var kept = config.OnResolveFormatter;

            // Before the seal it is a normal writable list - the control, and what makes the
            // post-seal refusal a change of behaviour rather than a list that never worked.
            kept.Add((s, t) => null);
            Assert.Single(kept);

            var ceras = new CerasFastSerializer(config);

            Assert.Throws<InvalidOperationException>(() => kept.Add((s, t) => null));
            Assert.Throws<InvalidOperationException>(() => kept.Clear());

            // Reading is unaffected, and the two references are the same object: enumerating the one
            // the caller kept yields the entry the serializer has.
            var entry = Assert.Single(kept);
            Assert.Same(entry, ceras.Config.OnResolveFormatter[0]);
        }

        /// <summary>
        /// The control for the test above, in the other direction: an entry added through a reference
        /// kept before the seal is a real resolver and Ceras uses it.
        /// </summary>
        [Fact]
        public void ResolverList_EntryAddedBeforeSeal_IsUsed()
        {
            var config = new CerasRawConfig();
            var kept = config.OnResolveFormatter;
            var formatter = new CountingFormatter<SealDoorModel>();

            kept.Add((s, t) => t == typeof(SealDoorModel) ? formatter : null);

            var ceras = new CerasFastSerializer(config);
            ceras.Serialize(new SealDoorModel { Number = 1, Text = "x" });

            Assert.Equal(1, formatter.Serializations);
        }

        // ── 2. ConfigType / ConfigStaticType / ConfigType<T>() ─────────────────────────────

        /// <summary>
        /// Reading a serializer's configuration after it exists is legitimate, and the three overloads
        /// all keep working for a type that was configured before the seal. Only creating one - or
        /// changing one - is refused.
        /// </summary>
        [Fact]
        public void ConfigType_AfterSeal_ReturnsTheExistingConfiguration_ForReading()
        {
            var config = new CerasRawConfig();
            var beforeSeal = config.ConfigType<SealDoorModel>();
            var staticBeforeSeal = config.ConfigStaticType(typeof(SealDoorModel));

            var ceras = new CerasFastSerializer(config);
            var sealedConfig = ceras.Config;

            // Same instances: the read finds the entry, it does not build a second one.
            Assert.Same(beforeSeal, sealedConfig.ConfigType<SealDoorModel>());
            Assert.Same(beforeSeal, sealedConfig.ConfigType(typeof(SealDoorModel)));
            Assert.Same(staticBeforeSeal, sealedConfig.ConfigStaticType(typeof(SealDoorModel)));

            // And every read on it still answers.
            Assert.Equal(typeof(SealDoorModel), sealedConfig.ConfigType<SealDoorModel>().Type);
            Assert.Equal(new[] { "Number", "Text" },
                sealedConfig.ConfigType<SealDoorModel>().Members.Select(m => m.Member.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Null(sealedConfig.ConfigType<SealDoorModel>().CustomFormatter);
            Assert.NotNull(sealedConfig.TypeBinder);
            Assert.Equal(uint.MaxValue, sealedConfig.SizeLimits.MaxStringLength);
        }

        /// <summary>
        /// A type with no configuration cannot get one after the seal. It used to: the entry was
        /// created, and a change to it was then applied to this type and ignored for every type the
        /// serializer had already resolved.
        /// </summary>
        [Fact]
        public void ConfigType_AfterSeal_ForATypeThatWasNeverConfigured_Refuses()
        {
            var ceras = new CerasFastSerializer();

            Assert.Throws<InvalidOperationException>(() => ceras.Config.ConfigType<LateDiscoveredModel>());
            Assert.Throws<InvalidOperationException>(() => ceras.Config.ConfigType(typeof(LateDiscoveredModel)));
            Assert.Throws<InvalidOperationException>(() => ceras.Config.ConfigStaticType(typeof(LateDiscoveredModel)));

            // The message has to say which half it is - this type has no configuration, and the
            // config is sealed - and where to configure it instead.
            var ex = Assert.Throws<InvalidOperationException>(() => ceras.Config.ConfigType<LateDiscoveredModel>());
            Assert.Contains("no configuration", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("OnConfigNewType", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// An open generic was never configurable, and that answer does not change with the seal: it
        /// is a statement about the argument, not about when the call happened.
        /// </summary>
        [Fact]
        public void ConfigType_AfterSeal_ForAnOpenGeneric_StillReportsThat()
        {
            var ceras = new CerasFastSerializer();

            var ex = Assert.Throws<InvalidOperationException>(() => ceras.Config.ConfigType(typeof(List<>)));
            Assert.Contains("open generic", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Every way of changing a type's configuration after the seal is refused, with one exception
        /// type and one message - and the configuration is unchanged afterwards, so a refusal is not
        /// "applied then undone".
        /// </summary>
        [Fact]
        public void EveryTypeConfigMutation_AfterSeal_Refuses()
        {
            var config = new CerasRawConfig();
            config.ConfigType<SealDoorModel>();     // configured, so the read below finds it
            var ceras = new CerasFastSerializer(config);

            var tc = ceras.Config.ConfigType<SealDoorModel>();
            var member = tc.ConfigField(nameof(SealDoorModel.Number));
            Assert.NotNull(member);

            var attempts = new (string Name, Action Change)[]
            {
                ("CustomFormatter", () => tc.CustomFormatter = new CountingFormatter<SealDoorModel>()),
                ("CustomFormatter(null)", () => tc.CustomFormatter = null),
                ("SetFormatter", () => tc.SetFormatter(new CountingFormatter<SealDoorModel>())),
                ("CustomResolver", () => tc.CustomResolver = (s, t) => null),
                ("TypeConstruction", () => tc.TypeConstruction = TypeConstruction.ByUninitialized()),
                ("ConstructByUninitialized", () => tc.ConstructByUninitialized()),
                ("ConstructByDelegate", () => tc.ConstructByDelegate(() => new SealDoorModel())),
                ("MapParameters", () => tc.MapParameters(new Dictionary<ParameterInfo, MemberInfo>())),
                ("Member.Exclude()", () => member.Exclude()),
                ("Member.Include()", () => member.Include()),
                ("Member.PersistentName", () => member.PersistentName = "renamed"),
                ("Member.WriteBackOrder", () => member.WriteBackOrder = 7),
                ("Member.SerializationOverride", () => member.SerializationOverride = SerializationOverride.ForceInclude),
            };

            foreach (var (name, change) in attempts)
            {
                var ex = Assert.Throws<ConfigurationSealedException>(change);
                Assert.False(string.IsNullOrWhiteSpace(ex.Message), $"{name} was refused without saying why");
            }

            // Unchanged: nothing got through on the way to being refused.
            Assert.Null(tc.CustomFormatter);
            Assert.Null(tc.CustomResolver);
            Assert.Equal(nameof(SealDoorModel.Number), member.PersistentName);
            Assert.Equal(SerializationOverride.NoOverride, member.SerializationOverride);
            Assert.Equal(0, member.WriteBackOrder);
        }

        /// <summary>
        /// The control for the test above: the same change, before the serializer exists, is how a
        /// custom formatter is installed - and it is used. (The formatter here writes a fixed payload
        /// and produces <c>default</c>, so the round trip returns null for a class: what is asserted
        /// is that the custom formatter ran, not what came back.)
        /// </summary>
        [Fact]
        public void TypeConfigMutations_BeforeSeal_TakeEffect()
        {
            var formatter = new CountingFormatter<SealDoorModel>();

            var ceras = new CerasFastSerializer(c =>
                c.ConfigType<SealDoorModel>().SetFormatter(formatter));

            var clone = ceras.Deserialize<SealDoorModel>(ceras.Serialize(new SealDoorModel { Number = 3, Text = "abc" }));

            Assert.Equal(1, formatter.Serializations);
            Assert.Equal(1, formatter.Deserializations);
            Assert.Null(clone);
        }

        // ── 3. MapParameters and the member list ────────────────────────────────────────────

        /// <summary>
        /// The parameter mapping is part of the configuration and has no seal check of its own, so a
        /// mapping written after the type's schema was built was accepted and then consulted (or not)
        /// depending on when the reflection formatter happened to be built.
        /// </summary>
        [Fact]
        public void MapParameters_AfterTheTypeConfigSealed_Refuses()
        {
            var config = new CerasRawConfig();
            var tc = config.ConfigType<ExplicitParameterMapModel>();
            var ctor = typeof(ExplicitParameterMapModel).GetConstructors()[0];
            tc.ConstructBy(ctor);

            // Sealed the way the serializer seals it: on the type's first schema build.
            tc.Seal();

            var ex = Assert.Throws<ConfigurationSealedException>(() => tc.MapParameters(ParameterMapping()));
            Assert.Contains("SerializationSchema", ex.Message, StringComparison.Ordinal);
            Assert.Null(tc.ParameterMap);
        }

        /// <summary>
        /// The same call once the owning config is sealed - the door CLEANUP_TODO item 2 is about, and
        /// the one that used to be accepted: the map was written, and whether anything read it
        /// depended on whether the reflection formatter for this type had been built yet.
        /// </summary>
        [Fact]
        public void MapParameters_AfterTheConfigIsSealed_Refuses()
        {
            var config = new CerasRawConfig();
            var tc = config.ConfigType<ExplicitParameterMapModel>();
            tc.ConstructBy(typeof(ExplicitParameterMapModel).GetConstructors()[0]);

            var ceras = new CerasFastSerializer(config);

            var ex = Assert.Throws<ConfigurationSealedException>(() => ceras.Config.ConfigType<ExplicitParameterMapModel>().MapParameters(ParameterMapping()));
            Assert.Contains("CerasConfig", ex.Message, StringComparison.Ordinal);
            Assert.Null(tc.ParameterMap);
        }

        /// <summary>
        /// The control: before the seal the call is accepted and records what it was given (a copy, so
        /// a later change to the caller's dictionary is not a change to the configuration).
        /// <para>
        /// What is asserted here is the state transition, not a round trip, because the library's only
        /// reader of the map - <c>TypeConstruction.VerifyParameterMapping</c> - is reached only for a
        /// construction that takes parameters, and parameterized construction is refused outright
        /// ("the fixup system was removed"). So a map can be stored and cannot currently be consulted;
        /// what is being pinned is that storing one is a configuration change like any other, and is
        /// refused on the same schedule as the rest of them.
        /// </para>
        /// </summary>
        [Fact]
        public void MapParameters_BeforeSeal_RecordsTheMapping()
        {
            var config = new CerasRawConfig();
            var tc = config.ConfigType<ExplicitParameterMapModel>();
            tc.ConstructBy(typeof(ExplicitParameterMapModel).GetConstructors()[0]);

            var mapping = ParameterMapping();
            tc.MapParameters(mapping);

            Assert.NotNull(tc.ParameterMap);
            Assert.Equal(2, tc.ParameterMap.Count);
            Assert.Equal(nameof(ExplicitParameterMapModel.Number), tc.ParameterMap.Values.First().Name);

            // Copied, not aliased: the caller's dictionary is no longer the configuration's.
            mapping.Clear();
            Assert.Equal(2, tc.ParameterMap.Count);
        }

        static Dictionary<ParameterInfo, MemberInfo> ParameterMapping()
        {
            var parameters = typeof(ExplicitParameterMapModel).GetConstructors()[0].GetParameters();
            var fields = typeof(ExplicitParameterMapModel).GetFields();
            return new Dictionary<ParameterInfo, MemberInfo>
            {
                [parameters[0]] = fields[0],
                [parameters[1]] = fields[1],
            };
        }

        /// <summary>
        /// The member list is handed out typed as <c>IEnumerable&lt;MemberConfig&gt;</c>, and it used
        /// to be the live <c>List&lt;MemberConfig&gt;</c> - so a cast recovered a collection that
        /// accepted changes after the type's schema was built, which is the one edit that goes around
        /// every other check. The cast now fails, and the object that is handed out is guarded.
        /// </summary>
        [Fact]
        public void MemberList_CannotBeCastBackToAList()
        {
            var config = new CerasRawConfig();
            var tc = config.ConfigType<SealDoorModel>();
            var ceras = new CerasFastSerializer(config);
            tc.Seal();

            var members = tc.UnsafeGetAllMembersIncludingCompilerGenerated();

            Assert.IsNotType<List<MemberConfig>>(members);
            Assert.Throws<InvalidCastException>(() => (List<MemberConfig>)members);

            // What it does hand out is still a list, and it still refuses.
            var asList = (IList<MemberConfig>)members;
            var countBefore = asList.Count;
            Assert.Throws<ConfigurationSealedException>(() => asList.Add(null!));
            Assert.Throws<ConfigurationSealedException>(() => asList.RemoveAt(0));
            Assert.Equal(countBefore, asList.Count);

            // And it is the type's own member list, not a copy of it.
            Assert.Equal(tc.Members.Count(), asList.Count(c => !c.IsCompilerGenerated));
        }

        /// <summary>
        /// The same retained-reference case as the resolver list: a member collection read before the
        /// seal is the live list afterwards, so the guard has to be in the list rather than in the
        /// accessor that returned it.
        /// </summary>
        [Fact]
        public void MemberList_ReferenceKeptBeforeSeal_CannotChangeAfterSeal()
        {
            var config = new CerasRawConfig();
            var tc = config.ConfigType<SealDoorModel>();
            var kept = tc.UnsafeGetAllMembersIncludingCompilerGenerated();

            var beforeSealCount = ((IList<MemberConfig>)kept).Count;
            Assert.Equal(2, beforeSealCount);   // pre-seal control: readable, and the type's own members

            var ceras = new CerasFastSerializer(config);
            tc.Seal();

            var asList = (IList<MemberConfig>)kept;
            Assert.Throws<ConfigurationSealedException>(() => asList.RemoveAt(0));
            Assert.Throws<ConfigurationSealedException>(() => asList.Clear());
            Assert.Equal(beforeSealCount, asList.Count);
        }

        /// <summary>
        /// The control for the two tests above, and the reason the guard is in the list rather than
        /// around it: before the seal this is still the live member list, and a member removed through
        /// it is a member the serializer does not write.
        /// </summary>
        [Fact]
        public void MemberList_BeforeSeal_IsTheLiveList()
        {
            var config = new CerasRawConfig();
            var tc = config.ConfigType<MemberListModel>();

            var members = (IList<MemberConfig>)tc.UnsafeGetAllMembersIncludingCompilerGenerated();
            var dropped = members.Single(m => m.Member.Name == nameof(MemberListModel.Dropped));
            Assert.True(members.Remove(dropped), "the pre-seal member list did not accept the removal");

            var ceras = new CerasFastSerializer(config);
            var clone = ceras.Deserialize<MemberListModel>(ceras.Serialize(new MemberListModel { Kept = 5, Dropped = 6 }));

            Assert.Equal(5, clone.Kept);
            Assert.Equal(0, clone.Dropped);   // it was not written, so it comes back default
        }

        // ── 4. The exceptions that must survive ──────────────────────────────────────────────

        /// <summary>
        /// The one door that stays open: a type Ceras discovers after the seal cannot have been
        /// configured before it, and <c>OnConfigNewType</c> is how the application configures it.
        /// </summary>
        [Fact]
        public void OnConfigNewType_AfterSeal_StillConfiguresTheTypeItIsHanded()
        {
            var config = new CerasRawConfig();
            var formatter = new CountingFormatter<LateDiscoveredModel>();
            Ceras.TypeConfig handed = null;
            config.OnConfigNewType = tc =>
            {
                handed = tc;
                if (tc.Type == typeof(LateDiscoveredModel))
                    tc.CustomFormatter = formatter;
            };

            var ceras = new CerasFastSerializer(config);

            // The type is reached for the first time here, i.e. after the seal.
            ceras.Serialize(new LateDiscoveryHolder { Inner = new LateDiscoveredModel { Value = 1 } });

            Assert.Same(formatter, ceras.Config.ConfigType<LateDiscoveredModel>().CustomFormatter);
            Assert.Equal(1, formatter.Serializations);

            // The window is the call, not the config: the configuration the callback was handed is
            // closed again the moment it returned.
            Assert.NotNull(handed);
            Assert.Throws<ConfigurationSealedException>(() => handed.CustomFormatter = new CountingFormatter<LateDiscoveredModel>());
        }

        /// <summary>
        /// ...and it is scoped to the one type that was handed, not to the whole config: a type
        /// configured before the seal is closed even from inside the callback, which is what stops
        /// the exception from being a general post-seal window.
        /// </summary>
        [Fact]
        public void OnConfigNewType_AfterSeal_CannotReachAnAlreadyConfiguredType()
        {
            var config = new CerasRawConfig();
            var configured = config.ConfigType<ConfiguredBeforeSealModel>();

            var refusals = new List<Exception>();
            config.OnConfigNewType = tc =>
            {
                var ex = Record.Exception(() => configured.CustomFormatter = new CountingFormatter<ConfiguredBeforeSealModel>());
                if (ex != null) refusals.Add(ex);
            };

            var ceras = new CerasFastSerializer(config);
            ceras.Serialize(new LateDiscoveryHolder { Inner = new LateDiscoveredModel { Value = 1 } });

            // The callback ran (the type it was handed was a new one) and every attempt to reach the
            // other type from it was refused.
            Assert.NotEmpty(refusals);
            Assert.All(refusals, e => Assert.IsType<ConfigurationSealedException>(e));
            Assert.Null(configured.CustomFormatter);
        }

        /// <summary>
        /// A robust context declares its roots into a manifest that seals at the first Save/Load, not
        /// at construction, so <c>DeclareRoot*</c> keeps working after a serializer exists. Unchanged
        /// by the config seal - pinned so a future tightening does not take this door silently.
        /// </summary>
        [Fact]
        public void DeclareRoot_AfterConstruction_StillWorks()
        {
            var ceras = new CerasRobustSerializer();

            ceras.Config.DeclareRoot(typeof(int[]));

            var bytes = ceras.Save(new[] { 1, 2, 3 }, 1);
            Assert.Equal(new[] { 1, 2, 3 }, ceras.Load<int[]>(bytes, 1));
        }
    }
}
