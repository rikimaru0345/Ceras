namespace CerasAotFormatterGenerator
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	static class LinqEx
	{
		public static IEnumerable<T> DistinctBy<T, TKey>(this IEnumerable<T> items, Func<T, TKey> property)
		{
			return items.GroupBy(property).Select(x => x.First());
		}

		public static void AddRange<T>(this HashSet<T> set, IEnumerable<T> items)
		{
			foreach (var item in items)
				set.Add(item);
		}
	}
}