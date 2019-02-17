using System.Collections;
using System.Collections.Generic;
using Xunit;

namespace Ceras.Test
{
	// Private setter in base class
	public class Issue25
	{
		public abstract class Person
		{
			public string Name = "default name";
			public List<Person> Friends { get; private set; } = new List<Person>();

			protected Person()
			{
				Friends = new List<Person>();
			}
			
			protected void SetFriendsToNullInternal()
			{
				Friends = null;
			}
		}

		public class Adult : Person
		{
			public byte[] Serialize() => new CerasSerializer().Serialize<object>(this);

			internal void SetFriendsToNull()
			{
				SetFriendsToNullInternal();
			}
		}
		

		[Fact]
		public void Test()
		{
			var p = new Adult();
			p.Name = "1";
			p.Friends.Add(new Adult { Name = "2" });

			var config = new SerializerConfig();
			config.DefaultTargets = TargetMember.AllPublic;
			var ceras = new CerasSerializer(config);

			var data = ceras.Serialize<object>(p);

			var clone = new Adult();
			clone.SetFriendsToNull();
			object refObj = clone;
			ceras.Deserialize<object>(ref refObj, data);

			Assert.True(refObj != null);
			clone = refObj as Adult;
			Assert.True(clone.Friends != null);
			Assert.True(clone.Friends.Count == 1);
			Assert.True(clone.Friends[0].Name == "2");

		}

	}
}

