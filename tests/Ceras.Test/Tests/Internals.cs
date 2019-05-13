using Xunit;
// ReSharper disable InconsistentNaming

namespace Ceras.Test
{
	using Helpers;
	using System;
	using System.Collections.Generic;

	public class Internals : TestBase
	{
		[Fact]
		public void MethodResolving()
		{
			// Check if the method resolver works correctly
			IAnimal animal = null;
			ICat cat = null;
			IDog dog = null;


			// - Non-generic MethodInfo, ConstructorInfo
			{
				var ctor = GetCtor(() => new Internals());
				TestDeepEquality(ctor);

				var mAA = GetMethod(() => HandleAnimal(animal, animal));
				TestDeepEquality(mAA);

				var mCA = GetMethod(() => HandleAnimal(cat, animal));
				TestDeepEquality(mCA);

				var mAC = GetMethod(() => HandleAnimal(animal, cat));
				TestDeepEquality(mAC);

				var mCC = GetMethod(() => HandleAnimal(cat, cat));
				TestDeepEquality(mCC);
			}


			// - Simple closed generic
			{
				var mt = GetMethod(() => HandleAnimal(dog, dog));
				TestDeepEquality(mt);
			}

			// - Exception on open method
			{
				var open = GetMethod(() => HandleAnimal(dog, dog)).GetGenericMethodDefinition();
				try
				{
					Clone(open);
					throw new Exception("expected exception not thrown");
				}
				catch
				{
				}
			}
		}


		void HandleAnimal(IAnimal anyA, IAnimal anyB) { }
		void HandleAnimal(ICat cat, IAnimal any) { }
		void HandleAnimal(IAnimal any, ICat cat) { }
		void HandleAnimal(ICat cat1, ICat cat2) { }
		void HandleAnimal<T>(T obj1, T obj2) where T : IDog { }

		interface IAnimal { }
		interface ICat : IAnimal { }
		interface IDog : IAnimal { }
		class Cat : ICat { }
		class Dog : IDog { }


		[Fact]
		public void ClearGenericCaches()
		{
			var ceras =  new CerasSerializer();

			var list = new List<Cat>();
			for (int i = 0; i < 1000; i++)
				list.Add(new Cat());

			var data = ceras.Serialize(list);
			var clone = ceras.Deserialize<List<Cat>>(data);
			
			var capacityBefore = ObjectCache.RefProxyPool<Cat>.GetPoolCapacity();
			Assert.True(capacityBefore > 500);
			
			CerasSerializer.ClearGenericCaches();
			
			var capacityAfter = ObjectCache.RefProxyPool<Cat>.GetPoolCapacity();
			Assert.True(capacityAfter < capacityBefore);
		}

		[Fact]
		public void IsBlittableChecks()
		{
			Assert.True(ReflectionHelper.IsBlittableType(typeof(bool)));
			Assert.True(ReflectionHelper.IsBlittableType(typeof(double)));
			Assert.True(ReflectionHelper.IsBlittableType(typeof(double)));
			Assert.True(ReflectionHelper.IsBlittableType(typeof(DayOfWeek))); // actual enum

			Assert.False(ReflectionHelper.IsBlittableType(typeof(string)));
			Assert.False(ReflectionHelper.IsBlittableType(typeof(Enum))); // enum class itself
			Assert.False(ReflectionHelper.IsBlittableType(typeof(byte*)));
			Assert.False(ReflectionHelper.IsBlittableType(typeof(int*)));
			Assert.False(ReflectionHelper.IsBlittableType(typeof(IntPtr)));
			Assert.False(ReflectionHelper.IsBlittableType(typeof(UIntPtr)));
		}

		[Fact]
		public void TypeMetaData()
		{
			// We need to ensure the type meta-data is correct.

			var ceras = new CerasSerializer();

			// true and false keywords are both highlighted in the same color, so this makes it easier to see :P
			const bool False = false;

			// isPrimitive means "is a serialization primitive", not primitive as in "primitive type" like int or something.
			var tests = new List<(bool isFramework, bool isPrimitive, bool isBlittable, Type testType)>();

			tests.Add((isFramework: true, isPrimitive: true, isBlittable: true, typeof(int)));
			tests.Add((isFramework: true, isPrimitive: true, isBlittable: true, typeof(bool)));
			tests.Add((isFramework: true, isPrimitive: true, isBlittable: true, typeof(char)));

			tests.Add((isFramework: true, isPrimitive: true, isBlittable: False, typeof(Type)));
			tests.Add((isFramework: true, isPrimitive: true, isBlittable: False, typeof(Type).GetType()));

			tests.Add((isFramework: true, isPrimitive: False, isBlittable: False, typeof(List<int>)));


			foreach (var test in tests)
			{
				var meta = ceras.GetTypeMetaData(test.testType);

				Assert.True(meta.IsFrameworkType == test.isFramework);
				Assert.True(meta.IsPrimitive == test.isPrimitive);
				Assert.True(ReflectionHelper.IsBlittableType(test.testType) == test.isBlittable);
			}
		}

		[Fact]
		public void FastCopy()
		{
#if NET45 || NET451 || NET452
			global::System.Console.WriteLine("Testing FastCopy on NET45.x");
#elif NET47 || NET471 || NET472
			global::System.Console.WriteLine("Testing FastCopy on NET47.x");
#elif NETSTANDARD2_0 || NETCOREAPP2_0 || NETCOREAPP2_1 || NETCOREAPP2_2
			global::System.Console.WriteLine("Testing FastCopy on NET STANDARD 2.0 / NETCOREAPP2_x");
#else

#error Unknown compiler version

#endif


			var sizes = new int[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 100, 200, 300, 400, 510, 511, 512, 513, 514, 1000, 5000, 10 * 1000, 100 * 1000 };

			List<byte[]> sourceArrays = new List<byte[]>();
			foreach (var s in sizes)
			{
				var ar = new byte[s];
				rng.NextBytes(ar);
				sourceArrays.Add(ar);
			}

			List<byte[]> targetArrays = new List<byte[]>();
			foreach (var s in sizes)
			{
				var ar = new byte[s];
				rng.NextBytes(ar);
				targetArrays.Add(ar);
			}


			for (int arIndex = 0; arIndex < sourceArrays.Count; arIndex++)
			{
				var size = sizes[arIndex];
				var source = sourceArrays[arIndex];
				var target = targetArrays[arIndex];

				SerializerBinary.FastCopy(source, 0, target, 0, size);

				Assert.True(size == source.Length);
				Assert.True(size == target.Length);
				for (int i = 0; i < size; i++)
					Assert.True(source[i] == target[i]);
			}
		}

		[Fact]
		public void SchemaClonesAreEqual()
		{
			// Clone schema, check if equal

			var ceras = new CerasSerializer();
			var meta = ceras.GetTypeMetaData(typeof(Element));

			var schema = meta.PrimarySchema;

			byte[] buffer = new byte[100];
			int offset = 0;
			CerasSerializer.WriteSchema(ref buffer, ref offset, schema);

			offset = 0;
			var clone = ceras.ReadSchema(buffer, ref offset, typeof(Element), false);

			Assert.True(Equals(schema, clone));


			// Check if List<Schema>.IndexOf() works correctly
			List<Schema> list = new List<Schema>();
			list.Add(schema);

			int index = list.IndexOf(clone);

			Assert.True(index == 0);
		}

		[Fact]
		public void ShouldClearEncounteredSchemata()
		{
			var plugin = new Plugin();
			var plugin2 = new Plugin();

			var serializerConfig = new SerializerConfig();
			serializerConfig.VersionTolerance.Mode = VersionToleranceMode.Standard;
			var s = new CerasSerializer(serializerConfig);

			for (int i = 0; i < 2; i++)
			{
				Assert.True(s.InstanceData.EncounteredSchemaTypes.Count == 0);
				var data = s.Serialize(plugin);
				Assert.True(s.InstanceData.EncounteredSchemaTypes.Count == 0);
				var data2 = s.Serialize(plugin2);
				Assert.True(s.InstanceData.EncounteredSchemaTypes.Count == 0);

				for (int j = 0; j < 3; j++)
				{
					var p1 = s.Deserialize<Plugin>(data);
					Assert.True(s.InstanceData.EncounteredSchemaTypes.Count == 0);
					var p2 = s.Deserialize<Plugin>(data2);
					Assert.True(s.InstanceData.EncounteredSchemaTypes.Count == 0);

					Assert.True(p1?.PluginLocation?.PluginName != null);
					Assert.True(p1.PluginLocation.PluginName == plugin.PluginLocation.PluginName);

					Assert.True(p2?.PluginLocation?.PluginName != null);
					Assert.True(p2.PluginLocation.PluginName == plugin2.PluginLocation.PluginName);
				}
			}
		}

		[Fact]
		public void ShouldThrowOnExtendedVersionTolerance()
		{
			Assert.ThrowsAny<NotSupportedException>(() =>
			{
				SerializerConfig s = new SerializerConfig();
				s.VersionTolerance.Mode = VersionToleranceMode.Extended;
				var c = new CerasSerializer(s);
			});
		}

		[Fact]
		public void ReadSchemaCanFindAllMembers()
		{
			var sc = new SerializerConfig();
			sc.DefaultTargets = TargetMember.AllFields;
			sc.VersionTolerance.Mode = VersionToleranceMode.Standard;
    
			var ceras = new CerasSerializer(sc);
			TestCls tc = new TestCls()
			{
				Field1 = "baseF" + Environment.TickCount,
				PrivateText1 = "baseP" + Environment.TickCount,
				Field2 = "derivedF" + Environment.TickCount,
				PrivateText2 = "derivedP" + Environment.TickCount,
			};
			TestCls tcClone = ceras.Deserialize<TestCls>(ceras.Serialize(tc));
			
			Assert.True(tc.Field1 == tcClone.Field1);
			Assert.True(tc.PrivateText1 == tcClone.PrivateText1);
			Assert.True(tc.Field2 == tcClone.Field2);
			Assert.True(tc.PrivateText2 == tcClone.PrivateText2);
		}

		public abstract class BaseCls
		{
			public string Field1;

			string _privateText1;
			public string PrivateText1
			{
				get => _privateText1;
				set => _privateText1 = value;
			}
		}

		public class TestCls : BaseCls
		{
			public string Field2;

			string _privateText2;
			public string PrivateText2
			{
				get => _privateText2;
				set => _privateText2 = value;
			}
		}



		public class PluginLocation
		{
			static int _c = 5;
			public string PluginName { get; set; } = (++_c).ToString();
		}

		public class Plugin
		{
			public PluginLocation PluginLocation { get; set; } = new PluginLocation();
		}
	}



	// todo: test public default ctor, private default ctor, and no parameterless ctor (and all construction modes)

	// todo: ignoreField, Caching, KeyValuePairs, Dictionaries, Typing, interfaces,
	// todo: RootObjects, reusing (overwriting) objects, arrays
	// todo: known types, hash checks for known types, assured mismatch when another type is added

}
