using System;
using System.Collections.Generic;

namespace Ceras.Test
{
	using Formatters;
	using Xunit;

	public class TypeConfig : TestBase
	{
		[Fact]
		public void CanGenerateDebugReports()
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
		public void CanNotConfigurePrimitives()
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

			bool configGotCalled = false;

			config.OnConfigNewType = t =>
			{
				if (primitiveTypes.Contains(t.Type))
					configGotCalled = true;
			};

			// Configuring primitives should not be possible
			foreach (var t in primitiveTypes)
				ExpectException(() => config.ConfigType(t));

			// Enum is not a real type (it's an abstract base class)
			ExpectException(() => config.ConfigType(typeof(Enum)));


			if (configGotCalled)
				throw new Exception("on config new type should not be called for 'serialization primitives'");
		}

		[Fact]
		public void CustomFormatterForEnum()
		{
			var config = new SerializerConfig();
			config.ConfigType<DayOfWeek>().CustomFormatter = new DayOfWeekFormatter();

			var ceras = new CerasSerializer(config);
			
			Assert.True(ceras.Deserialize<DayOfWeek>(ceras.Serialize(DayOfWeek.Sunday)) == DayOfWeek.Sunday);
			Assert.True(ceras.Deserialize<DayOfWeek>(ceras.Serialize(DayOfWeek.Monday)) == DayOfWeek.Monday);
			Assert.True(ceras.Deserialize<DayOfWeek>(ceras.Serialize(DayOfWeek.Saturday)) == DayOfWeek.Saturday);
			Assert.True(ceras.Deserialize<DayOfWeek>(ceras.Serialize((DayOfWeek)591835)) == (DayOfWeek)591835);

		}

		[Fact]
		public void CtorTest()
		{
			var obj = new ConstructorTest(5);
			var ceras = new CerasSerializer();

			bool success = false;
			try
			{
				// Expected to throw: no default ctor
				var data = ceras.Serialize(obj);
				var clone = ceras.Deserialize<ConstructorTest>(data);
			}
			catch (Exception e)
			{
				success = true;
			}
			
			Assert.True(success, "objects with no ctor and no TypeConfig should not serialize");
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

	class DayOfWeekFormatter : IFormatter<DayOfWeek>
	{
		public void Serialize(ref byte[] buffer, ref int offset, DayOfWeek value)
		{
			SerializerBinary.WriteUInt64(ref buffer, ref offset, (ulong)value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref DayOfWeek value)
		{
			value = (DayOfWeek)SerializerBinary.ReadUInt64(buffer, ref offset);
		}
	}

	class ErrorFormatter<T> : IFormatter<T>
	{
		public void Serialize(ref byte[] buffer, ref int offset, T value) => Throw();
		public void Deserialize(byte[] buffer, ref int offset, ref T value) => Throw();

		void Throw() => throw new InvalidOperationException("This shouldn't happen");
	}

	class ConstructorTest
	{
		public int x;

		public ConstructorTest(int x)
		{
			this.x = x;
		}
	}

}
