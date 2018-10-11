using System;

namespace Ceras.Helpers
{
	using System.Reflection;
	using static System.Linq.Expressions.Expression;

	public struct Accessors<T>
	{
		public readonly Func<object, T> Get;
		public readonly Action<object, T> Set;

		public Accessors(Func<object, T> get, Action<object, T> set)
		{
			Get = get;
			Set = set;
		}
	}

	static class FieldOrProp<TItemType>
	{
		public static Accessors<TItemType> Create(Type type, string name, BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
		{
			var value = Parameter(typeof(TItemType), "value");
			var instance = Parameter(type, "instance");

			var getter = Lambda<Func<object, TItemType>>(PropertyOrField(instance, name)).Compile();
			var setter = Lambda<Action<object, TItemType>>(Assign(PropertyOrField(instance, name), value)).Compile();

			return new Accessors<TItemType>(getter, setter);
		}
		
	}
}
