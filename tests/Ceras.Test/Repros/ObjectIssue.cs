using System;
using System.Collections.Generic;
using System.Linq;
using Ceras;
using Ceras.Test;
using Xunit;

public class ObjectIssueTest : TestBase {
	[Fact]
	public void ReproTest() {
		var g = new ReproTest.Game();

		var a = new ReproTest.Entity();
		var b = new ReproTest.Entity();
		// g.ar.Add(a);
		// g.ar.Add(b);

		// g.someDictionary1[a] = "a";
		// g.someDictionary2["b"] = b;

		g.LogisticsManager.Add(a, b);

		var text = "abc123";
		g.Obj = new string(text);
		g.Str = new string(text);

		g.someDict.Add("key", new List<ReproTest.Connection>() { new ReproTest.Connection() { Source = text } });

		var config = new SerializerConfig();
		config.Robust.Enabled = true;
		CerasRobust.Configure(config);
		Ceras.Generated.GeneratedFormatters.UseFormatters(config);
		var ceras = new CerasSerializer(config);
		var data = CerasRobust.Save(g, 1, ceras);
		var clone = CerasRobust.Load<ReproTest.Game>(data, 1, ceras);

		Assert.Equal(text, g.Str); // This works, even though "_str" is private
		Assert.Equal(text, clone.Obj); // fails when obj is private and "all fields" is set
		Assert.Equal(g.someDict.Count, clone.someDict.Count);
		Assert.Equal(g.LogisticsManager.GetNumberOfEntries(), clone.LogisticsManager.GetNumberOfEntries());
		Assert.True(DeepComparer.Instance.CheckEquality(g, clone), "objects are different");
	}
}
namespace ReproTest {
	[CerasSerializable]
	[MemberConfig(TargetMembers = TargetMember.AllFields)]
	partial class Game {
		private object _obj;

		private string _str;

		[Exclude]
		public object Obj { get => _obj; set => _obj = value; }
		[Exclude]
		public string Str { get => _str; set => _str = value; }

		public List<object> List { get; } = new List<object>();
		public LogisticsManager LogisticsManager { get; } = new();

		public readonly Dictionary<object, List<Connection>> someDict = new();

		public override bool Equals(object obj) {
			if (obj is Game other) {
				return Equals(_obj, other._obj) &&
					   Equals(_str, other._str) &&
					   someDict.Count == other.someDict.Count &&
					   LogisticsManager.Equals(other.LogisticsManager);
			}
			return false;
		}
		public override int GetHashCode() => 0;
	}

	[CerasSerializable]
	[MemberConfig(TargetMembers = TargetMember.AllFields)]
	interface IEntity {
		int GetId();
	}

	public struct Connection {
		public object Source; // IOutput, BeltSegment, or IPowerNode
	}

	[MemberConfig(TargetMembers = TargetMember.AllFields)]
	partial class LogisticsManager {
		// making this public fixes the issue, but why?
		private readonly Dictionary<object, List<Connection>> _entityConnections = new();

		public void Add(object key, object source) {
			var list = new List<Connection>();
			list.Add(new Connection { Source = source });
			_entityConnections.Add(key, list);
		}

		public int GetNumberOfEntries() {
			return _entityConnections.Count;
		}

		public override bool Equals(object obj) {
			if (obj is LogisticsManager other) {
				if (_entityConnections.Count != other._entityConnections.Count) return false;
				foreach (var kvp in _entityConnections) {
					if (!other._entityConnections.TryGetValue(kvp.Key, out var otherList)) return false;
					if (kvp.Value.Count != otherList.Count) return false;
					for (int i = 0; i < kvp.Value.Count; i++) {
						if (!Equals(kvp.Value[i].Source, otherList[i].Source)) return false;
					}
				}
				return true;
			}
			return false;
		}
		public override int GetHashCode() => 0;
	}

	class Entity : IEntity {
		static int nextId = 1;

		public int id;

		public int GetId() => id;

		public Entity() {
			id = nextId++;
		}

		public override bool Equals(object obj) => obj is Entity other && id == other.id;
		public override int GetHashCode() => id;
	}
}
