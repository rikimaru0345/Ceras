// A context's configured ITypeBinder has to reach every door that resolves a stored Type name.
//
// The FullNameMarker (127) type value is a read-only path: no Ceras writer emits it, so it is a
// spelling a foreign or hand-built writer used for a type this build has no id for (Format/LAYOUT.md).
// RobustTypeFormatter used to resolve it through a private SimpleTypeBinder of its own while the other
// Type-value doors (SchemaTraversalEngine.ResolveFullName, CerasRobustSerializer.BuildIdToType) used
// Config.TypeBinder - so a context that abbreviates or remaps type names could not read back its own
// spelling. Pinned directly against the formatter the serializer actually installed, because no Ceras
// writer can produce the bytes and a hand-patched save would pin the patcher instead of the fix.
using System;
using System.Buffers;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.TypeBinderDoor
{
    [CerasContext("TypeBinderDoor")]
    [CerasSerializable]
    public class TypeBinderDoorHolder
    {
        public int Value;
    }

    public class ConfiguredTypeBinderTests : TestBase
    {
        const string Ctx = "TypeBinderDoor";

        /// <summary>
        /// A binder that abbreviates: it names a few types by a short token and otherwise behaves like
        /// <see cref="SimpleTypeBinder"/>. Every name it is asked about is recorded, so a test can assert
        /// that *this* binder answered rather than that the answer happened to be right.
        /// </summary>
        sealed class AbbreviatingTypeBinder : ITypeBinder
        {
            static readonly Dictionary<string, Type> Abbreviations = new(StringComparer.Ordinal)
            {
                ["uri"] = typeof(Uri),
                ["i32"] = typeof(int),
            };

            /// <summary>Every name GetTypeFromBase was asked about, in order.</summary>
            public List<string> Asked { get; } = new();

            public string GetBaseName(Type type)
                => type == typeof(Uri) ? "uri" : new SimpleTypeBinder().GetBaseName(type);

            public Type GetTypeFromBase(string baseTypeName)
            {
                Asked.Add(baseTypeName);
                return Abbreviations.TryGetValue(baseTypeName, out var abbreviated)
                    ? abbreviated
                    : new SimpleTypeBinder().GetTypeFromBase(baseTypeName);
            }

            public Type GetTypeFromBaseAndArguments(string baseTypeName, params Type[] genericTypeArguments)
                => GetTypeFromBase(baseTypeName).MakeGenericType(genericTypeArguments);
        }

        /// <summary>
        /// The <c>RobustTypeFormatter</c> this serializer actually resolved for <c>Type</c> — read off
        /// the config, which is where <c>CerasSerializer</c>'s constructor takes it from. Using the
        /// installed instance is the point: a formatter built here with a binder would prove nothing
        /// about the wiring.
        /// </summary>
        static RobustTypeFormatter InstalledTypeFormatter(CerasRobustSerializer serializer)
        {
            var typeConfig = serializer.Config.GetExistingTypeConfig(typeof(Type), false);
            Assert.NotNull(typeConfig?.CustomFormatter);
            return Assert.IsType<RobustTypeFormatter>(typeConfig!.CustomFormatter);
        }

        /// <summary>
        /// Reads one stored <c>Type</c> value the way a save holds it: a 4-byte string-table index into
        /// <paramref name="storedName"/>, behind the FullNameMarker tag, inside a read context.
        /// </summary>
        static Type ReadFullNameTypeValue(RobustTypeFormatter formatter, string storedName)
        {
            // Index 0 is the null sentinel a decoded table always carries, so the name is at 1.
            string[] table = { null, storedName };

            var payload = new ArrayBufferWriter<byte>();
            SchemaSection.WriteLeb128(payload, FullNameMarker);
            SerializerBinary.WriteUInt32Fixed(payload, 1);

            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(payload.WrittenMemory));
            Type value = null!;
            using (RobustContext.BeginRead(table, new Dictionary<int, Type>(), uint.MaxValue))
                formatter.Deserialize(ref reader, ref value);

            // Every path has to have consumed the whole value, or the following field would be read out
            // of step; the marker plus a 4-byte index is the entire value.
            Assert.Equal(payload.WrittenCount, reader.Consumed);
            return value;
        }

        const int FullNameMarker = 127;   // Format/LAYOUT.md: the read-only full-name type value

        [Fact]
        public void An_Abbreviated_Stored_Type_Value_Resolves_Through_The_Contexts_Own_Binder()
        {
            var binder = new AbbreviatingTypeBinder();
            var serializer = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(Ctx);
                c.TypeBinder = binder;
            });

            var formatter = InstalledTypeFormatter(serializer);

            Assert.Equal(typeof(Uri), ReadFullNameTypeValue(formatter, "uri"));
            Assert.Equal(typeof(int), ReadFullNameTypeValue(formatter, "i32"));

            // The configured binder answered, and was asked the abbreviated spelling: a SimpleTypeBinder
            // cannot resolve "uri" at all (it searches for a type by that name in every loaded assembly
            // and throws), so this is what distinguishes the fix from the old private binder.
            Assert.Equal(new[] { "uri", "i32" }, binder.Asked);
        }

        [Fact]
        public void An_Ordinary_Full_Name_Still_Resolves_Through_The_Same_Door()
        {
            // The fix must not narrow the door: a name this build does know is still resolved, and still
            // through the configured binder - which is also what the registry and the migration
            // interpreter resolve names through.
            var binder = new AbbreviatingTypeBinder();
            var serializer = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(Ctx);
                c.TypeBinder = binder;
            });

            var formatter = InstalledTypeFormatter(serializer);

            Assert.Equal(typeof(Uri), ReadFullNameTypeValue(formatter, "System.Uri"));
            Assert.Equal(typeof(TypeBinderDoorHolder), ReadFullNameTypeValue(formatter, typeof(TypeBinderDoorHolder).FullName));
            Assert.Equal(new[] { "System.Uri", typeof(TypeBinderDoorHolder).FullName }, binder.Asked);
        }

        [Fact]
        public void A_Context_That_Declares_No_Binder_Keeps_The_Default_One()
        {
            // CerasConfig.TypeBinder never hands back null, so the unset case resolves names exactly as
            // it always has - through a SimpleTypeBinder. Pinned because RobustTypeFormatter's
            // `typeBinder ?? new SimpleTypeBinder()` exists to keep its field non-null, and this is the
            // answer that coalesce must not be shadowing in the normal case.
            var config = new CerasRobustConfig();
            Assert.NotNull(config.TypeBinder);
            Assert.IsType<SimpleTypeBinder>(config.TypeBinder);

            var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));
            var formatter = InstalledTypeFormatter(serializer);

            Assert.Equal(typeof(Uri), ReadFullNameTypeValue(formatter, "System.Uri"));
            Assert.Equal(typeof(int), ReadFullNameTypeValue(formatter, "System.Int32"));

            // The abbreviated spelling is *not* readable here, which is the same answer as before the
            // change for a context that configured nothing.
            Assert.ThrowsAny<Exception>(() => ReadFullNameTypeValue(formatter, "uri"));
        }

        // ── a null binder is a configuration error, not a third way to resolve a name ───────────

        /// <summary>
        /// <c>CerasConfig.TypeBinder</c> is declared non-nullable and defaults to a
        /// <see cref="SimpleTypeBinder"/>, so "no binder" is not a state a config can be in. The setter
        /// used to accept null anyway, and the three places that resolve a stored type name each treated
        /// that null as a different thing - so one misconfigured context answered the same name three
        /// ways inside a single load, and the loudest of the three said only "Object reference not set to
        /// an instance of an object". The setter refuses it now, on both config types.
        /// </summary>
        public static TheoryData<string> ConfigKinds => new() { "CerasRobustConfig", "CerasRawConfig" };

        [Theory]
        [MemberData(nameof(ConfigKinds))]
        public void Configuring_A_Null_TypeBinder_Is_Refused(string configKind)
        {
            var config = NewConfig(configKind);

            var ex = Assert.Throws<ArgumentNullException>(() => config.TypeBinder = null);

            // The cause, and the door the old message never named: this assignment used to surface three
            // saves later as a NullReferenceException inside CerasRobustSerializer.BuildIdToType.
            Assert.Contains("TypeBinder", ex.Message, StringComparison.Ordinal);
            Assert.Contains("cannot be null", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("BuildIdToType", ex.Message, StringComparison.Ordinal);

            // ...and the fix, which is "leave it unset" rather than "assign something clever".
            Assert.Contains("unset", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(nameof(SimpleTypeBinder), ex.Message, StringComparison.Ordinal);
        }

        [Theory]
        [MemberData(nameof(ConfigKinds))]
        public void A_Refused_Null_Binder_Leaves_The_Context_As_It_Was(string configKind)
        {
            var config = NewConfig(configKind);
            var before = config.TypeBinder;

            Assert.Throws<ArgumentNullException>(() => config.TypeBinder = null);

            // Not half an assignment: the property reads back what it read before, so a caller that
            // swallows the exception gets the default binder rather than the null it just tried to set.
            Assert.Same(before, config.TypeBinder);
            Assert.IsType<SimpleTypeBinder>(config.TypeBinder);
        }

        [Fact]
        public void A_Real_Binder_Is_Still_Accepted_And_Still_The_One_That_Answers()
        {
            // The control for the two tests above: without it, "always throws" would satisfy both.
            var binder = new AbbreviatingTypeBinder();
            var config = new CerasRobustConfig();
            config.TypeBinder = binder;

            Assert.Same(binder, config.TypeBinder);

            var serializer = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(Ctx);
                c.TypeBinder = binder;
            });

            Assert.Equal(typeof(Uri), ReadFullNameTypeValue(InstalledTypeFormatter(serializer), "uri"));
            Assert.Equal(new[] { "uri" }, binder.Asked);
        }

        static CerasConfig NewConfig(string kind) => kind switch
        {
            "CerasRobustConfig" => new CerasRobustConfig(),
            "CerasRawConfig" => new CerasRawConfig(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        [Fact]
        public void A_Robust_Context_Refuses_A_Custom_Type_Formatter_Outright_So_The_Rebinding_Has_Nothing_Of_The_Callers_To_Overwrite()
        {
            // The ordering the fix has to preserve: the binder re-pointing runs after the configuration
            // callback, and it must not touch a Type formatter the caller installed. A robust context
            // refuses one of those at setup, so that case is unreachable here — which is *why* the
            // re-pointing is safe (RobustTypeFormatter is internal, so the only RobustTypeFormatter a
            // config can hold is the one Ceras installed). Pinned so the reason is recorded rather than
            // assumed, and so a future change that lets a custom formatter through re-checks the order.
            var ex = Assert.Throws<Ceras.Exceptions.CerasSchemaException>(() => new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(Ctx);
                c.ConfigType<Type>().CustomFormatter = new CallerSuppliedTypeFormatter();
                c.TypeBinder = new AbbreviatingTypeBinder();
            }));

            Assert.Contains(nameof(CallerSuppliedTypeFormatter), ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A caller-supplied <c>Type</c> formatter. A robust context refuses one, so it exists only to pin
    /// that refusal — and to be a formatter Ceras does not recognise as its own.
    /// </summary>
    sealed class CallerSuppliedTypeFormatter : Formatters.FormatterBase<Type>
    {
        public override void Serialize(IBufferWriter<byte> writer, Type value) => throw new NotSupportedException();
        public override void Deserialize(ref SequenceReader<byte> reader, ref Type value) => throw new NotSupportedException();
    }
}
