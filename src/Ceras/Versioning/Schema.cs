namespace Ceras.Helpers
{
	using Ceras.Formatters;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Reflection;
	using System.Runtime.CompilerServices;

	/*
	 * A 'Schema' is really simple, all it contains is:
	 * - the Type the Schema is for
	 * - the serialized members (ordered!)
	 * - and for each member:
	 *		- its persistent name (normally that's just the member-name, but can overriden by the user)
	 *		- whether or not to skip it (when reading an old object some members might not be present anymore)
	 *
	 * The persistent name is only used when writing the Schema; and while reading the persistent name is just recorded but never used,
	 * because it only serves to lookup the target member.
	 * 
	 * The "primary" means that the schema is the current one; as in it was not read from some data.
	 * In other words, its the schema for the type as it currently is in the application, not a schema of an older version of the type.
	 *
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

	// !! DON'T USE GetHashCode(), the current implementation does not consider writing the hash to a file (and .NET hashcodes always change from one program start to another)
	// !! If we ever want to embedd the hashcode we need to switch to xxHash or something (xxHash seems to be the best)
	class Schema
	{
		public Type Type { get; }
		public TypeConfig TypeConfig { get; }
		public bool IsStatic { get; }
		public bool IsPrimary { get; }
		public List<SchemaMember> Members { get; } = new List<SchemaMember>();
		
		public Schema(bool isPrimary, Type type, TypeConfig typeConfig, bool isStatic)
		{
			IsPrimary = isPrimary;
			Type = type;
			TypeConfig = typeConfig;
			IsStatic = isStatic;
		}

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
				var a = Members[i];
				var b = other.Members[i];

				if (a.PersistentName != b.PersistentName)
					return false;

				if (a.IsSkip != b.IsSkip)
					return false;

				if (a.MemberInfo != b.MemberInfo)
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
					var hashSource = Type.FullName + string.Join("", Members.Select(m =>
					{
						if (m.IsSkip)
							return "skip";
						return m.MemberType.FullName + m.MemberInfo.Name;
					}));
					_hash = hashSource.GetHashCode();

				}

			return _hash;
		}


		internal static MemberInfo FindMemberInType(Type type, string name, bool isStatic)
		{
			var members = isStatic
					? type.GetAllStaticDataMembers()
					: type.GetAllDataMembers();

			foreach (var member in members)
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
		public string PersistentName { get; } // If set, this gets written as type name
		public MemberInfo Member { get; }
		public int WriteBackOrder { get; } // when to write the data back to the target (uses [DataMember.Order])

		public MemberInfo MemberInfo => Member;
		public Type MemberType => Member is FieldInfo f ? f.FieldType : ((PropertyInfo)Member).PropertyType;
		public string MemberName => Member.Name;
		public bool IsSkip => MemberInfo == null; // If this is true, then member and override formatter are not used; while reading the element is skipped (by reading its size)


		public SchemaMember(string persistentName, MemberInfo memberInfo, int writeBackOrder)
		{
			if (memberInfo == null)
				throw new ArgumentNullException(nameof(memberInfo));
			
			var declaringType = memberInfo.DeclaringType;
			if (declaringType == null)
				throw new Exception("declaring type is null");

			if (memberInfo is PropertyInfo p)
			{
				if (!p.CanRead || !p.CanWrite)
					throw new Exception("property must be readable and writable");
			}
			
			PersistentName = persistentName;
			Member = memberInfo;
			WriteBackOrder = writeBackOrder;
		}

		// Used when reading a schema and the member was not found
		// >> IsSkip == true
		public SchemaMember(string persistentName)
		{
			PersistentName = persistentName;
			Member = default;
			WriteBackOrder = 0;
		}

		public override string ToString()
		{
			var str = $"{PersistentName}";
			if (IsSkip)
				str = "[SKIP] " + str;

			return str;
		}
	}

	class SchemaComplex
	{
		readonly List<Schema> _schemata;
		readonly int _hash;

		public SchemaComplex(List<Schema> schemata)
		{
			_schemata = schemata;
			_hash = CalculateHash();
		}

		int CalculateHash()
		{
			int hash = 17;

			for (int i = 0; i < _schemata.Count; i++)
				hash = hash * 31 + _schemata[i].GetHashCode();

			return hash;
		}

		public override int GetHashCode()
		{
			return _hash;
		}

		public override bool Equals(object obj)
		{
			var other = obj as SchemaComplex;
			if (other == null)
				return false;

			if (_hash != other._hash)
				return false;

			if (_schemata.Count != other._schemata.Count)
				return false;

			for (int i = 0; i < _schemata.Count; i++)
				if (!_schemata[i].Equals(other._schemata[i]))
					return false;

			return true;
		}
	}
}