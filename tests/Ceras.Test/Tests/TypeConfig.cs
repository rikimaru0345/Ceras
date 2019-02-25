using System;
using System.Collections.Generic;

namespace Ceras.Test
{
	using Formatters;
	using Xunit;

	public class TypeConfig : TestBase
	{
		[Fact]
		public void Report()
		{
			var ceras = new CerasSerializer();

			var reportBool = ceras.GenerateSerializationDebugReport(typeof(bool));
			var reportType = ceras.GenerateSerializationDebugReport(typeof(Type));
			var reportString = ceras.GenerateSerializationDebugReport(typeof(string));

			var reportList = ceras.GenerateSerializationDebugReport(typeof(List<int>));
			var reportTuple = ceras.GenerateSerializationDebugReport(typeof(Tuple<string, int>));
			var reportValueTuple = ceras.GenerateSerializationDebugReport(typeof(ValueTuple<string, int>));
			var reportPerson = ceras.GenerateSerializationDebugReport(typeof(Issue25.Adult));

		}

		[Fact]
		public void PreventNonsense()
		{
			// Changing any settings for "Serialization Primitives" should not be allowed
			// String, Type, int, ...

			SerializerConfig config = new SerializerConfig();

			HashSet<Type> primitiveTypes = new HashSet<Type>
			{
				typeof(Type),
				typeof(byte),
				typeof(int),
				typeof(float),
				typeof(string),
			};

			config.OnConfigNewType = t =>
			{
				if (primitiveTypes.Contains(t.Type))
					throw new Exception("on config new type should not be called for 'serialization primitives'");
			};

			foreach (var t in primitiveTypes)
				ExpectException(() => config.ConfigType(t));


		}

		static void ExpectException(Action f)
		{
			try
			{
				f();
				throw new InvalidOperationException("The given method should have thrown an exception!");
			}
			catch
			{
			}
		}
	}

	class ErrorFormatter<T> : IFormatter<T>
	{
		public void Serialize(ref byte[] buffer, ref int offset, T value) => Throw();
		public void Deserialize(byte[] buffer, ref int offset, ref T value) => Throw();

		void Throw() => throw new InvalidOperationException("This shouldn't happen");
	}
}
