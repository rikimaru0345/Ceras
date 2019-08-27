using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;


namespace Ceras.Helpers
{
	using static Expression;

	public delegate bool EqualsDelegate<TStruct>(ref TStruct left, ref TStruct right);
	static class StructEquality<T>
	{
		public static EqualsDelegate<T> EqualFunction { get; }
		public static LambdaExpression Lambda { get; }


		public static bool AreEqual(ref T left, ref T right) => EqualFunction(ref left, ref right);


		static StructEquality()
		{
			if (!typeof(T).IsValueType || typeof(T).IsPrimitive)
				throw new InvalidOperationException("T must be a non-primitive value type (a struct)");

			(EqualFunction, Lambda) = GenerateEq();
		}

		static (EqualsDelegate<T>, LambdaExpression) GenerateEq()
		{
			var type = typeof(T);

			var left = Parameter(type.MakeByRefType(), "left");
			var right = Parameter(type.MakeByRefType(), "right");
			var methodEnd = Label(typeof(bool), "methodEnd");
			var body = new List<Expression>();

			foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{
				var fLeft = Field(left, f);
				var fRight = Field(right, f);

				// "if( left.f != right.f )  return false;"
				body.Add(IfThen(
					Not(AreFieldsEqual(f.FieldType, fLeft, fRight)),
					Return(methodEnd, Constant(false))
					));
			}

			// "return true;"
			body.Add(Label(methodEnd, Constant(true)));

			var lambda = Expression.Lambda(
				delegateType: typeof(EqualsDelegate<>).MakeGenericType(type),
				body: Block(body),
				left, right);
			var del = lambda.Compile();

			return ((EqualsDelegate<T>)del, lambda);
		}

		static Expression AreFieldsEqual(Type fieldType, MemberExpression leftField, MemberExpression rightField)
		{
			// ReferenceTypes: ReferenceEqual()
			if (!fieldType.IsValueType)
				return ReferenceEqual(leftField, rightField);

			// Primitives: Equal()
			if (fieldType.IsPrimitive)
				return Equal(leftField, rightField);

			// Try custom equality operator if it exists
			try
			{
				var customEq = Equal(leftField, rightField);
				return customEq;
			}
			catch { }

			// Structs: Recurse into AreEqual()

			const bool resolveLambda = true;

			if (resolveLambda)
			{
				var lambdaProp = typeof(StructEquality<>).MakeGenericType(fieldType).GetProperty(nameof(Lambda));
				var eqLambda = (LambdaExpression)lambdaProp.GetValue(null);
				return Invoke(eqLambda, leftField, rightField);
			}
			else
			{
				var eqMethod = typeof(StructEquality<>).MakeGenericType(fieldType).GetMethod(nameof(AreEqual), BindingFlags.Static | BindingFlags.Public);
				return Call(method: eqMethod, arg0: leftField, arg1: rightField);
			}
		}

	}

}
