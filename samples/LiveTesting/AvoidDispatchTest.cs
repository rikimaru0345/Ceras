using Ceras.Formatters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace LiveTesting.AvoidDispatch
{
	using static Expression;

	static class AvoidDispatchTest
	{
		public static void Test()
		{
			// Is there a way to avoid dispatching?
			// It seems like Expressions cannot handle ByRef types yet...


			var derivedFormatter = new DerivedFormatter();
			var baseFormatter = new BaseFormatter(derivedFormatter);

			int offset = 0;
			BaseObject baseObj = null;
			baseFormatter.Deserialize(null, ref offset, ref baseObj);

			Console.WriteLine();
		}
	}

	class BaseFormatter : IFormatter<BaseObject>
	{
		IFormatter _derivedFormatter;
		
		DeserializeDelegate<BaseObject> _deserializeDispatcher1;
		DeserializeDelegate<BaseObject> _deserializeDispatcher2;

		public BaseFormatter(IFormatter derivedFormatter)
		{
			_derivedFormatter = derivedFormatter;

			// CreateExpressionDispatcher();
			
			var method = _derivedFormatter.GetType().GetMethod("Deserialize");

			var dynMethod = new DynamicMethod("dispatcher", typeof(void), new Type[] { typeof(byte[]), typeof(int).MakeByRefType(), typeof(BaseObject).MakeByRefType() });

			var ilGen = dynMethod.GetILGenerator();
			
			ilGen.Emit(OpCodes.Ldarg_0);
			ilGen.Emit(OpCodes.Ldarga_S, (byte)1);
			ilGen.Emit(OpCodes.Ldarga_S, (byte)2);




			ilGen.Emit(OpCodes.Ret);

			_deserializeDispatcher2 = (DeserializeDelegate<BaseObject>)dynMethod.CreateDelegate(typeof(DeserializeDelegate<BaseObject>));
		}

		void CreateExpressionDispatcher()
		{
			var bufferArg = Parameter(typeof(byte[]), "buffer");
			var offsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var valueArg = Parameter(typeof(BaseObject).MakeByRefType(), "value");

			List<Expression> body = new List<Expression>();

			var method = _derivedFormatter.GetType().GetMethod("Deserialize");

			var castedArg = Convert(CastRef(valueArg, typeof(BaseObject), typeof(DerivedObject)), typeof(DerivedObject).MakeByRefType());
			body.Add(Call(Constant(_derivedFormatter), method, bufferArg, offsetArg, castedArg));

			_deserializeDispatcher1 = Lambda<DeserializeDelegate<BaseObject>>(Block(body), bufferArg, offsetArg, valueArg).Compile();
		}

		Expression CastRef(ParameterExpression valueArg, Type from, Type to)
		{
			var unsafeAs = typeof(Unsafe).GetMethods()
				.First(m => m.Name == "As" && m.GetGenericArguments().Length == 2)
				.MakeGenericMethod(from, to);

			return Call(unsafeAs, valueArg);
		}

		public void Serialize(ref byte[] buffer, ref int offset, BaseObject value)
		{
		}

		public void Deserialize(byte[] buffer, ref int offset, ref BaseObject value)
		{
			_deserializeDispatcher2(buffer, ref offset, ref value);
		}
	}

	class DerivedFormatter : IFormatter<DerivedObject>
	{
		public void Serialize(ref byte[] buffer, ref int offset, DerivedObject value)
		{
		}

		public void Deserialize(byte[] buffer, ref int offset, ref DerivedObject value)
		{
			value = new DerivedObject();
		}
	}


	class BaseObject
	{
		public int BaseInt;
	}

	class DerivedObject : BaseObject
	{
		public int DerivedInt;
	}
}
