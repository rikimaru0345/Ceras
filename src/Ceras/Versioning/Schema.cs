namespace Ceras.Helpers
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Reflection;
	using System.Runtime.CompilerServices;
	using Ceras.Formatters;

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
					var hashSource = Type.FullName + string.Join("", Members.Select(m => m.Member.MemberType.FullName + m.Member.MemberInfo.Name));
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


	/*
	 * The SchemaDb maintains a list of all the Schmata for each Type.
	 * Initially there's only one Schema per Type, but if we
	 * read with VersionTolerance more Schemata might be added.
	 * 
	 * SchemaDb was previously static, but that's not possible because different serializers most likely have different configurations.
	 * Which means that even the primary schema of a type may be different (because of different TargetMembers, ShouldSerializeObject, ...)
	 */
	struct SchemaDb
	{
		readonly SerializerConfig _config;
		readonly Dictionary<Type, Schema> _typeToPrimary;
		readonly Dictionary<Type, List<Schema>> _typeToSecondaries;

		public SchemaDb(SerializerConfig config)
		{
			_config = config;
			_typeToPrimary = new Dictionary<Type, Schema>();
			_typeToSecondaries = new Dictionary<Type, List<Schema>>();
		}


		// Creates the primary schema for a given type
		internal Schema GetOrCreatePrimarySchema(Type type)
		{
			if (_typeToPrimary.TryGetValue(type, out Schema s))
				return s;


			Schema schema = new Schema(true, type);

			var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

			var classConfig = type.GetCustomAttribute<MemberConfig>();

			foreach (var m in type.GetFields(flags).Cast<MemberInfo>().Concat(type.GetProperties(flags)))
			{
				bool isPublic;
				bool isField = false, isProp = false;
				bool isReadonly = false;
				bool isCompilerGenerated = false;

				if (m is FieldInfo f)
				{
					// Skip readonly
					if (f.IsInitOnly)
					{
						isReadonly = true;

						if (_config.ReadonlyFieldHandling == ReadonlyFieldHandling.Off)
							continue;
					}

					// Readonly auto-prop backing fields
					if (f.GetCustomAttribute<CompilerGeneratedAttribute>() != null)
						isCompilerGenerated = true;

					// By default we skip hidden/compiler generated fields, so we don't accidentally serialize properties twice (property, and then its automatic backing field as well)
					if (isCompilerGenerated)
						if (_config.SkipCompilerGeneratedFields)
							continue;

					isPublic = f.IsPublic;
					isField = true;
				}
				else if (m is PropertyInfo p)
				{
					// There's no way we can serialize a prop that we can't read and write, even {private set;} props would be writeable,
					// That is becasue there is actually physically no set() method we could possibly call. Just like a "computed property".
					// As for readonly props (like string Name { get; } = "abc";), the only way to serialize them is serializing the backing fields, which is handled above (skipCompilerGenerated)
					if (!p.CanRead || !p.CanWrite)
						continue;

					// This checks for indexers, an indexer is classified as a property
					if (p.GetIndexParameters().Length != 0)
						continue;

					isPublic = p.GetMethod.IsPublic;
					isProp = true;
				}
				else
					continue;

				bool allowReadonly = _config.ReadonlyFieldHandling != ReadonlyFieldHandling.Off;
				var serializedMember = SerializedMember.Create(m, allowReadonly);

				// should we allow users to provide a formatter for each old-name (in case newer versions have changed the type of the element?)
				var attrib = m.GetCustomAttribute<PreviousNameAttribute>();

				if (attrib != null)
				{
					VerifyName(attrib.Name);
					foreach (var n in attrib.AlternativeNames)
						VerifyName(n);
				}


				var schemaMember = new SchemaMember(attrib?.Name ?? m.Name, serializedMember);


				//
				// 1.) ShouldSerializeMember - use filter if there is one
				if (_config.ShouldSerializeMember != null)
				{
					var filterResult = _config.ShouldSerializeMember(serializedMember);

					if (filterResult == SerializationOverride.ForceInclude)
					{
						schema.Members.Add(schemaMember);
						continue;
					}
					else if (filterResult == SerializationOverride.ForceSkip)
					{
						continue;
					}
				}

				//
				// 2.) Use member-attribute
				var ignore = m.GetCustomAttribute<Ignore>(true) != null;
				var include = m.GetCustomAttribute<Include>(true) != null;

				if (ignore && include)
					throw new Exception($"Member '{m.Name}' on type '{type.Name}' has both [Ignore] and [Include]!");

				if (ignore)
				{
					continue;
				}

				if (include)
				{
					schema.Members.Add(schemaMember);
					continue;
				}


				//
				// After checking the user callback (ShouldSerializeMember) and the direct attributes (because it's possible to directly target a backing field like this: [field: Include])
				// we now need to check for compiler generated fields again.
				// The intent is that if 'skipCompilerGenerated==false' then we allow checking the callback, as well as the attributes.
				// But (at least for now) we don't want those problematic fields to be included by default,
				// which would happen if any of the class or global defaults tell us to include 'private fields', because it is too dangerous to do it globally.
				// There are all sorts of spooky things that we never want to include like:
				// - enumerator-state-machines
				// - async-method-state-machines
				// - events (maybe?)
				// - cached method invokers for 'dynamic' objects
				// Right now I'm not 100% certain all or any of those would be a problem, but I'd rather test it first before just blindly serializing this unintended stuff.
				if (isCompilerGenerated)
					continue;

				//
				// 3.) Use class-attribute
				if (classConfig != null)
				{
					if (IsMatch(isField, isProp, isPublic, classConfig.TargetMembers))
					{
						schema.Members.Add(schemaMember);
						continue;
					}
				}

				//
				// 4.) Use global defaults
				if (IsMatch(isField, isProp, isPublic, _config.DefaultTargets))
				{
					schema.Members.Add(schemaMember);
					continue;
				}
			}


			// Need to sort by name to ensure fields are always in the same order (yes, that is actually a real problem that really happens, even on the same .NET version, same computer, ...) 
			schema.Members.Sort(SchemaMemberComparer.Instance);


			_typeToPrimary.Add(type, schema);

			return schema;
		}

		// Reads a schema from given data
		internal Schema ReadSchema(byte[] buffer, ref int offset, Type type)
		{
			// todo 1:
			// Maybe we'll add some sort of skipping mechanism later.
			// We write count, type-names, hashes, offset to data
			// And when reading we can prepare all the schema serializers,
			// and if we have all of them already we can skip straight to the data
			// which would save us quite a bit of time.


			//
			// Get list of secondary schemata
			List<Schema> secondaries;
			if(!_typeToSecondaries.TryGetValue(type, out secondaries))
			{
				secondaries = new List<Schema>();
				_typeToSecondaries.Add(type, secondaries);
			}


			//
			// Read Schema
			var schema = new Schema(false, type);

			var memberCount = SerializerBinary.ReadInt32(buffer, ref offset);
			for (int i = 0; i < memberCount; i++)
			{
				var name = SerializerBinary.ReadString(buffer, ref offset);

				var member = Schema.FindMemberInType(type, name);

				if(member == null)
					schema.Members.Add(new SchemaMember(name));
				else
					schema.Members.Add(new SchemaMember(name, SerializedMember.Create(member, true)));
			}

			//
			// Add entry or return existing
			var existing = secondaries.IndexOf(schema);
			if(existing == -1)
			{
				secondaries.Add(schema);
				return schema;
			}
			else
			{
				return secondaries[existing];
			}
		}

		internal void WriteSchema(ref byte[] buffer, ref int offset, Schema schema)
		{
			if (!schema.IsPrimary)
				throw new InvalidOperationException("Can't write schema that doesn't match the primary. This is a bug, please report it on GitHub!");

			// Write the schema...
			var members = schema.Members;
			SerializerBinary.WriteInt32(ref buffer, ref offset, members.Count);

			for (int i = 0; i < members.Count; i++)
				SerializerBinary.WriteString(ref buffer, ref offset, members[i].PersistentName);
		}



		static void VerifyName(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				throw new Exception("Member name can not be null/empty");
			if (char.IsNumber(name[0]) || char.IsControl(name[0]))
				throw new Exception("Name must start with a letter");

			const string allowedChars = "_";

			for (int i = 1; i < name.Length; i++)
				if (!char.IsLetterOrDigit(name[i]) && !allowedChars.Contains(name[i]))
					throw new Exception($"The name '{name}' has character '{name[i]}' at index '{i}', which is not allowed. Must be a letter or digit.");
		}

		static bool IsMatch(bool isField, bool isProp, bool isPublic, TargetMember targetMembers)
		{
			if (isField)
			{
				if (isPublic)
				{
					if ((targetMembers & TargetMember.PublicFields) != 0)
						return true;
				}
				else
				{
					if ((targetMembers & TargetMember.PrivateFields) != 0)
						return true;
				}
			}

			if (isProp)
			{
				if (isPublic)
				{
					if ((targetMembers & TargetMember.PublicProperties) != 0)
						return true;
				}
				else
				{
					if ((targetMembers & TargetMember.PrivateProperties) != 0)
						return true;
				}
			}

			return false;
		}
	}

	struct SchemaMember
	{
		public readonly string PersistentName; // If set, this gets written as type name
		public readonly SerializedMember Member;

		public bool IsSkip => Member.MemberInfo == null; // If this is true, then member and override formatter are not used; while reading the element is skipped (by reading its size)

		// public IFormatter OverrideFormatter;

		public SchemaMember(string persistentName, SerializedMember serializedMember)
		{
			PersistentName = persistentName;
			Member = serializedMember;
		}

		public SchemaMember(string persistentName)
		{
			PersistentName = persistentName;
			Member = default;
		}

		public override string ToString()
		{
			var str = $"{PersistentName}";
			if (IsSkip)
				str = "[SKIP] " + str;

			return str;
		}
	}

}