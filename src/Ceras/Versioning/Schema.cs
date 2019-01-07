namespace Ceras.Helpers
{
	using Ceras.Formatters;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Reflection;
	using System.Runtime.CompilerServices;

	/*
	 * A schema just contains the
	 * - The Type the Schema is for
	 * - All serialized members (ordered!)
	 * - For each member it contains:
	 *		- The persistent name (normally that's just the member-name, but can overriden by the user)
	 *		- Whether or not to skip the entry (when reading an old object some members might not be present anymore)
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
		public bool IsPrimary { get; }
		public List<SchemaMember> Members { get; } = new List<SchemaMember>();

		public IFormatter SpecificFormatter;
		public IFormatter ReferenceFormatter;

		public Schema(bool isPrimary, Type type)
		{
			IsPrimary = isPrimary;
			Type = type;
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

				if (a.Member.MemberInfo != b.Member.MemberInfo)
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
						return m.Member.MemberType.FullName + m.Member.MemberInfo.Name;
					}));
					_hash = hashSource.GetHashCode();

				}

			return _hash;
		}




		internal static MemberInfo FindMemberInType(Type type, string name)
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


		// Removed until we are ready to deal with the V2 of version tolerance (to include type-information)
		/*
		static IFormatter DetermineOverrideFormatter(MemberInfo memberInfo)
		{
			var prevType = memberInfo.GetCustomAttribute<PreviousType>();
			if (prevType != null)
				return GetGenericFormatter(prevType.MemberType);

			var prevFormatter = memberInfo.GetCustomAttribute<PreviousFormatter>();
			if (prevFormatter != null)
			{
				var formatter = ReflectionHelper.FindClosedType(prevFormatter.FormatterType, typeof(IFormatter<>));
				if (formatter == null)
					throw new Exception($"Type '{prevFormatter.FormatterType.FullName}' must inherit from IFormatter<>");

				return (IFormatter)Activator.CreateInstance(formatter);
			}

			return null;
		}
		*/
	}

	/*
		 * todo:
		 * 
		 * Idea:
		 * Eventually we want to be able to skip over Schemata.
		 * To do this we'd prefix the data with: 
		 *	- Number of Schemata
		 *	- All the type names + hash of their schema
		 *	- offset to skip over the schema data
		 * 
		 * Problem:
		 *  - schema definitions contain type names, which have been written by the TypeFormatter.
		 *    We MUST read those because it's possible that a Type is later referenced again (that time by its ID!)
		 *    So we must read all the type-names anyway so the type-name-cache is populated correctly.
		 *    
		 *  - Right now we already write the Schema before the type, and we tell the serializer that we've made use of that schema.
		 *    That way the serializer knows which schemata to prefix the data with.
		 *    The problem is of course that we're using a HashSet for that, which means the order is back to random.
		 *    So the type-names are actually not written in the right order.
		 *
		 *  - if we write schema types after the data; then the data contains the actual strings,
		 *    and while reading we need to read the schemata FIRST, potentially referring us to a string that was supposedly already there but is not
		 *
		 *  -> can we know the schema beforehand? no, there might be objects that are hidden in <object> or interface fields!
		 *
		 *  -> we must write type names at the very beginning
		 *
		 *
		 * Other approach:
		 * Schema data interweaved. Keep track of what types are written.
		 * When a type name is written in full, also write the schema for it right into the data (with has prefix)
		 * When reading we read the type (and cache it), then the schema hash; potentially ignoring the schema data because we already have a schema+formatter for that
		 *
		 * 1.) Write schema together with type-name as needed
		 * 2.) While reading, make use of the schema hash to reuse an existing schema + use "skip-reader" to quickly read over the schema data (only happens once, so its ok)
		 *
		 * Trying to rescue approach #1?
		 * We would need to ensure type names are written in full only in the schema, because that is read first;
		 * - When a type has to be written: add it to a list, pseudo caching it, and instantly emit the cacheId.
		 * - After writing the data: emit schemata in the correct order; emit the type names in full.
		 *
		 *
		 * Approach 1 vs 2:
		 * - First approach collects all schema data into the beginning of the file
		 *   + could potentially extract it into a separate thing maybe?
		 * - Second approach writes schema data inline with the type-name
		 *   + easier to do
		 *
		 * Is one always faster than the other?
		 *  + inline is likely faster to implement
		 *
		 *
		 * Scenario: Multiple files all with same schema
		 * - would like to have schema data shared somewhere
		 * - but then we'd like to put all the objects into one big file anyway
		 * - doing that would require some sort of database because we need fast access to entries and being able to rewrite an entry (with dynamic size change)
		 *
		 * Abort?
		 * - for simple versioning maybe use json
		 * - but we'd lose IExternalRootObject, which is super-bad, but we could do some special formatting, to write an ID instead just like Ceras
		 * - why do we even want versioning info??
		 *    -> settings files? maybe DB-like functionality?
		 * - assuming db: where do we put lists and strings?
		 *
		 */


	struct SchemaMember
	{
		public readonly string PersistentName; // If set, this gets written as type name
		public readonly SerializedMember Member;

		public bool IsSkip => Member.MemberInfo == null; // If this is true, then member and override formatter are not used; while reading the element is skipped (by reading its size)

		public readonly ReadonlyFieldHandling ReadonlyFieldHandling;

		// public IFormatter OverrideFormatter;


		public SchemaMember(string persistentName, SerializedMember serializedMember, ReadonlyFieldHandling readonlyFieldHandling)
		{
			PersistentName = persistentName;
			Member = serializedMember;
			ReadonlyFieldHandling = readonlyFieldHandling;
		}

		// Used when reading a schema and the member was not found
		public SchemaMember(string persistentName)
		{
			PersistentName = persistentName;
			Member = default;
			ReadonlyFieldHandling = ReadonlyFieldHandling.Off;
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