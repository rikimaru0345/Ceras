using System;
using System.Collections.Generic;
using System.Linq;

namespace Ceras.Test
{
	using Xunit;

	public class Misc : TestBase
	{
		[Fact]
		public void DictInObjArrayTest()
		{
			var dict = new Dictionary<string, object>
			{
				["test"] = new Dictionary<string, object>
				{
					["test"] = new object[]
					{
						new Dictionary<string, object>
						{
							["test"] = 3
						}
					}
				}
			};


			var s = new CerasSerializer();

			var data = s.Serialize(dict);

			var cloneDict = s.Deserialize<Dictionary<string, object>>(data);

			var inner1 = cloneDict["test"] as Dictionary<string, object>;
			Assert.True(inner1 != null);

			var objArray = inner1["test"] as object[];
			Assert.True(objArray != null);

			var dictElement = objArray[0] as Dictionary<string, object>;
			Assert.True(dictElement != null);

			var three = dictElement["test"];

			Assert.True(three.GetType() == typeof(int));
			Assert.True(3.Equals(three));
		}

		[Fact]
		public void SerializeStatic()
		{
			var config = new SerializerConfig();
			config.ConfigStaticType(typeof(StaticClassTest))
				  .Members.First(m => m.PersistentName == "NotIncluded3")
				  .SerializationOverride = SerializationOverride.ForceSkip;

			var ceras = new CerasSerializer(config);

			var dataDefault = ceras.Advanced.SerializeStatic(typeof(StaticClassTest));

			StaticClassTest.ValueField = 1;
			StaticClassTest.StringField = "2";
			StaticClassTest.FloatProp = 3;
			StaticClassTest.ListProp = new List<int> { 4, 4, 4, 4};
			StaticClassTest.NotIncluded1 = 5;
			StaticClassTest.NotIncluded2 = 6;
			StaticClassTest.NotIncluded3 = 7;

			var dataChanged = ceras.Advanced.SerializeStatic(typeof(StaticClassTest));




			//
			// Check if the "reset" works
			//
			ceras.Advanced.DeserializeStatic(typeof(StaticClassTest), dataDefault);

			Assert.True(StaticClassTest.ValueField == -3);
			Assert.True(StaticClassTest.StringField == "-3");
			Assert.True(StaticClassTest.FloatProp == -3);
			Assert.True(StaticClassTest.ListProp.Count == 1 && StaticClassTest.ListProp[0] == -3);
			// Those should still be like we last changed them 
			Assert.True(StaticClassTest.NotIncluded1 == 5);
			Assert.True(StaticClassTest.NotIncluded2 == 6);
			Assert.True(StaticClassTest.NotIncluded3 == 7);
			
			//
			// Now we restore the changed state
			//
			ceras.Advanced.DeserializeStatic(typeof(StaticClassTest), dataChanged);

			Assert.True(StaticClassTest.ValueField == 1);
			Assert.True(StaticClassTest.StringField == "2");
			Assert.True(StaticClassTest.FloatProp == 3);
			Assert.True(StaticClassTest.ListProp.Count == 4 && StaticClassTest.ListProp.All(x => x == 4));
			// Those should still be like we last changed them 
			Assert.True(StaticClassTest.NotIncluded1 == 5);
			Assert.True(StaticClassTest.NotIncluded2 == 6);
			Assert.True(StaticClassTest.NotIncluded3 == 7);


		}
	
		[Fact]
		public void SerializeStaticPart()
		{
			var ceras = new CerasSerializer();

			var obj = new StaticMembersTest();


			var dataStaticDefault = ceras.Advanced.SerializeStatic(typeof(StaticMembersTest));
			var dataInstanceDefault = ceras.Serialize(obj);
			
			Assert.True(StaticMembersTest.ValueField == -12);
			Assert.True(obj.InstanceValue == -5);

			obj.InstanceValue = 1;
			StaticMembersTest.ValueField = 2;

			var dataStaticChanged = ceras.Advanced.SerializeStatic(typeof(StaticMembersTest));
			var dataInstanceChanged = ceras.Serialize(obj);

			Assert.True(StaticMembersTest.ValueField == 2);
			Assert.True(obj.InstanceValue == 1);
			
			//
			// Deserialize
			//

			ceras.Advanced.DeserializeStatic(typeof(StaticMembersTest), dataStaticDefault);
			ceras.Deserialize(ref obj, dataInstanceDefault);
			
			Assert.True(StaticMembersTest.ValueField == -12);
			Assert.True(obj.InstanceValue == -5);
			


			ceras.Advanced.DeserializeStatic(typeof(StaticMembersTest), dataStaticChanged);
			ceras.Deserialize(ref obj, dataInstanceChanged);
			
			Assert.True(StaticMembersTest.ValueField == 2);
			Assert.True(obj.InstanceValue == 1);
		}
	}

	static class StaticClassTest
	{
		public static int ValueField = -3;
		public static string StringField = "-3";

		[Exclude]
		public static int NotIncluded1 = -3;
		[NonSerialized]
		public static int NotIncluded2 = -3;
		public static int NotIncluded3 = -3;

		public static float FloatProp { get; set; } = -3;
		public static List<int> ListProp { get; set; } = new List<int> { -3 };
	}

	class StaticMembersTest
	{
		public static int ValueField = -12;
		public int InstanceValue = -5;
	}
}
