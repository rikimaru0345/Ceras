namespace Ceras
{
	using System;
	using System.Collections.Generic;
	using System.Reflection;
	using Helpers;

	/*
	 * Sorting by memberType first gives us improved performance
	 * - We're immune to the crazy reordering of members that all C# compilers sometimes do for no (obvious) reason.
	 * - Better cache coherency when using the formatters, since we'll keep using the same type of formatter over and over again, instead of switching around wildly.
	 * - Because fixed-size types get grouped together, this enables a huge optimization: We can do one big(combined) size-check for all of them; which enables us to use the "NoSizeCheck" versions of the methods in SerializerBinary (they don't exist yet, will be added together with them, todo)
	 * - Sorting by declaring type last will improve our robustness in rare edge cases. Since we're doing ordering anyway, this optimization comes for free! (the edge case being when class A:B, and B:C, and later A and B swap names and there are private fields in each that have the same name)
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
			// It's not just the contents, it's also this exact ordering of the elements that is important as well. Read above for more information
			return
				(IsFixedSize(m.MemberType) ? "" : "") + // Enforce fixed-size types to group together
				m.MemberType.FullName + // Optimize for formatter reuse
				m.PersistentName + // Actual name
				m.MemberInfo.DeclaringType.FullName + // Ensure things are ordered correctly even when there are (inherited) fields of the exact same name and type
				(m.MemberInfo is FieldInfo ? "f" : "p"); // Unsure if it can happen in some scenarios, but we need to differentiate between fields and props
		}

		static bool IsFixedSize(Type t)
		{
			if(t.IsPrimitive)
				return true;

			if(!t.IsValueType)
				return false;

			// It is a struct
			// Structs can be fixed size if all of its members are fixed size
			foreach(var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
			{
				if(!IsFixedSize(f.FieldType))
					return false;
			}

			// The struct contains only fixed-size types, great!
			return true;
		}
	}
}