namespace Ceras
{
	using System;


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
	sealed class Member : Attribute
	{
		readonly int _id;

		public Member(int id)
		{
			_id = id;
		}
	}

}
