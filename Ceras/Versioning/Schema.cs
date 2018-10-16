namespace Ceras.Helpers
{
	using System;
	using System.Collections.Generic;
	using Formatters;

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
	}

	class SchemaMember
	{
		public string PersistentName; // If set, this gets written as type name
		public bool IsSkip; // If this is true, member and override formatter are not used; while reading the element is skipped (by reading its size)
		public SerializedMember Member;
		public IFormatter OverrideFormatter;
	}

}