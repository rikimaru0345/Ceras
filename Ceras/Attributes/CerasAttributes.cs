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


	[AttributeUsage(AttributeTargets.Class)]
	public sealed class CerasConfig : Attribute
	{
		public MemberSerialization MemberSerialization { get; set; }
		public bool IncludePrivate { get; set; }
	}

	public enum MemberSerialization
	{
		OptOut,
		OptIn,
	}

	enum IncludeOptions
	{
		None,
		NonPublic = 1 << 0,
		Protected = 1 << 1,
		Public = 1 << 2,
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
