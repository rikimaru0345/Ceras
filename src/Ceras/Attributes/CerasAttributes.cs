using System;


namespace Ceras
{
	using Formatters;
	using Helpers;


	/// <summary>
	/// Add this to a field or property to force Ceras to ignore it.
	/// Check out the tutorial to see in what order attributes, the ShouldSerialize callback and other settings are evaluated.
	/// </summary>
	[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
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
	/// Configure what members to include by default in this type, you can also add [Exclude] and [Include] to individual members as well to override the member config.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
	public sealed class MemberConfigAttribute : Attribute
	{
		public TargetMember TargetMembers { get; set; }
		public ReadonlyFieldHandling ReadonlyFieldHandling { get; set; }

		public MemberConfigAttribute(TargetMember targetMembers = TargetMember.PublicFields, ReadonlyFieldHandling readonlyFieldHandling = ReadonlyFieldHandling.ExcludeFromSerialization)
		{
			TargetMembers = targetMembers;
			ReadonlyFieldHandling = readonlyFieldHandling;
		}
	}

	/// <summary>
	/// Use this to override global or class-level settings for a single field or property.
	/// </summary>
	[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
	public sealed class ReadonlyConfigAttribute : Attribute
	{
		public ReadonlyFieldHandling ReadonlyFieldHandling { get; set; }

		public ReadonlyConfigAttribute(ReadonlyFieldHandling readonlyFieldHandling = ReadonlyFieldHandling.ExcludeFromSerialization)
		{
			ReadonlyFieldHandling = readonlyFieldHandling;
		}
	}

	/// <summary>
	/// What members should be serialized and deserialized
	/// </summary>
	[Flags]
	public enum TargetMember
	{
		None = 0,

		/// <summary>
		/// Include all fields with the "public" keyword, pretty obvious. As with all visibility checks, "public" just means "has the public keyword", and doesn't require complete outside visibility (like, the containing class or struct doesn't have to be public for the field to count as public)
		/// </summary>
		PublicFields = 1 << 0,
		/// <summary>
		/// Include all private fields. Hidden compiler-generated fields (like backing fields for properties, enumerator state-machines, ...) are not included.
		/// </summary>
		PrivateFields = 1 << 1,
		/// <summary>
		/// Only properties marked with the "public" keyword. So properties marked as internal/protected/private are not included
		/// </summary>
		PublicProperties = 1 << 2,
		/// <summary>
		/// Properties that are not public (so "internal", "private", or "protected")
		/// </summary>
		PrivateProperties = 1 << 3,

		AllPublic = PublicFields | PublicProperties,
		AllPrivate = PrivateFields | PrivateProperties,

		AllFields = PublicFields | PrivateFields,
		AllProperties = PublicProperties | PrivateProperties,

		All = PublicFields | PrivateFields | PublicProperties | PrivateProperties
	}

	/// When using the 'VersionTolerance' feature you might sometimes rename a member.
	/// In order to be able to still deserialize data created in the old format Ceras needs to know what member an old name should be mapped to.
	/// Add this attribute to your renamed member to specify any old names this member had previously.
	/// <para>PersistentName is the name Ceras will write</para>
	/// <para>AlternativeNames is an array of names that will be used when trying to find a member defined in an older version of the data</para>
	[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
	public class MemberNameAttribute : Attribute
	{
		public readonly string PersistentName;
		public readonly string[] AlternativeNames = new string[0];
		
		public MemberNameAttribute(string persistentName)
		{
			PersistentName = persistentName;
		}
		
		public MemberNameAttribute(string persistentName, params string[] alternativeNames)
		{
			PersistentName = persistentName;
			AlternativeNames = alternativeNames;
		}
	}


	/// <summary>
	/// Put this on any constructor or static-method (within the same type) as a hint of what constructor/factory to use by default. (Can also be overriden through <see cref="SerializerConfig.ConfigType{T}"/>)
	/// </summary>
	[AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method)]
	public class CerasConstructorAttribute : Attribute
	{
	}

	
	/// <summary>
	/// Add this to any method of your class to let Ceras call it during serialization.
	/// <para>The method must have 'void' as return type, and not take any parameters</para>
	/// </summary>
	[AttributeUsage(AttributeTargets.Method)]
	public class OnBeforeSerializeAttribute : Attribute
	{
	}
	
	/// <summary>
	/// Add this to any method of your class to let Ceras call it during serialization.
	/// <para>The method must have 'void' as return type, and not take any parameters</para>
	/// </summary>
	[AttributeUsage(AttributeTargets.Method)]
	public class OnAfterSerializeAttribute : Attribute
	{
	}
	
	/// <summary>
	/// Add this to any method of your class to let Ceras call it during serialization.
	/// <para>The method must have 'void' as return type, and not take any parameters</para>
	/// </summary>
	[AttributeUsage(AttributeTargets.Method)]
	public class OnBeforeDeserializeAttribute : Attribute
	{
	}
	
	/// <summary>
	/// Add this to any method of your class to let Ceras call it during serialization.
	/// <para>The method must have 'void' as return type, and not take any parameters</para>
	/// </summary>
	[AttributeUsage(AttributeTargets.Method)]
	public class OnAfterDeserializeAttribute : Attribute
	{
	}
}

namespace Ceras.Formatters
{
	/// <summary>
	/// Add this attribute to your <see cref="IFormatter{T}"/> class to configure Ceras' dependency injection system. (Only needed to turn it off, it's enabled, even for private fields, by default)
	/// </summary>
	[AttributeUsage(AttributeTargets.Class)]
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

	/// <summary>
	/// This attribute is used by the AotGenerator. It places this attribute on all formatters it generates so they can be identified as ahead-of-time generated formatters.
	/// This is very important so the AotGenerator can properly distinguish between formatters it has written itself, and formatters written by the user.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class)]
	public class AotGeneratedFormatterAttribute : Attribute
	{
	}

}

namespace Ceras.Formatters.AotGenerator
{
	/// <summary>
	/// When using the formatter generator you should have a static method somewhere that returns a SerializerConfig, the tool will find the method using this attribute and then execute it so it can use the same config that you are using when the program runs.
	/// That way the generator knows what types to generate formatters for, and what members to include or exclude.
	/// The tool inspects 'KnownTypes' as well as all types that you configured using the ConfigType methods. Types that are only handled in "OnConfigNewType" will not be handled in any way because that method is only called when any type is actually encountered (serialized or deserialized).
	/// </summary>
	[AttributeUsage(AttributeTargets.Method)]
	public class AotSerializerConfigAttribute : Attribute
	{
	}

	/// <summary>
	/// Put this attribute on every class/struct you want to serialize when you are using IL2CPP (or any other AOT scenario).
	/// The formatter generator looks for it and generates formatters only for types with the attribute.
	/// If a base type (for example 'abstract class NetworkMessage') has this attribute, then all derived types that the tool can find will also have a formatter generated for them.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
	public class GenerateFormatterAttribute : Attribute
	{
	}
}
