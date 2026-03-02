using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Generated;

namespace Ceras.Test {
	[CerasSerializable]
	public interface ICombatEntity {
		int Id { get; set; }
	}

	[CerasSerializable]
	public class CombatEntity : ICombatEntity {
		public int Id { get; set; }
		public string Name { get; set; }
	}

	[CerasSerializable]
	public class MapChunk {
		public List<ICombatEntity> Entities { get; set; }
	}

	public class Pair<T1, T2> {
		public T1 Item1 { get; set; }
		public T2 Item2 { get; set; }
	}

	public class DummyForMismatch {}

	public class RobustGenericTest {
		[Fact]
		public void TestRobustGenericList() {
			var robust = new CerasRobustSerializer(config => config.UseGeneratedFormatters());

			var chunk = new MapChunk {
				Entities = new List<ICombatEntity>
				{
					new CombatEntity { Id = 1, Name = "Orc" },
					new CombatEntity { Id = 2, Name = "Goblin" }
				}
			};

			uint version = 1;
			var bytes = robust.Save(chunk, version);
			var loadedChunk = robust.Load<MapChunk>(bytes, version);

			Assert.NotNull(loadedChunk);
			Assert.NotNull(loadedChunk.Entities);
			Assert.Equal(2, loadedChunk.Entities.Count);
			Assert.Equal(1, loadedChunk.Entities[0].Id);
			Assert.Equal("Orc", ((CombatEntity)loadedChunk.Entities[0]).Name);
			Assert.Equal(2, loadedChunk.Entities[1].Id);
			Assert.Equal("Goblin", ((CombatEntity)loadedChunk.Entities[1]).Name);
		}

		[Fact]
		public void TestRobustGenericListTraversal() {
			var robust = new CerasRobustSerializer(config =>
			{
				config.UseGeneratedFormatters();
				// Add a dummy to the registry to include it in the fingerprint (F1).
				config.SchemaRegistry.Register(typeof(DummyForMismatch), null);
			});

			var chunk = new MapChunk {
				Entities = new List<ICombatEntity>
				{
					new CombatEntity { Id = 1, Name = "Orc" },
				}
			};

			uint version = 1;
			var bytes = robust.Save(chunk, version);

			// Force fingerprint mismatch: clear registry and re-populate WITHOUT DummyForMismatch.
			robust.Config.SchemaRegistry.InternalClear();
			robust.Config.UseGeneratedFormatters();
			// We DO NOT register DummyForMismatch here, so the fingerprint (F2) will differ from F1.

			var loadedChunk = robust.Load<MapChunk>(bytes, version);

			Assert.NotNull(loadedChunk);
			Assert.Single(loadedChunk.Entities);
			Assert.Equal(1, loadedChunk.Entities[0].Id);
			Assert.Equal("Orc", ((CombatEntity)loadedChunk.Entities[0]).Name);
		}

		[Fact]
		public void TestRobustGenericPair() {
			var robust = new CerasRobustSerializer(config =>
			{
				config.UseGeneratedFormatters();
				// Fingerprint F1 (includes DummyForMismatch)
				config.SchemaRegistry.Register(typeof(DummyForMismatch), null);
			});

			var pair = new Pair<int, string> { Item1 = 42, Item2 = "Hello" };

			uint version = 1;
			var bytes = robust.Save(pair, version);

			// 1. Normal roundtrip (Fast path)
			var loadedPair = robust.Load<Pair<int, string>>(bytes, version);
			Assert.Equal(42, loadedPair.Item1);
			Assert.Equal("Hello", loadedPair.Item2);

			// 2. Traversal roundtrip (Fingerprint mismatch)
			robust.Config.SchemaRegistry.InternalClear();
			robust.Config.UseGeneratedFormatters();
			// We DO NOT register DummyForMismatch here, so the fingerprint will mismatch.
			var loadedPair2 = robust.Load<Pair<int, string>>(bytes, version);
			Assert.Equal(42, loadedPair2.Item1);
			Assert.Equal("Hello", loadedPair2.Item2);
		}
	}
}
