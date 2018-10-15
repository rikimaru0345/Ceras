using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Helpers
{
	using Formatters;

	class ObjectSchema
	{
		public List<SerializedMember> Members = new List<SerializedMember>();

	}

	struct SchemaMember
	{
		public string PersistentName; // If set, this gets written as type name
		public bool IsSkip; // If this is true, member and override formatter are not used; while reading the element is skipped (by reading its size)
		public SerializedMember Member;
		public IFormatter OverrideFormatter;
	}

	class DynamicSchemaFormatter<T> : IFormatter<T>
	{
		// generate Serializer/Deserializer from schema
		// - if member is a built-in value-type: write normally and continue.
		//
		// - reserve space in front of every member (add 4 bytes)
		// - write member
		// - write written size
	}
}


/*
 * ConstructorFormatter
 * - remove the "CreateInstanceIfNeeded" 
 * - replace all "read into member" with "read into local"
 * - add "call constructor (which could also be a normal static method) from locals"
 * - set remaining members from locals
 */