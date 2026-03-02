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
	/// Configure what members to include by default in this type. You can also add [Exclude] and [Include]
	/// to individual members to override the per-type setting.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Assembly)]
	public sealed class MemberConfigAttribute : Attribute
	{
		public TargetMember TargetMembers { get; set; }

		public MemberConfigAttribute(TargetMember targetMembers = TargetMember.AllFields)
		{
			TargetMembers = targetMembers;
		}
	}

	/// <summary>
	/// What members should be serialized and deserialized.
	/// Ceras serializes fields only — never properties directly (auto-property backing fields are included).
	/// </summary>
	[Flags]
	public enum TargetMember
	{
		None = 0,

		/// <summary>Fields declared with the <c>public</c> keyword.</summary>
		PublicFields = 1 << 0,
		/// <summary>Fields declared with any non-public visibility (private, internal, protected).</summary>
		PrivateFields = 1 << 1,

		/// <summary>All fields, both public and private. This is the default.</summary>
		AllFields = PublicFields | PrivateFields,

		/// <summary>Alias for AllFields</summary>
		All = AllFields,

		/// <summary>Compatibility alias for PublicFields</summary>
		AllPublic = PublicFields,
	}

    /// <summary>
    /// Add this to a member if you have changed the name.
    /// Ceras will use this to map old field names to the new one in the Robust format migration engine.
    /// You can also use this to simply override what name is used to serialize the member, so as long as the attribute is around and does not change you can freely rename the member itself; this can be used to make the resulting serialized data smaller.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class PreviousNameAttribute : Attribute
    {
        public readonly string[] AlternativeNames = new string[0];
        public readonly string Name;

        public PreviousNameAttribute()
        {
        }

        public PreviousNameAttribute(string name)
        {
            Name = name;
        }

        public PreviousNameAttribute(string name, params string[] alternativeNames)
        {
            Name = name;
            AlternativeNames = alternativeNames;
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
    /// <c>Ceras.Generated.RobustFormatters.UseRobustFormatters(config)</c>.
    /// This replaces the old AotGenerator tool and is required for IL2CPP / AOT platforms.
    ///
    /// When placed on an interface or abstract class with <see cref="DiscoverSubtypes"/> = true,
    /// the source generator automatically discovers every non-abstract type in the compilation
    /// that implements or inherits from it and generates formatters for all of them.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Interface)]
    public class CerasSerializableAttribute : Attribute
    {
        /// <summary>The stable name used in the binary schema. Defaults to the simple class name.</summary>
        public string StableName { get; }

        /// <summary>
        /// When true (and placed on an interface or abstract/base class), the source generator
        /// scans the entire compilation and automatically generates formatters for every
        /// non-abstract concrete type that implements or extends the marked type.
        /// </summary>
        public bool DiscoverSubtypes { get; set; } = true;

        public CerasSerializableAttribute(string stableName = null)
        {
            StableName = stableName;
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
        public CerasSerializableAttribute(string stableName = null) : base(stableName)
        {
        }
    }

    /// <summary>
    /// Declares a previous CLR type name for this class/struct so that the robust format migration
    /// engine can map old binary data (written under the old name) to the current type.
    /// Can be applied multiple times if the type has been renamed more than once.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
    public class PreviousTypeNameAttribute : Attribute
    {
        public string Name { get; }
        public PreviousTypeNameAttribute(string name) { Name = name; }
    }

	/// <summary>
	/// Sets a stable wire name for this field or property — independent of the C# member name.
	/// Set this once and the C# member can be freely renamed without needing <see cref="PreviousNameAttribute"/>.
	/// </summary>
	[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
	public class CerasStableNameAttribute : Attribute {
		public string Name { get; }
		public CerasStableNameAttribute(string name) { Name = name; }
	}

	// TODO: exposing those is probably not a good idea; too big a footgun...
	// maybe we can just improve the heuristic or so? Or tell people to seal their types?

	/// <summary>
	/// Forces a collection field to be treated as monomorphic: all elements are guaranteed to be
	/// exactly the declared element type, so no per-element type ID is written.
	/// Auto-detected for value types and sealed classes; use this to override for non-sealed types
	/// you know will never be subclassed in saves.
	/// </summary>
	[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
	internal class MonomorphicAttribute : Attribute { }

	/// <summary>
	/// Forces a collection field to be treated as polymorphic even when auto-detection would
	/// mark it as monomorphic (e.g. a sealed class that you plan to extend later).
	/// </summary>
	[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
	internal class PolymorphicAttribute : Attribute { }
}
