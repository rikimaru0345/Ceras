namespace Ceras
{
	using System;
	using System.Collections.Generic;
	using Helpers;

	class SchemaMemberComparer : IComparer<SchemaMember>
	{
		public static readonly SchemaMemberComparer Instance = new SchemaMemberComparer();

		static string Prefix(SchemaMember m) => m.Member.IsField ? "f" : "p";

		public int Compare(SchemaMember x, SchemaMember y)
		{
			var name1 = Prefix(x) + x.Member.MemberType.FullName + x.PersistentName;
			var name2 = Prefix(y) + y.Member.MemberType.FullName + y.PersistentName;

			return string.Compare(name1, name2, StringComparison.Ordinal);
		}
	}
}