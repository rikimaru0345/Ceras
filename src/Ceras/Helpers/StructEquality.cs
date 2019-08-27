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
	static class StructEquality<T>
	{
		public static bool AreEqual(ref T left, ref T right) => EqualFunction(ref left, ref right);

		public static EqualsDelegate<T> EqualFunction { get; }
		public static LambdaExpression Lambda { get; }

		static StructEquality()
		{
			if (!typeof(T).IsValueType || typeof(T).IsPrimitive)
				throw new InvalidOperationException("T must be a non-primitive value type (a struct)");

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

	static class StructEquality
	{
		internal static readonly bool resolveLambda = true;
		internal static readonly bool useIEquatable = false;

		// Knowing that <T> is a struct, compare all of its fields one-by-one
		[MethodImpl(MethodImplOptions.Synchronized)]
		internal static Expression IsStructEqual(Expression left, Expression right)
		{
			if (left.Type != right.Type)
				throw new InvalidOperationException("left and right expressions have the same type");

			var type = left.Type; // Expression automatically strips 'byRef'

			if (!type.IsValueType || type.IsPrimitive)
				throw new InvalidOperationException("T must be a non-primitive value type (a struct)");

			/*
			 * return (
			 *		(left.f1 == right.f1) &&
			 *		(left.f2 == right.f2) &&
			 *		(left.f3 == right.f3) &&
			 *		...
			 * );
			 */
			var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

			var fieldEqualities = fields.Select(f => IsFieldEqual(Field(left, f), Field(right, f)));
			var andAll = fieldEqualities.Aggregate((a, b) => AndAlso(a, b));

			return andAll;
		}

		// Given the fields (left, right) of unknown types, determine the right comparison method (ReferenceEquals, Equals, RecurseIntoStruct)
		static Expression IsFieldEqual(MemberExpression left, MemberExpression right)
		{
			var fieldType = ((FieldInfo)left.Member).FieldType;

			//
			// ReferenceTypes: ReferenceEqual()
			if (!fieldType.IsValueType)
				return ReferenceEqual(left, right);

			//
			// Primitives: Equal()
			if (fieldType.IsPrimitive)
				return Equal(left, right);

			//
			// Nullable: compare directly
			if (Nullable.GetUnderlyingType(fieldType) != null)
				return IsStructEqual(left, right);

			//
			// IEquatable<T>: strongly typed custom implementation
			// Not preferable because it takes the parameter by-value, instead of by-reference
			if (useIEquatable)
			{
				var equatable = typeof(IEquatable<>).MakeGenericType(fieldType);
				if (equatable.IsAssignableFrom(fieldType))
				{
					var typedEquals = fieldType.GetMethod(nameof(IEquatable<int>.Equals), new Type[] { fieldType });
					return Call(left, typedEquals, right);
				}
			}


			// !! What if a user defines 'bool Equals(MyStruct other)'
			// !! but doesn't mark it as implementing 'IEquatable<MyStruct>' ??
			// !! If they only override Equals() that'd be really bad.
			//
			// override Equals()
			// try { return Equal(leftField, rightField); } catch { }


			// 
			// Structs: recurse into AreEqual (either by call, or by unpacking the lambda)
			return GetStructEquality(left, right);
		}

		// Create a 'method call' or 'lambda invoke' to compare the fields of the two given structs
		static Expression GetStructEquality(MemberExpression left, MemberExpression right)
		{
			var fieldType = ((FieldInfo)left.Member).FieldType;

			// Resolving lambda gives an improvement from 20x slower -> 3-5x slower
			if (resolveLambda)
			{
				// Get and unpack:
				// 'StructEquality<fieldType>.Lambda'
				var lambdaProp = typeof(StructEquality<>).MakeGenericType(fieldType).GetProperty(nameof(Lambda));
				var eqLambda = (LambdaExpression)lambdaProp.GetValue(null);
				return Invoke(eqLambda, left, right);
			}
			else
			{
				// Call:
				// 'StructEquality<fieldType>.AreEqual(ref left, ref right);
				var eqMethod = typeof(StructEquality<>).MakeGenericType(fieldType).GetMethod(nameof(StructEquality<int>.AreEqual), BindingFlags.Static | BindingFlags.Public);
				return Call(method: eqMethod, arg0: left, arg1: right);
			}
		}
	}
}
