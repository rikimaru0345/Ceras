using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

	}
}
