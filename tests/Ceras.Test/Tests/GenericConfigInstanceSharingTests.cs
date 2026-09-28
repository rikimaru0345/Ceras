// The models below are shapes for Ceras to reflect over, not objects these tests read: a field only has
// to *exist* for the config to build a member for it, which the compiler does not count towards a field
// being assigned (CS0649). Same reason the other model-only test files disable it file-wide.
#pragma warning disable 0649 // Field is never assigned to

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Buffers;
using System.Linq;
using System.Reflection;
using Ceras;
using Ceras.Formatters;
using Xunit;

namespace Ceras.Test.Tests
{
    // The test assembly contains a *test class* named Ceras.Test.TypeConfig, which shadows the
    // library's Ceras.TypeConfig for everything in the Ceras.Test.* namespaces unless it is aliased
    // here (inside the namespace body - a file-level alias loses to the enclosing namespace).
    using TypeConfig = Ceras.TypeConfig;

    // A TypeConfig is one object per (type, isStatic) - however it was first reached. The NativeAOT fix
    // (TODO.md defect 3) changed *how* that object is built: ConfigType<T> constructs TypeConfig<T>
    // directly and TypeConfig<T> constructs MemberConfig<T> directly, instead of both going through
    // MakeGenericType + Activator.CreateInstance, because ILC only emits native code for a closed generic
    // that some code in the compilation names statically. What it must not have changed is *which* object
    // comes back: a second call through either overload, and the resolver's own first resolution, all have
    // to find the instance the first call created. These tests pin that, in both orders, for both cache
    // entries (instance and static) and for a value-type argument - the case that fails under NativeAOT.
    public class GenericConfigInstanceSharingTests
    {
        [Fact]
        public void ConfigTypeGenericFirst_ReflectiveCallReturnsTheSameInstance()
        {
            var config = new CerasRawConfig();

            var viaGeneric = config.ConfigType<SharedModel>();
            var viaType = ConfigByType(config, typeof(SharedModel));

            Assert.Same(viaGeneric, viaType);
        }

        [Fact]
        public void ConfigTypeReflectiveFirst_GenericCallReturnsTheSameInstance()
        {
            var config = new CerasRawConfig();

            // The order the two paths differ in: this instance is created by MakeGenericType +
            // Activator, and the generic overload then has to find *that* one rather than build a
            // second TypeConfig<T> and hand it back instead.
            var viaType = ConfigByType(config, typeof(SharedModel));
            var viaGeneric = config.ConfigType<SharedModel>();

            Assert.Same(viaType, viaGeneric);
            Assert.IsType<TypeConfig<SharedModel>>(viaGeneric);
        }

        [Fact]
        public void ValueTypeArgument_SharesOneConfigBetweenBothOverloads()
        {
            // The NativeAOT failure was specific to a value-type argument: a reference-type
            // instantiation has shared canonical code to fall back on, a value-type one has none.
            var config = new CerasRawConfig();

            var viaGeneric = config.ConfigType<SharedEnum>();
            var viaType = ConfigByType(config, typeof(SharedEnum));

            Assert.Same(viaGeneric, viaType);
        }

        [Fact]
        public void StaticAndInstanceConfigs_AreSeparateEntriesInTheCache()
        {
            var config = new CerasRawConfig();

            var instance = config.ConfigType<SharedStaticModel>();
            var stat = ConfigStaticByType(config);

            // Different (type, isStatic) pairs, so two objects - and each one is shared within itself.
            Assert.NotSame(instance, stat);
            Assert.Same(stat, ConfigStaticByType(config));

            Assert.False(instance.IsStatic);
            Assert.True(stat.IsStatic);
            Assert.All(stat.Members, m => Assert.IsType<MemberConfig<SharedStaticModel>>(m));
        }

        [Fact]
        public void MemberConfigs_AreTheGenericInstantiation_WhicheverOverloadCreatedTheConfig()
        {
            // The other half of the fix: TypeConfig<T> constructs MemberConfig<T> directly, so a
            // config created through the *reflective* ConfigType(Type) has to hold the same member
            // objects as one created through ConfigType<T>() - and be the same config object at all,
            // so its members cannot have been built twice.
            var config = new CerasRawConfig();
            var fromGeneric = config.ConfigType<SharedModel>();
            var fromType = ConfigByType(config, typeof(SharedModel));

            Assert.Same(fromGeneric, fromType);
            Assert.All(fromType.Members, m => Assert.IsType<MemberConfig<SharedModel>>(m));

            var name = fromGeneric.Members.Single(m => m.Member.Name == nameof(SharedModel.Name));
            Assert.Same(name, fromType.Members.Single(m => m.Member.Name == nameof(SharedModel.Name)));
            Assert.Same(name, fromGeneric.GetMemberConfig(name.Member));

            // Reflective first, on a config of its own: the member objects are the generic
            // instantiation there too, because the same TypeConfig<T> builds them either way.
            var reflectiveFirst = ConfigByType(new CerasRawConfig(), typeof(SharedModel));
            Assert.IsType<TypeConfig<SharedModel>>(reflectiveFirst);
            Assert.All(reflectiveFirst.Members, m => Assert.IsType<MemberConfig<SharedModel>>(m));
        }

        [Fact]
        public void TheClosedConfigsAreConstructedWhereACompilerCanSeeThem()
        {
            // Why this one reads IL instead of behaviour: on CoreCLR both ways of building
            // TypeConfig<T> / MemberConfig<T> produce the same closed type from the same object graph,
            // so nothing observable in a test distinguishes them. The difference only appears under
            // NativeAOT, where the reflective construction has no native code to bind to. This is the
            // assertion that keeps the fix from being silently reverted without an AOT build, and it
            // asks the same question ILC asks: does any code the entry point reaches contain a
            // `newobj` naming the closed type?
            var configTypeOfT = typeof(CerasConfig)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Single(m => m.Name == nameof(CerasConfig.ConfigType) && m.IsGenericMethodDefinition)
                .MakeGenericMethod(typeof(SharedModel));

            Assert.True(ConstructsStatically(configTypeOfT, typeof(TypeConfig<SharedModel>)),
                        "ConfigType<T> must contain a 'new TypeConfig<T>(...)' (possibly in the lambda it passes to the cache lookup) instead of only the reflective MakeGenericType/Activator path.");

            Assert.True(ConstructsStatically(
                            typeof(TypeConfig<SharedModel>).GetMethod("CreateMemberConfig", BindingFlags.Instance | BindingFlags.NonPublic),
                            typeof(MemberConfig<SharedModel>)),
                        "TypeConfig<T> must construct 'new MemberConfig<T>(...)' itself instead of delegating to the reflective base implementation.");
        }

        /// <summary>
        /// True when <paramref name="entryPoint"/>, or anything it can take a function pointer to (a
        /// lambda, a local function), contains a <c>newobj</c> of <paramref name="closedType"/>. A
        /// closed generic exists under NativeAOT if some code names it, and a <c>newobj</c> on it is
        /// such a name - which is what the trim/AOT smoke test observes from the outside.
        /// <para>
        /// The scan is deliberately shallow: the only tokens it follows are function pointers, because
        /// Ceras passes the direct construction as a factory (<c>ConfigType&lt;T&gt;</c> hands
        /// <c>GetTypeConfigForConfiguration</c> a lambda), and everything it needs to find is one hop
        /// away. A token that does not resolve to a method is a byte of some other operand, not a
        /// failure, so it is skipped.
        /// </para>
        /// </summary>
        static bool ConstructsStatically(MethodBase entryPoint, Type closedType)
        {
            // Resolving the operands of a method *inside* a generic method needs that method's closed
            // generic arguments, or the runtime refuses ("lack of a generic context").
            var declaringTypeArguments = entryPoint.DeclaringType?.GetGenericArguments();
            var methodArguments = entryPoint.IsGenericMethod ? entryPoint.GetGenericArguments() : null;

            var pending = new Queue<MethodBase>();
            var seen = new HashSet<MethodBase>();
            pending.Enqueue(entryPoint);

            while (pending.Count > 0)
            {
                var method = pending.Dequeue();
                if (!seen.Add(method))
                    continue;

                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il == null)
                    continue;

                for (int i = 0; i + 4 < il.Length; i++)
                {
                    // 0x73 newobj, 0xFE06 ldftn, 0xFE07 ldvirtftn - all three carry a 4-byte token.
                    bool isNewObj = il[i] == 0x73;
                    bool isFunctionPointer = il[i] == 0xFE && (il[i + 1] == 0x06 || il[i + 1] == 0x07);
                    if (isFunctionPointer)
                        i++;
                    if (!isNewObj && !isFunctionPointer)
                        continue;

                    MethodBase target;
                    try
                    {
                        target = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1), declaringTypeArguments, methodArguments);
                    }
                    catch (Exception)
                    {
                        continue;   // an operand byte that happens to look like one of these opcodes
                    }

                    if (target == null)
                        continue;

                    if (isNewObj && target.DeclaringType == closedType)
                        return true;

                    pending.Enqueue(target);
                }
            }

            return false;
        }

        [Fact]
        public void FirstResolution_ReusesTheConfiguredInstance_AndFiresTheCallbackOnce()
        {
            var config = new CerasRawConfig();
            int callbacks = 0;
            config.OnConfigNewType = _ => callbacks++;

            // Explicit configuration does not fire the callback, and does not leave a second instance
            // behind for the resolver to find instead.
            var configured = config.ConfigType<SharedModel>();
            Assert.Equal(0, callbacks);
            Assert.Same(configured, config.GetTypeConfig(typeof(SharedModel), false));
            Assert.Equal(0, callbacks);

            // A type nothing configured yet is created by the resolver - once, with the callback.
            var discovered = config.GetTypeConfig(typeof(DiscoveredModel), false);
            Assert.NotSame(configured, discovered);
            Assert.Equal(1, callbacks);
            Assert.Same(discovered, config.GetTypeConfig(typeof(DiscoveredModel), false));
            Assert.Equal(1, callbacks);
        }

        [Fact]
        public void ConfiguredThroughTheGenericOverload_ConfiguresTheSerializerItself()
        {
            // The end of the chain: the config object both overloads share is the one the serializer
            // resolves, so a formatter installed through ConfigType<T>() is the one that gets used.
            var config = new CerasRawConfig();
            var configured = config.ConfigType<SharedModel>();
            configured.CustomFormatter = new SharedModelFormatter();

            var ceras = new CerasFastSerializer(config);
            var clone = ceras.Deserialize<SharedModel>(ceras.Serialize(new SharedModel { Name = "a" }));

            Assert.Equal("a", clone.Name);
            Assert.Same(configured, ceras.Config.GetTypeConfig(typeof(SharedModel), false));
        }

        // ── the Type-based entry points ────────────────────────────────────────────────────────────
        //
        // Both helpers below exist only so that the annotated public overloads are called from exactly
        // one place each, in one file, instead of from every test that happens to need the reflective
        // path. What is under test *is* that path: these tests assert that a config reached through a
        // Type is the same object the generic API hands back, and that is only checkable by calling it.

        // ConfigType(Type) constructs the closed TypeConfig<T> reflectively (MakeGenericType +
        // Activator), so it is annotated [RequiresUnreferencedCode] and [RequiresDynamicCode]: a
        // trimmed or NativeAOT build has no native code for that instantiation unless something
        // constructs it statically. These tests run on CoreCLR, where it always does, and the rooting
        // requirement is documented on the overload itself.
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The test asserts that the Type-based overload returns the same object as the generic one; running on CoreCLR, the reflective construction is always available, and the NativeAOT rooting requirement is documented on the overload.")]
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Same call site as the IL2026 suppression above: CoreCLR test, and the NativeAOT rooting requirement is documented on ConfigType(Type).")]
        static TypeConfig ConfigByType(CerasConfig config, Type type) => config.ConfigType(type);

        // ConfigStaticType(Type) has no generic equivalent - ConfigType<T> configures the *instance*
        // entry, not the static one - so a test of the static cache entry has to call it. Both
        // overloads construct the same closed TypeConfig<T> through the same constructor, so one
        // ConfigType<T>() call for the type is what would root it under NativeAOT; that requirement is
        // documented on the overload. Here it is a CoreCLR test, where the reflective construction
        // always works, and what is under test is that the static entry is separate and shared.
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "No generic overload configures a static config, and the test runs on CoreCLR where the reflective construction always works; the NativeAOT limitation is documented on ConfigStaticType(Type).")]
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Same call site as the IL2026 suppression above: no generic overload exists for a static config, and this is a CoreCLR test.")]
        static TypeConfig ConfigStaticByType(CerasConfig config) => config.ConfigStaticType(typeof(SharedStaticModel));

        // ── models ────────────────────────────────────────────────────────────────────────────────

        private class SharedModel
        {
            public string Name;
            public int Number;
        }

        private enum SharedEnum
        {
            None,
            Some,
        }

        private class SharedStaticModel
        {
            public static int Counter;
            public static string Label;
        }

        private class DiscoveredModel
        {
            public int Value;
        }

        private class SharedModelFormatter : FormatterBase<SharedModel>
        {
            public override void Serialize(IBufferWriter<byte> writer, SharedModel value)
                => SerializerBinary.WriteString(writer, value.Name);

            public override void Deserialize(ref SequenceReader<byte> reader, ref SharedModel value)
                => value = new SharedModel { Name = SerializerBinary.ReadString(ref reader) };
        }
    }
}
