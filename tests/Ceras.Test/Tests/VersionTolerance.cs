using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Ceras.Test
{

	namespace Version1
	{
		class Person
		{
			public string Name;
			public int Age;
		}
	}

	namespace Version2
	{
		class Person
		{
			public string Name;
			public string Surname;
			public int Age;
		}
	}


	class VersionTest
	{
		public string Name;
		public int Number;
		public VersionTest Next;
	}

	public class VersionTolerance : TestBase
	{
		[Fact]
		public void EmulatorCanReadSchemaData()
		{
			var config = new SerializerConfig();
			config.VersionTolerance.Mode = VersionToleranceMode.Standard;
			CerasSerializer ceras = new CerasSerializer(config);

			var obj = new VersionTest();
			obj.Name = "abc";
			obj.Number = 123;
			obj.Next = null;

			byte[] buffer = new byte[100];
			ceras.Serialize(obj, ref buffer);

			ReadData(buffer);
		}

		void ReadData(byte[] buffer)
		{
			var config = new SerializerConfig();
			config.VersionTolerance.Mode = VersionToleranceMode.Standard;

			config.ConfigType<VersionTest>().CustomResolver = (c, t)
				=> new Ceras.Versioning.DynamicEmulator<VersionTest>(c, c.GetTypeMetaData(t).PrimarySchema);

			CerasSerializer ceras = new CerasSerializer(config);

			var clone = ceras.Deserialize<VersionTest>(buffer);

		}

		[Fact]
		public void DerivedProperties()
		{
			var config = new SerializerConfig();
			config.VersionTolerance.Mode = VersionToleranceMode.Standard;
			CerasSerializer ceras = new CerasSerializer(config);

			var obj = new DerivedClass();
			obj.Name = "derived!";

			var data = ceras.Serialize(obj);
			var clone = ceras.Deserialize<DerivedClass>(data);

			Assert.True(clone.Name == obj.Name);

			Assert.True(config.ConfigType<DerivedClass>().Members.Count(m => m.Member is PropertyInfo) == 1);
		}

		[Fact]
		public void AutomaticSchemaChanges()
		{
			SerializerConfig CreateConfig()
			{
				var config = new SerializerConfig();
				config.VersionTolerance.Mode = VersionToleranceMode.Standard;
				var typeMap = new MappingTypeBinder();
				config.Advanced.TypeBinder = typeMap;

				typeMap.Map(typeof(Version1.Person), "Person");
				typeMap.Map(typeof(Version2.Person), "Person");
				typeMap.Map("Person", typeof(Version2.Person));
				return config;
			}

			byte[] data1 = null;
			byte[] data2 = null;

			{ // 1: Save old data
				CerasSerializer ceras = new CerasSerializer(CreateConfig());

				Version1.Person p1 = new Version1.Person { Name = "A", Age = 1 };
				data1 = ceras.Serialize(p1);
			}

			{ // 2: Use new type (added member), load old data, save again
				CerasSerializer ceras = new CerasSerializer(CreateConfig());
				Version2.Person p2 = null;
				ceras.Deserialize(ref p2, data1);

				// Check if everything was loaded correctly
				Assert.True(p2.Name == "A");
				Assert.True(p2.Age == 1);
				Assert.True(p2.Surname == null);

				// Make use of the new member, and save it
				p2.Surname = "S";
				data2 = ceras.Serialize(p2);

				// Load new data, then old again
				var p2Clone = ceras.Deserialize<Version2.Person>(data2);
				var p1Clone = ceras.Deserialize<Version2.Person>(data1);

				Assert.True(p2Clone.Surname == "S");
				Assert.True(p1Clone.Surname == null);
			}
		}
	}

	class DisplayAttribute : Attribute
	{
		public string Name;
	}

	public class BaseClass
	{
		[Display(Name = "Bla bla bla")]
		public virtual string Name { get; set; }
	}

	public class DerivedClass : BaseClass
	{
		[Display(Name = "Cla Cla Cla")]
		public override string Name { get; set; }
	}



	class MappingTypeBinder : ITypeBinder
	{
		Dictionary<Type, string> _typeToName = new Dictionary<Type, string>();
		Dictionary<string, Type> _nameToType = new Dictionary<string, Type>();

		public void Map(string name, Type type)
		{
			_nameToType.Add(name, type);
		}

		public void Map(Type type, string name)
		{
			_typeToName.Add(type, name);
		}


		public string GetBaseName(Type type)
		{
			return _typeToName[type];
		}

		public Type GetTypeFromBase(string baseTypeName)
		{
			return _nameToType[baseTypeName];
		}

		public Type GetTypeFromBaseAndArguments(string baseTypeName, params Type[] genericTypeArguments)
		{
			throw new NotSupportedException("this binder is only for debugging");
		}
	}

}
