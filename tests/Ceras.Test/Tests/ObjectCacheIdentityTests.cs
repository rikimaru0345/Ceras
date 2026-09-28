// The object reference cache decides which *occurrences* of a value come back as one object, and
// it is keyed on reference identity. That is not an implementation preference: an object-cache id in
// the payload is a *position* in the reader's deserialization cache, so an id can only ever name one
// instance. Keying on the default object comparer instead (EqualityComparer<object>.Default, which
// calls Equals) made any two instances of a type that overrides equality share an id, so the second
// occurrence was written as a back-reference to the first and came back as *that* object. The save
// loaded without complaint and described a different graph than the one that was written.
//
// What is pinned here, on both wire formats (raw and robust-generated):
//
//   * two instances that are Equals but not ReferenceEquals stay two objects;
//   * one instance in two slots stays one object (sharing is not collateral damage of the fix);
//   * a type whose Equals/GetHashCode are hostile - everything equal, everything hashing to 0, a
//     hash that changes on every call - cannot influence the cache at all;
//   * the explicit ObjectCacheComparer opt-in still collapses by value, because that is the user's
//     call to make, and a save written that way still loads.
//
// Payload bytes are asserted only where they carry the property (the raw format, where a reference
// slot is one marker byte plus a payload). Nothing about the byte grammar changed: the markers and
// the bias are still Format/Protocol.cs, ids are still dense encounter-order indices, and the change
// is entirely on the writer side - which slot is one byte and which is a full value. The reader only
// ever allocates a proxy when it reads a new-value marker, in stream order, so it is a pure function
// of the bytes and unaffected by which comparer wrote them.
using System;
using System.Collections.Generic;
using Ceras;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test.ObjectCacheIdentity
{
	/// <summary>
	/// Two nodes with the same <see cref="Id"/> are <c>Equals</c> and are different instances. The
	/// <c>Tag</c> is what the payload test counts: it is a value, so it is written once per
	/// occurrence, which is the property under test seen from the bytes.
	/// </summary>
	[CerasContext("Tests")]
	[CerasSerializable]
	public class EqualNode
	{
		public int Id;
		public string Tag;

		public override bool Equals(object obj) => obj is EqualNode other && other.Id == Id;
		public override int GetHashCode() => Id;
		public override string ToString() => $"EqualNode({Id}, '{Tag}')";
	}

	[CerasContext("Tests")]
	[CerasSerializable]
	public class NodeHolder
	{
		public EqualNode First;
		public EqualNode Second;
	}

	/// <summary>
	/// The worst case for a value-keyed cache: every instance is equal to every other instance and
	/// every instance lands in the same bucket.
	/// </summary>
	[CerasContext("Tests")]
	[CerasSerializable]
	public class AllEqualNode
	{
		public string Tag;

		public override bool Equals(object obj) => obj is AllEqualNode;
		public override int GetHashCode() => 0;
		public override string ToString() => $"AllEqualNode('{Tag}')";
	}

	[CerasContext("Tests")]
	[CerasSerializable]
	public class AllEqualHolder
	{
		public AllEqualNode First;
		public AllEqualNode Second;
		public AllEqualNode Third;
	}

	/// <summary>
	/// A hash that is different on every call, and a counter of how many times anything asked for
	/// it. A value-keyed cache cannot work with this: the lookup of an already-registered value
	/// misses, so the same instance is written again as if it were new and comes back as a second
	/// object. Reference-keyed, the count stays at zero and nothing about the graph changes.
	/// </summary>
	[CerasContext("Tests")]
	[CerasSerializable]
	public class ShiftingHashNode
	{
		public string Tag;

		public int HashQueries;

		public override bool Equals(object obj) => obj is ShiftingHashNode other && other.Tag == Tag;
		public override int GetHashCode()
		{
			HashQueries++;
			return unchecked(HashQueries * -1640531527);
		}
		public override string ToString() => $"ShiftingHashNode('{Tag}')";
	}

	[CerasContext("Tests")]
	[CerasSerializable]
	public class ShiftingHashHolder
	{
		public ShiftingHashNode First;
		public ShiftingHashNode Second;
		public ShiftingHashNode AlsoFirst;
	}

	public class ObjectCacheIdentityTests : TestBase
	{
		// The raw serializer with the reflection-fallback formatter (what `new CerasFastSerializer()`
		// gives) and with the generated formatters, because the object cache is reached through the
		// same ReferenceFormatter either way and both must agree on identity.
		static CerasFastSerializer Raw(bool generated)
		{
			if (!generated) return new CerasFastSerializer();
			var config = new CerasRawConfig();
			config.UseGeneratedFormatters("Tests");
			return new CerasFastSerializer(config);
		}

		static CerasRobustSerializer Robust() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));

		// ── Two equal instances stay two objects ────────────────────────────────────────────

		[Fact]
		public void Raw_Two_Equal_Instances_Stay_Two_Objects()
		{
			foreach (var generated in new[] { false, true })
			{
				var ceras = Raw(generated);

				var shared = new EqualNode { Id = 7, Tag = "seven" };
				var distinct = new EqualNode { Id = 7, Tag = "also seven" };
				Assert.True(shared.Equals(distinct));
				Assert.False(ReferenceEquals(shared, distinct));

				var bytes = ceras.Serialize(new NodeHolder { First = shared, Second = distinct });
				var clone = ceras.Deserialize<NodeHolder>(bytes);

				// The point of the whole thing: the second slot is a *different* object, not the
				// first one wearing its place.
				Assert.NotSame(clone.First, clone.Second);
				Assert.True(clone.First.Equals(clone.Second));
				Assert.Equal("seven", clone.First.Tag);
				Assert.Equal("also seven", clone.Second.Tag);
			}
		}

		[Fact]
		public void Generated_Robust_Two_Equal_Instances_Stay_Two_Objects()
		{
			var robust = Robust();

			var first = new EqualNode { Id = 7, Tag = "seven" };
			var second = new EqualNode { Id = 7, Tag = "also seven" };
			var bytes = robust.Save(new NodeHolder { First = first, Second = second }, 1);

			var clone = robust.Load<NodeHolder>(bytes, 1);

			Assert.NotSame(clone.First, clone.Second);
			Assert.Equal("seven", clone.First.Tag);
			Assert.Equal("also seven", clone.Second.Tag);

			// ...and the migration interpreter, which is a second reader over the same bytes,
			// reaches the same graph. Nothing here is a property of the fast path.
			var interpreted = Robust().Load<NodeHolder>(SlowPath(bytes), 1);
			Assert.NotSame(interpreted.First, interpreted.Second);
			Assert.Equal("seven", interpreted.First.Tag);
			Assert.Equal("also seven", interpreted.Second.Tag);
		}

		// ── One instance in two slots stays one object ──────────────────────────────────────

		[Fact]
		public void Raw_One_Instance_In_Two_Slots_Stays_One_Object()
		{
			foreach (var generated in new[] { false, true })
			{
				var ceras = Raw(generated);
				var node = new EqualNode { Id = 7, Tag = "seven" };

				var clone = ceras.Clone(new NodeHolder { First = node, Second = node });

				Assert.Same(clone.First, clone.Second);
			}
		}

		[Fact]
		public void Generated_Robust_One_Instance_In_Two_Slots_Stays_One_Object()
		{
			var robust = Robust();
			var node = new EqualNode { Id = 7, Tag = "seven" };

			var bytes = robust.Save(new NodeHolder { First = node, Second = node }, 1);
			var clone = robust.Load<NodeHolder>(bytes, 1);

			// The back-reference id is written once and resolved by both readers to the same proxy.
			Assert.Same(clone.First, clone.Second);

			// One load, one graph: the interpreter resolves the same id to the same proxy.
			var interpreted = Robust().Load<NodeHolder>(SlowPath(bytes), 1);
			Assert.Same(interpreted.First, interpreted.Second);
		}

		// ── Hostile Equals/GetHashCode cannot reach the cache ───────────────────────────────

		[Fact]
		public void Objects_That_Are_All_Equal_And_All_Hash_To_Zero_Stay_Distinct()
		{
			// Every pair of these is Equals and shares a bucket, so a value-keyed cache would
			// collapse all three into one and a hash-keyed one would degrade to a linear scan.
			foreach (var generated in new[] { false, true })
			{
				var ceras = Raw(generated);
				var a = new AllEqualNode { Tag = "a" };
				var b = new AllEqualNode { Tag = "b" };
				var c = new AllEqualNode { Tag = "c" };

				var clone = ceras.Clone(new AllEqualHolder { First = a, Second = b, Third = c });

				Assert.Equal(new[] { "a", "b", "c" }, new[] { clone.First.Tag, clone.Second.Tag, clone.Third.Tag });
				Assert.NotSame(clone.First, clone.Second);
				Assert.NotSame(clone.Second, clone.Third);
			}

			var robust = Robust();
			var robustClone = robust.Load<AllEqualHolder>(
				robust.Save(new AllEqualHolder { First = new AllEqualNode { Tag = "a" }, Second = new AllEqualNode { Tag = "b" }, Third = new AllEqualNode { Tag = "c" } }, 1), 1);

			Assert.NotSame(robustClone.First, robustClone.Second);
			Assert.NotSame(robustClone.Second, robustClone.Third);
			Assert.Equal("c", robustClone.Third.Tag);
		}

		[Fact]
		public void A_Hash_That_Changes_On_Every_Call_Is_Never_Consulted()
		{
			foreach (var generated in new[] { false, true })
			{
				var ceras = Raw(generated);

				var node = new ShiftingHashNode { Tag = "shared" };
				var other = new ShiftingHashNode { Tag = "other" };
				// AlsoFirst is the *same instance* as First: sharing must survive a hash that a
				// value-keyed cache could never look up with.
				var holder = new ShiftingHashHolder { First = node, Second = other, AlsoFirst = node };

				var clone = ceras.Clone(holder);

				Assert.Same(clone.First, clone.AlsoFirst);
				Assert.NotSame(clone.First, clone.Second);
				Assert.Equal("shared", clone.First.Tag);
				Assert.Equal("other", clone.Second.Tag);

				// And nothing in the pipeline asked the value for its hash - not the cache, not the
				// schema walk, not the generated reader.
				Assert.Equal(0, node.HashQueries);
				Assert.Equal(0, other.HashQueries);
			}
		}

		// ── Payload bytes: the second equal instance is a full value, not a back-reference ──

		[Fact]
		public void The_Second_Equal_Instance_Is_Written_Out_Not_As_A_Back_Reference()
		{
			// The raw format only, and on purpose: it has no string table, so an occurrence of a
			// value is an occurrence in the bytes. The robust format interns payload strings, so
			// counting them there would measure the string table, not the reference cache.
			var ceras = Raw(generated: false);

			var shared = new EqualNode { Id = 7, Tag = "the-node-tag" };
			var sharedBytes = ceras.Serialize(new NodeHolder { First = shared, Second = shared });
			var distinctBytes = ceras.Serialize(new NodeHolder { First = shared, Second = new EqualNode { Id = 7, Tag = "the-node-tag" } });

			// Written once and then referred to by a one-byte id, versus written out in full.
			Assert.Equal(1, CountOccurrences(sharedBytes, "the-node-tag"));
			Assert.Equal(2, CountOccurrences(distinctBytes, "the-node-tag"));
			Assert.True(distinctBytes.Length > sharedBytes.Length,
				$"the distinct graph should carry the node twice: {distinctBytes.Length} B vs {sharedBytes.Length} B");

			// Both are still ordinary saves of the same shape: the markers, the bias and the dense
			// encounter-order ids are unchanged, which is why both load back.
			var sharedClone = ceras.Deserialize<NodeHolder>(sharedBytes);
			Assert.Equal("the-node-tag", sharedClone.First.Tag);
			Assert.Same(sharedClone.First, sharedClone.Second);

			var distinctClone = ceras.Deserialize<NodeHolder>(distinctBytes);
			Assert.Equal("the-node-tag", distinctClone.First.Tag);
			Assert.Equal("the-node-tag", distinctClone.Second.Tag);
			Assert.NotSame(distinctClone.First, distinctClone.Second);
		}

		[Fact]
		public void A_Save_Written_By_The_Collapsing_Comparer_Still_Loads()
		{
			// The backward-compatibility half, with no hand-written bytes: the opt-in comparer below
			// reproduces exactly the shape the old default produced for this graph (second slot a
			// back-reference to the first), and a stock reader must still read it - faithfully,
			// including the collapse. Nothing is re-numbered or re-keyed on the way in, because the
			// reader only allocates a proxy for a new-value marker, in stream order.
			var collapsing = new CerasFastSerializer(c => c.ObjectCacheComparer = EqualityComparer<object>.Default);
			var bytes = collapsing.Serialize(new NodeHolder
			{
				First = new EqualNode { Id = 7, Tag = "seven" },
				Second = new EqualNode { Id = 7, Tag = "also seven" },
			});

			var clone = new CerasFastSerializer().Deserialize<NodeHolder>(bytes);

			Assert.Same(clone.First, clone.Second);
			Assert.Equal("seven", clone.First.Tag);

			// ...and a save written the fixed way is read the fixed way, by the same reader, so the
			// two are told apart by the bytes rather than by the reader.
			var stock = new CerasFastSerializer();
			var fixedClone = stock.Deserialize<NodeHolder>(stock.Serialize(new NodeHolder
			{
				First = new EqualNode { Id = 7, Tag = "seven" },
				Second = new EqualNode { Id = 7, Tag = "also seven" },
			}));
			Assert.NotSame(fixedClone.First, fixedClone.Second);
		}

		// ── The explicit opt-in still does what it says ─────────────────────────────────────

		[Fact]
		public void An_Explicit_Object_Cache_Comparer_Collapses_Equal_Instances_By_Design()
		{
			// Documented as an identity-losing trade (CerasConfig.ObjectCacheComparer): the user asks
			// for equal instances to share, so they do, and the save is smaller for it. The default
			// must not quietly undo the request, and the serializer must stay usable afterwards.
			var collapsing = new CerasFastSerializer(c => c.ObjectCacheComparer = EqualityComparer<object>.Default);

			var node = new EqualNode { Id = 7, Tag = "seven" };
			var clone = collapsing.Clone(new NodeHolder { First = node, Second = new EqualNode { Id = 7, Tag = "also seven" } });

			Assert.Same(clone.First, clone.Second);
			Assert.Equal("seven", clone.First.Tag);

			// The cache is per-operation state, so the very next save on the same serializer is a
			// normal, correct save again.
			var distinct = collapsing.Clone(new NodeHolder { First = new EqualNode { Id = 1, Tag = "one" }, Second = new EqualNode { Id = 2, Tag = "two" } });
			Assert.NotSame(distinct.First, distinct.Second);
			Assert.Equal(new[] { "one", "two" }, new[] { distinct.First.Tag, distinct.Second.Tag });
		}

		[Fact]
		public void The_Default_Comparer_Is_Reference_Identity_And_Says_So()
		{
			// The config's own words used to claim the default was "default reference equality"
			// while the code used EqualityComparer<object>.Default. The null default is kept (so the
			// property stays nullable and "unset" is still expressible), and what it means is pinned
			// here rather than only in the documentation.
			var config = new CerasRawConfig();
			Assert.Null(config.ObjectCacheComparer);

			var ceras = new CerasFastSerializer(config);
			var first = new EqualNode { Id = 1, Tag = "a" };
			var second = new EqualNode { Id = 1, Tag = "b" };

			var clone = ceras.Clone(new NodeHolder { First = first, Second = second });
			Assert.NotSame(clone.First, clone.Second);
		}

		static int CountOccurrences(byte[] haystack, string needle)
		{
			var pattern = global::System.Text.Encoding.UTF8.GetBytes(needle); // Ceras.Test has its own Encoding
			int found = 0;
			for (int i = 0; i + pattern.Length <= haystack.Length; i++)
			{
				bool match = true;
				for (int j = 0; j < pattern.Length; j++)
					if (haystack[i + j] != pattern[j]) { match = false; break; }

				if (match) found++;
			}
			return found;
		}
	}
}
