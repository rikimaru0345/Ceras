using System;


namespace Ceras
{
    using Formatters;
    using Helpers;


	/// <summary>
	/// Add this to a field or property to force Ceras to ignore it.
	/// When applied to a type (class, struct, interface), it prevents Ceras from ever serializing
	/// any members of that type, effectively banning it from all serialization.
	/// Check out the tutorial to see in what order attributes, the ShouldSerialize callback and other settings are evaluated.
	/// </summary>
	[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
    public sealed class ExcludeAttribute : Attribute { }

    /// <summary>
    /// Add this to a field or property to force Ceras to include it.
    /// Check out the tutorial to see in what order attributes, the ShouldSerialize callback and other settings are evaluated.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class IncludeAttribute : Attribute { }


    /// <summary>
    /// Read the descriptions of the individual entries.
    /// </summary>
    public enum SerializationOverride
    {
        /// <summary>
        /// When you return 'NoOverride' Ceras will continue normally, which is checking the member-attributes, class attributes, etc... check out the tutorial to see how Ceras decides what members are included in detail.
        /// </summary>
        NoOverride,
        /// <summary>
        /// ForceInclude will completely skip all other checks and include the member. Be careful that you don't accidentally include hidden/compiler generated fields if you have turned 'SkipCompilerGeneratedFields' off.
        /// </summary>
        ForceInclude,
        /// <summary>
        /// Forces Ceras to ignore the field or property completely.
        /// </summary>
        ForceSkip,
    }

    /// <summary>
    /// Add this to a member if you have changed the name.
    /// Ceras will use this to map old field names to the new one in the Robust format migration engine.
    /// You can also use this to simply override what name is used to serialize the member, so as long as the attribute is around and does not change you can freely rename the member itself; this can be used to make the resulting serialized data smaller.
    /// <para>
    /// On an enum member it is the only way to reach a spelling this build cannot derive: the file
    /// records the member's stable wire name, and a member renamed in C# *and* repinned to a new wire
    /// name shares no spelling with a file written before wire names were recorded.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class PreviousNameAttribute : Attribute
    {
        /// <summary>
        /// The other names this member has been serialized under. Never null: it is an empty array
        /// when the attribute names none, so a caller can always enumerate it. A null array passed to
        /// the constructor is normalized to that empty one.
        /// </summary>
        public readonly string[] AlternativeNames = new string[0];

        /// <summary>The previous name, or null when this attribute only records alternatives.</summary>
        public readonly string? Name;

        public PreviousNameAttribute()
        {
        }

        public PreviousNameAttribute(string? name)
        {
            Name = name;
        }

        public PreviousNameAttribute(string? name, params string[]? alternativeNames)
        {
            Name = name;
            AlternativeNames = alternativeNames ?? new string[0];
        }
    }


    /// <summary>
    /// Put this on any constructor or static method as a hint of what constructor/factory to use by default. (Can be overriden through <see cref="CerasConfig.ConfigType{T}"/>)
    /// </summary>
    [AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method)]
    public class CerasConstructorAttribute : Attribute
    {
    }


	/// <summary>
	/// Add this to a method of a class you serialize/deserialize. Ceras will call it during serialization.
	/// </summary>
	public class OnBeforeSerializeAttribute : Attribute
    {
    }

    /// <summary>
    /// Add this to a method of a class you serialize/deserialize. Ceras will call it during serialization.
    /// </summary>
    public class OnAfterSerializeAttribute : Attribute
    {
    }

	/// <summary>
	/// Add this to a method of a class you serialize/deserialize. Ceras will call it during serialization.
	/// </summary>
	public class OnBeforeDeserializeAttribute : Attribute
    {
    }

    /// <summary>
    /// Add this to a method of a class you serialize/deserialize. Ceras will call it during serialization.
    /// </summary>
    public class OnAfterDeserializeAttribute : Attribute
    {
    }
}

namespace Ceras.Formatters
{
    /// <summary>
    /// Add this attribute to your <see cref="IFormatter{T}"/> class to configure Ceras' dependency injection system. (Only needed to turn it off, it's enabled, even for private fields, by default)
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Field)]
    public class CerasInjectAttribute : Attribute
    {
        internal static readonly CerasInjectAttribute Default = new CerasInjectAttribute();

        /// <summary>
        /// If true, Ceras will include all private fields when trying to inject dependencies
        /// <para>Default: true</para>
        /// </summary>
        public bool IncludePrivate { get; set; } = true;
    }

    /// <summary>
    /// Add this to a field inside an <see cref="IFormatter{T}"/> to let Ceras ignore it. Only useful on fields that could potentially have anything injected into them in the first place (field types like <see cref="CerasSerializer"/> or inheriting from <see cref="IFormatter"/>)
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Class)]
    public class CerasNoInjectAttribute : Attribute
    {
    }

    /// <summary>
    /// Add this to a field inside an <see cref="IFormatter{T}"/> to inject only the direct version of a formatter, instead of a wrapper for reference types. Only use this if you are fully aware of what ReferenceFormatter does and provides
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class CerasNoReference : Attribute
    {
    }
}

namespace Ceras.Formatters.AotGenerator
{
}

namespace Ceras
{
    /// <summary>
    /// Put this attribute on every class/struct you want Ceras to generate a formatter for at compile time.
    /// The source generator will emit an <c>IFormatter&lt;T&gt;</c> implementation and include it in
    /// the generated <c>Ceras.Generated.RobustFormatters.UseRobustFormatters(config, context)</c>
    /// registration for each <see cref="CerasContextAttribute">context</see> the type belongs to.
    /// This replaces the old AotGenerator tool and is required for IL2CPP / AOT platforms.
    ///
    /// When placed on an interface or abstract class,
    /// the source generator automatically discovers every non-abstract type in the compilation
    /// that implements or inherits from it and generates formatters for all of them.
    ///
    /// <para>
    /// Under the context model this attribute is <b>not</b> what makes a type a root: a type is part of a
    /// serialization context when it is a <see cref="CerasContextAttribute"/> root of that context or is
    /// reachable from one. <c>[CerasSerializable]</c> is still useful for its stable-name override and as
    /// an explicit "this type is meant to be serialized" marker (and for the generator's diagnostics),
    /// but a type that is neither a context root nor reachable from one is not registered by
    /// <c>UseGeneratedFormatters</c>.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Interface)]
    public class CerasSerializableAttribute : Attribute
    {
        /// <summary>
        /// Optional override for the stable type name written into the robust schema, or
        /// <see langword="null"/> when the attribute was applied without one — which is the normal
        /// parameterless <c>[CerasSerializable]</c> form, so callers wanting the effective name have
        /// to derive the default themselves.
        /// <para>
        /// When omitted, the default stable name is the CLR metadata identity:
        /// <c>Namespace.Outer+Nested`Arity</c> (namespace, containing types joined with <c>+</c>,
        /// and the metadata name including the generic arity backtick). This is stable across
        /// renames of the C# declaration only if the metadata identity is unchanged.
        /// </para>
        /// <para>
        /// Aliases for old names are declared separately:
        /// <list type="bullet">
        /// <item><see cref="PreviousTypeNameAttribute"/> maps a renamed type.</item>
        /// <item><see cref="CerasStableNameAttribute"/> fixes a member's wire name independently of the C# name.</item>
        /// <item><see cref="PreviousNameAttribute"/> maps renamed members (with alternative names).</item>
        /// </list>
        /// </para>
        /// </summary>
        public string? StableName { get; }

        public CerasSerializableAttribute(string? stableName = null)
        {
            StableName = stableName;
        }
    }

    /// <summary>
    /// Declares that the annotated type is a <b>root</b> of the named serialization
    /// <b>context</b>, and that the type is serializable within that context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A context is a named, finite set of serializable types that share one generated manifest, one
    /// deterministic type-ID space, and one layout fingerprint. You select a context by name when you
    /// configure a serializer, for example
    /// <c>new CerasRobustSerializer(c =&gt; c.UseGeneratedFormatters("SaveGame"))</c>. Everything the
    /// source generator discovers from that context's roots &#8212; member types, generic arguments, and
    /// polymorphic alternatives (the subtypes of interfaces and abstract base types) &#8212;
    /// automatically joins the same context. A context does not leak types from another context: its
    /// manifest, its type IDs, and its fingerprint cover exactly the types reachable from its own roots.
    /// </para>
    /// <para>
    /// Contexts exist because different serialization scenarios have different, sometimes conflicting,
    /// requirements. A persistent save game must stay readable across many releases, so it wants a
    /// frozen type set and no accidental coupling to unrelated types. A transient network protocol has
    /// no such compatibility guarantee: it may use only a handful of message types, may change shape
    /// every build, and should not drag the save-game schema (or its version gates) into every packet.
    /// Modelling these as separate contexts gives each one independent evolution: adding, renaming, or
    /// removing a type in the network context does not change the save-game fingerprint and therefore
    /// cannot invalidate existing saves, and vice versa. Separate contexts also produce smaller
    /// manifests and make undeclared-type mistakes fail with a precise "not declared in this context"
    /// error instead of silently pulling in types from an unrelated area.
    /// </para>
    /// <para>
    /// A type becomes a member of a context in one of two ways:
    /// <list type="number">
    /// <item>it is annotated directly with this attribute (possibly more than once), which makes it a
    /// root of each named context; or</item>
    /// <item>it is reachable from a root of that context, as a member type, a generic argument, or a
    /// polymorphic alternative.</item>
    /// </list>
    /// A type may therefore belong to several contexts at once, either by naming each context on the
    /// attribute or by being reachable from roots of each. Membership is always per context: the type
    /// still gets its own entry, its own type ID, and its own fingerprint in every context it appears
    /// in. One serializer configuration uses exactly one context; there is no context composition at
    /// runtime.
    /// </para>
    /// <para>
    /// This attribute is an alternative to relying on <see cref="CerasSerializableAttribute"/> alone.
    /// A type annotated with <see cref="CerasContextAttribute"/> is serializable even without
    /// <see cref="CerasSerializableAttribute"/>, while <see cref="CerasSerializableAttribute"/> still
    /// provides the stable-name override and marks the type as deliberately serializable.
    /// The generated registration exposes the declared context names as
    /// <c>Ceras.Generated.RobustFormatters.ContextNames</c>, and passing an unknown name to
    /// <c>UseGeneratedFormatters</c> fails with an error that lists the available contexts.
    /// </para>
    /// <para>
    /// Because there is deliberately no default context, a compilation that shows serialization
    /// intent (it annotates a type with <see cref="CerasSerializableAttribute"/>/<see cref="CerasContextAttribute"/>,
    /// or declares an assembly-level <see cref="CerasSerializableAttribute{T}"/>/<see cref="CerasKnownTypeAttribute"/>)
    /// but declares no <c>[CerasContext]</c> fails the build with <c>CERAS025</c>: there would be no
    /// manifest to register. A compilation that merely references the generator without any such
    /// declaration is left alone and emits an empty (compilable) registration.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// [CerasContext("SaveGame")]   // persistent, versioned, must stay readable
    /// [CerasContext("Network")]    // transient, no cross-build compatibility promise
    /// public class Player { public string Name; }
    ///
    /// [CerasContext("Network")]
    /// public class ChatMessage { public string Text; }
    ///
    /// // Registers only the SaveGame manifest (Player, its closure, and the save-game fingerprint):
    /// var save = new CerasRobustSerializer(c =&gt; c.UseGeneratedFormatters("SaveGame"));
    ///
    /// // Registers only the Network manifest (Player + ChatMessage in this example):
    /// var net = new CerasRobustSerializer(c =&gt; c.UseGeneratedFormatters("Network"));
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum, AllowMultiple = true)]
    public sealed class CerasContextAttribute : Attribute
    {
        /// <summary>The case-sensitive name of the serialization context this type is a root of.</summary>
        public string Name { get; }

        public CerasContextAttribute(string name)
        {
            Name = name;
        }
    }

    /// <summary>
    /// Declares an additional known type for the generated manifest, beyond the types that are
    /// reachable from the roots. This is primarily useful for closed generic instantiations that
    /// are hidden behind <c>object</c>/interface members and therefore cannot be discovered by
    /// walking member types (e.g. <c>[assembly: CerasKnownType&lt;List&lt;MyItem&gt;&gt;]</c>).
    /// The declared type and everything reachable from it are added to the generated closed set of
    /// every serialization context, or only of the context named by <see cref="Context"/>.
    /// Applied at the assembly level (the current compilation declares the contexts via
    /// <see cref="CerasContextAttribute"/> on root types).
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class CerasKnownTypeAttribute : Attribute
    {
        public Type Type { get; }

        /// <summary>
        /// Optional context this known type belongs to. When omitted, the known type is added to
        /// <b>every</b> context declared in the compilation (which is what most users want for a
        /// globally known type such as a collection instantiation). When set, it is added only to
        /// that context, and the context must be declared by at least one
        /// <see cref="CerasContextAttribute"/> root.
        /// </summary>
        public string Context { get; }

        public CerasKnownTypeAttribute(Type type)
            : this(type, null!)
        {
        }

        public CerasKnownTypeAttribute(Type type, string context)
        {
            Type = type;
            Context = context;
        }
    }

    /// <summary>
    /// Generic, type-safe form of <see cref="CerasKnownTypeAttribute"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class CerasKnownTypeAttribute<T> : Attribute
    {
        /// <summary>
        /// Optional context this known type belongs to. When omitted, the known type is added to
        /// <b>every</b> context declared in the compilation. When set, it is added only to that
        /// context, and the context must be declared by at least one
        /// <see cref="CerasContextAttribute"/> root.
        /// </summary>
        public string Context { get; }

        public CerasKnownTypeAttribute()
            : this(null!)
        {
        }

        public CerasKnownTypeAttribute(string context)
        {
            Context = context;
        }
    }

	/// <summary>
	/// Prevents the Ceras source generator from automatically discovering and generating
	/// formatters for types in the specified namespace or matching the pattern (e.g. "Godot.*").
	/// Types with an explicit <see cref="CerasSerializableAttribute"/> will still be processed.
	/// </summary>
	[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class CerasGeneratorBlacklistAttribute : Attribute
    {
        public string Pattern { get; }
        public CerasGeneratorBlacklistAttribute(string pattern) { Pattern = pattern; }
    }

    /// <summary>
    /// Generic version of <see cref="CerasSerializableAttribute"/> for better type safety.
    /// Can be used on any type or the assembly itself to generate a formatter for the specified type <typeparamref name="T"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Assembly, AllowMultiple = true)]
    public class CerasSerializableAttribute<T> : CerasSerializableAttribute
    {
        public CerasSerializableAttribute(string? stableName = null) : base(stableName)
        {
        }
    }

    /// <summary>
    /// Declares a previous CLR type name for this class/struct/enum so that the robust format migration
    /// engine can map old binary data (written under the old name) to the current type.
    /// Can be applied multiple times if the type has been renamed more than once.
    /// <para>
    /// Enums are included because an enum is a named schema entry like any other: without an alias a
    /// renamed enum cannot be resolved from an old save at all, and the load fails before any value is
    /// read. The alias is carried in the enum's schema entry, exactly as for a class.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, AllowMultiple = true)]
    public class PreviousTypeNameAttribute : Attribute
    {
        public string Name { get; }
        public PreviousTypeNameAttribute(string name) { Name = name; }
    }

	/// <summary>
	/// Sets a stable wire name for this field, property or enum member — independent of the C# member
	/// name. Set this once and the C# member can be freely renamed without needing
	/// <see cref="PreviousNameAttribute"/>.
	/// <para>
	/// For an enum member the name is what the save's schema records and what a migration matches on, so
	/// a member pinned here migrates across a rename on its own. It has to be unique within the enum: two
	/// members under one name are rejected (CERAS009 when generated, a <c>CerasSchemaException</c> at the
	/// bake for a runtime-derived enum).
	/// </para>
	/// </summary>
	[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
	public class CerasStableNameAttribute : Attribute {
		public string Name { get; }
		public CerasStableNameAttribute(string name) { Name = name; }
	}
}
