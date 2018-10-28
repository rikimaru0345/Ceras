namespace Ceras
{
	using System;
	using Formatters;


	[AttributeUsage(AttributeTargets.Field)]
	public sealed class Ignore : Attribute { }

	[AttributeUsage(AttributeTargets.Field)]
	public sealed class Include : Attribute { }


	public enum SerializationOverride
	{
		NoOverride,
		ForceInclude,
		ForceSkip,
	}


	/// <summary>
	/// Configure what members to include by default in this type.
	/// Note: 1) If the attribute is missing, the setting from the config will be used.
	/// Note: 2) Ceras checks the ShouldSerializeMember method first, then Ignore/Include attributes on the members, then the CerasConfig attribute, and last the default setting from the SerializerConfig.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class)]
	public sealed class MemberConfig : Attribute
	{
		public TargetMember TargetMembers { get; set; }

		public MemberConfig(TargetMember targetMembers = TargetMember.PublicFields)
		{
			TargetMembers = targetMembers;
		}
	}

	[Flags]
	public enum TargetMember
	{
		None = 0,

		PublicFields = 1 << 0,
		PrivateFields = 1 << 1,
		PublicProperties = 1 << 2,
		PrivateProperties = 1 << 3,

		AllPublic = PublicFields | PublicProperties,
		AllPrivate = PrivateFields | PrivateProperties,

		AllFields = PublicFields | PrivateFields,
		AllProperties = PublicProperties | PrivateProperties,

		All = PublicFields | PrivateFields | PublicProperties | PrivateProperties
	}
	
	[AttributeUsage(AttributeTargets.Field)]
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
	

	



	// todo: previous type / previous formatter would be nice to have. It's supposed to auto-convert old data to the new format (or let the user provide a formatter to read the old data)
	// at the moment the problem is that we never know in what format the data was written; we'd have to embed the data type (ewww!), or add a version number that the user provides
	// so we always know in what format we can expect the data. version number would be simply added to the binary data. 

	class PreviousFormatter : PreviousNameAttribute
	{
		public Type FormatterType { get; } // formatter that can read this old version
		
		public PreviousFormatter(Type formatterType) : base(null)
		{
			CheckType(formatterType);
			FormatterType = formatterType;
		}
		public PreviousFormatter(string previousName, Type formatterType) : base(previousName)
		{
			CheckType(formatterType);
			FormatterType = formatterType;
		}

		static void CheckType(Type formatterType)
		{
			if (!typeof(IFormatter).IsAssignableFrom(formatterType))
				throw new Exception($"The provided type {formatterType.Name} is not valid for 'PreviousFormatter', it needs to be a type that implements IFormatter<T>");
		}
	}

	class PreviousType : PreviousNameAttribute
	{
		public Type MemberType { get; } // the old type of the field/property

		public PreviousType(Type memberType) : base(null)
		{
			MemberType = memberType;
		}

		public PreviousType(string previousName, Type memberType) : base(previousName)
		{
			MemberType = memberType;
		}
	}
}
