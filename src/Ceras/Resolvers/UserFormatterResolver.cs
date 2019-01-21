using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Resolvers
{
	using Formatters;

	// Unused for now, maybe we'll add it back later...
	class UserFormatterResolver : IFormatterResolver
	{
		Dictionary<Type, IFormatter> _userFormatters = new Dictionary<Type, IFormatter>();

		public int UserFormatterCount => _userFormatters.Count;

		public void AddFormatterAndDetectType(IFormatter formatter)
		{
			var ft = Helpers.ReflectionHelper.FindClosedType(formatter.GetType(), typeof(IFormatter<>));
			if (ft == null)
				throw new ArgumentException("AddFormatter needs an object that implements IFormatter<>");

			var t = ft.GetGenericArguments()[0];

			ThrowIfExists(t, formatter);

			_userFormatters.Add(t, formatter);
		}

		public void AddFormatter<T>(IFormatter<T> formatter)
		{
			ThrowIfExists(typeof(T), formatter);

			_userFormatters.Add(typeof(T), formatter);
		}

		void ThrowIfExists(Type serializedType, object formatter)
		{
			var f = GetFormatter(serializedType);
			if (f != null)
				throw new InvalidOperationException($"Cannot add new formatter '{formatter.GetType().Name}', because formatter '{f.GetType().Name}' is already registered to handle Type '{serializedType.Name}'");
		}

		public IFormatter GetFormatter(Type type)
		{
			IFormatter f;
			if(_userFormatters.TryGetValue(type, out f))
				return f;

			return null;
		}
	}
}
