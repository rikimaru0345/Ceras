using System.Collections.Generic;
using System.Linq;
using Ceras;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test
{
    /// <summary>
    /// Guards the reuse of the payload string intern table across saves.
    /// <para>
    /// The table is emptied and handed back after every <c>Save</c> instead of being rebuilt, so
    /// that its dictionary and list keep the capacity they grew to (see
    /// <c>CerasRobustSerializer.RentStringInternTable</c>). That is only sound if a cleared table
    /// is indistinguishable from a fresh one: same numbering from the null sentinel at index 0,
    /// and no leftovers from the previous graph. If it is not, a second save on the same serializer
    /// emits different bytes from the first - a file whose string indices no longer line up with
    /// its own table - and the damage only shows up on the second write, which is exactly what these
    /// tests exercise.
    /// </para>
    /// </summary>
    public class StringInternTableReuseTests
    {
        [CerasSerializable]
        public class Node
        {
            public int Id;
            public string Name;
        }

        [CerasContext("StringInternTableReuse")]
        [CerasSerializable]
        public class Root
        {
            public List<Node> Nodes = new();
            public List<string> Tags = new();
        }

        static Root MakeGraph(int count, params string[] tags)
        {
            var r = new Root();
            for (int i = 0; i < count; i++)
                r.Nodes.Add(new Node { Id = i, Name = "node-" + i });
            foreach (var t in tags) r.Tags.Add(t);
            return r;
        }

        /// <summary>
        /// The core property: repeated saves of the same graph through one serializer are
        /// byte-identical, and identical to a save from a brand-new serializer.
        /// </summary>
        [Fact]
        public void RepeatedSaves_AreByteIdentical_AndMatchAFreshSerializer()
        {
            var ser = new CerasFastSerializer();
            var graph = MakeGraph(40, "a", "bb", "ccc");

            var first = ser.Serialize(graph);
            for (int i = 0; i < 4; i++)
            {
                var again = ser.Serialize(graph);
                Assert.Equal(string.Join(",", first), string.Join(",", again));
            }

            var fresh = new CerasFastSerializer().Serialize(graph);
            Assert.Equal(string.Join(",", first), string.Join(",", fresh));
        }

        /// <summary>
        /// A cleared table must not leak the previous graph's strings, and a *different* graph
        /// through the same serializer has to come out the same as through a fresh one - which is
        /// what "no leftovers" actually means once the numbering restarts.
        /// </summary>
        [Fact]
        public void ASmallerGraphAfterALargerOne_EncodesTheSameAsOnAFreshSerializer()
        {
            var ser = new CerasFastSerializer();

            ser.Serialize(MakeGraph(40, "a", "bb", "ccc", "dddd", "eeeee", "ffffff"));
            var second = ser.Serialize(MakeGraph(3, "a", "bb"));

            var expected = new CerasFastSerializer().Serialize(MakeGraph(3, "a", "bb"));
            Assert.Equal(string.Join(",", expected), string.Join(",", second));
        }

        [Fact]
        public void ASavedGraph_StillLoadsAfterTheTableHasBeenReused()
        {
            var ser = new CerasFastSerializer();
            var graph = MakeGraph(20, "x", "y");

            var bytes = ser.Serialize(graph);
            ser.Serialize(MakeGraph(5, "totally", "different", "strings"));
            ser.Serialize(MakeGraph(1));

            var clone = ser.Deserialize<Root>(bytes);

            Assert.Equal(graph.Nodes.Select(n => n.Name), clone.Nodes.Select(n => n.Name));
            Assert.Equal(graph.Nodes.Select(n => n.Id), clone.Nodes.Select(n => n.Id));
            Assert.Equal(graph.Tags, clone.Tags);
        }

        /// <summary>
        /// The same property through the robust format, which is where the intern table actually
        /// matters: it is what keeps a save's string indices pointing at the save's own table.
        /// </summary>
        [Fact]
        public void RobustRepeatedSaves_AreTheSameLength_AndBothLoad()
        {
            var ser = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters("StringInternTableReuse");
                c.DeclareRoots(typeof(Root));
            });

            var graph = MakeGraph(30, "one", "two", "three");

            var lengths = new List<int>();
            for (int i = 0; i < 3; i++)
                lengths.Add(ser.Save(graph, 1).Length);

            Assert.All(lengths, l => Assert.Equal(lengths[0], l));

            var clone = ser.Load<Root>(ser.Save(graph, 1), 1);
            Assert.Equal(graph.Nodes.Select(n => n.Name), clone.Nodes.Select(n => n.Name));
            Assert.Equal(graph.Tags, clone.Tags);
        }
    }
}
