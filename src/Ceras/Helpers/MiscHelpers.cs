using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Helpers
{
	static class MiscHelpers
	{
		public static string Singular<T>(this T enumValue)
		{
			if (!typeof(T).IsEnum)
				throw new ArgumentException();
			
			var str = enumValue.ToString();

			if (str.EndsWith("ies"))
				return str.Substring(0, str.Length - 3) + "y";
			
			return str.TrimEnd('s');
		}

		
		// Remove nonsense like 'm_' from variable names
		public static string CleanMemberName(string name)
		{
			if (name.StartsWith("m_"))
				return name.Remove(0, 2);
			if (name.StartsWith("_"))
				return name.Remove(0, 1);

			return name;
		}

		// https://github.com/morelinq/MoreLINQ/blob/master/MoreLinq/DistinctBy.cs
		public static IEnumerable<TSource> DistinctBy<TSource, TKey>(this IEnumerable<TSource> source,
																	 Func<TSource, TKey> keySelector)
		{
			if (source == null) throw new ArgumentNullException(nameof(source));
			if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));

			return _(); IEnumerable<TSource> _()
			{
				var knownKeys = new HashSet<TKey>();
				foreach (var element in source)
				{
					if (knownKeys.Add(keySelector(element)))
						yield return element;
				}
			}
		}
	}
}
