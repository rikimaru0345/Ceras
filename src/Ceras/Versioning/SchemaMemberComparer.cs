namespace Ceras
{
	using System;
	using System.Collections.Generic;
	using System.Reflection;
	using Helpers;

	/*
	 * When do we order members?
	 * - Only when *not* using VersionTolerance, because when we do, we don't need any implicit ordering.
	 *   Because then the 'Schema' fully describes how the binary data looks like, including member order.
	 * 
	 * Why do we order members?
	 * - Must have stable order (because .NET/C# do *not* ensure stable order on their own)
	 * - Reading/writing the same types - very slightly - improves performance because we're executing the same code (formatter) many times
	 * - Grouping fixed-size types allows us to merge all those "do we have space for this in the target buffer??" into one big EnsureCapacity() (to be done)
	 * - Prevent ambiguity when a base and derived type have the same member (for example: private int with same name in both)
	 */
	class SchemaMemberComparer : IComparer<SchemaMember>
	{
		public static readonly SchemaMemberComparer Instance = new SchemaMemberComparer();
		

		public int Compare(SchemaMember x, SchemaMember y)
		{
			var name1 = GetComparisonName(x);
			var name2 = GetComparisonName(y);

			return string.Compare(name1, name2, StringComparison.Ordinal);
		}

		static string GetComparisonName(SchemaMember m)
		{
			// todo:
			// - ensure type names can be customized as well (and that those custom names are used here)
			//   otherwise we might order members differently when someone uses obfuscation (where names of types and members change between every build)

			// It's not just the contents, it's also this exact ordering of the elements that is important as well. Read above for more information
			return
				// Group fixed-size types so we can optimize out the 'do we have enough space left' while writing
				(ReflectionHelper.IsBlittableType(m.MemberType) ? "1" : "2") + 

				// Group by type: tiny performance improvement (repeated use of the same formatter)
				m.MemberType.FullName +

				// Actual name
				m.PersistentName + 

				// Prevent ambiguity with inheritance (ex: two private ints with same name defined in base and derived class)
				m.MemberInfo.DeclaringType.FullName +

				// Unsure if it can happen in some scenarios, but just to be sure we differentiate between fields and props
				(m.MemberInfo is FieldInfo ? "f" : "p");
		}
	}
}