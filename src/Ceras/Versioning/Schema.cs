namespace Ceras.Helpers
{
	using Formatters;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Reflection;

	/*
	* A schema is super simple.
	* It's just the members of an object (with an emphasis that the order in which they appear is important as well)
	* Optionally the user can provide a custom formatter.
	*
	* At serialization time:
	* - Write schema to buffer
	* - Write objects using schema formatter (which will prefix every written member with its size)
	*
	* At deserialization time:
	* - Read schema from file
	*    - Some members might not be found anymore (bc they were removed), so they'll be marked with IsSkip=true
	* - Generate a DynamicSchemaFormatter using this schema
	* - Use it to read the data
	*/
	class Schema
	{
		public Type Type;
		public List<SchemaMember> Members = new List<SchemaMember>();

		// this assumes that a schema will not change after being created
		// that's ok since Schema is not public
		int _hash = -1;

		protected bool Equals(Schema other)
		{
			if (Type != other.Type)
				return false;

			if (Members.Count != other.Members.Count)
				return false;

			for (int i = 0; i < Members.Count; i++)
			{
				if (Members[i].Member.MemberInfo != other.Members[i].Member.MemberInfo)
					return false;
			}

			return true;
		}

		public override bool Equals(object obj)
		{
			if (ReferenceEquals(null, obj))
				return false;
			if (ReferenceEquals(this, obj))
				return true;
			if (obj.GetType() != GetType())
				return false;
			return Equals((Schema)obj);
		}

		public override int GetHashCode()
		{
			if (_hash == -1)
				unchecked
				{
					var hashSource = Type.FullName + string.Join("", Members.Select(m => m.Member.MemberType.FullName + m.Member.MemberInfo.Name));
					_hash = hashSource.GetHashCode();
				}

			return _hash;
		}




		internal static MemberInfo FindMember(Type type, string name)
		{
			foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
			{
				if (member is FieldInfo f)
				{
					if (IsMatch(f, name))
						return f;
				}
				else if (member is PropertyInfo p)
				{
					if (IsMatch(p, name))
						return p;
				}
			}

			return null;
		}

		static bool IsMatch(MemberInfo member, string name)
		{
			if (member.Name == name)
				return true;

			var attrib = member.GetCustomAttribute<PreviousNameAttribute>();
			if (attrib != null)
			{
				if (attrib.Name == name)
					return true;

				if (attrib.AlternativeNames.Any(n => n == name))
					return true;
			}

			return false;
		}

	}

	class SchemaMember
	{
		public string PersistentName; // If set, this gets written as type name
		public bool IsSkip; // If this is true, member and override formatter are not used; while reading the element is skipped (by reading its size)
		public SerializedMember Member;
		
		// public IFormatter OverrideFormatter;

		public override string ToString()
		{
			var str = $"{PersistentName}";
			if (IsSkip)
				str = "[SKIP] " + str;

			return str;
		}
	}

}