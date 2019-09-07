using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;


namespace Ceras.Helpers
{
	using static Expression;

	public delegate bool EqualsDelegate<TStruct>(ref TStruct left, ref TStruct right);
	public static class StructEquality<T>
	{
		public static bool AreEqual(ref T left, ref T right) => EqualFunction(ref left, ref right);

		public static EqualsDelegate<T> EqualFunction { get; }
		public static LambdaExpression Lambda { get; }

		static StructEquality()
		{
			Lambda = GenerateEqualityExpression();
			EqualFunction = (EqualsDelegate<T>)Lambda.Compile();
		}

		static LambdaExpression GenerateEqualityExpression()
		{
			var type = typeof(T);
			var left = Parameter(type.MakeByRefType(), "left");
			var right = Parameter(type.MakeByRefType(), "right");

			var isEqExp = StructEquality.IsStructEqual(left, right);

			var delType = typeof(EqualsDelegate<>).MakeGenericType(type);
			return Lambda(delType, isEqExp, left, right);
		}
	}

	public static class StructEquality
	{
		// Knowing that <T> is a ValueType, compare it or all of its fields one-by-one
		[MethodImpl(MethodImplOptions.Synchronized)]
		public static Expression IsStructEqual(Expression left, Expression right)
		{
			if (left.Type != right.Type)
				throw new InvalidOperationException("left and right expressions have the same type");

			var type = left.Type; // Expression automatically strips 'byRef'

			if (!type.IsValueType)
				throw new InvalidOperationException($"Type T must be a value type, given '{type.FriendlyName(true)}'");

			return IsEqual(left, right);
		}

		public static Delegate GetEqualsDelegate(Type type)
		{
			var se = typeof(StructEquality<>).MakeGenericType(type);
			var prop = se.GetProperty(nameof(StructEquality<int>.EqualFunction));
			return (Delegate)prop.GetValue(null);
		}

		// Determine comparison method (ReferenceEquals, Equals, RecurseIntoStruct)
		static Expression IsEqual(Expression left, Expression right)
		{
			var type = left.Type;

			//
			// ReferenceTypes: ReferenceEqual()
			if (!type.IsValueType)
				return Equal(left, right);

			//
			// Primitives: Equal()
			if (type.IsPrimitive)
				return Equal(left, right);

			// Not preferable because it takes the parameter by-value, instead of by-reference
			// IEquatable<T>: strongly typed custom implementation
			//if (useIEquatable)
			//{
			//	var equatable = typeof(IEquatable<>).MakeGenericType(type);
			//	if (equatable.IsAssignableFrom(type))
			//	{
			//		var typedEquals = type.GetMethod(nameof(IEquatable<int>.Equals), new Type[] { type });
			//		return Call(left, typedEquals, right);
			//	}
			//}

			// 
			// Struct: compare field by field
			/*
			 * return (
			 *		(left.f1 == right.f1) &&
			 *		(left.f2 == right.f2) &&
			 *		(left.f3 == right.f3) &&
			 *		...
			 * );
			 */
			var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			var fieldEqualities = fields.Select(f => IsEqual(Field(left, f), Field(right, f)));
			var andAll = fieldEqualities.Aggregate((a, b) => AndAlso(a, b));

			return andAll;
		}
	}
}
