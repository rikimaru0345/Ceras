namespace Ceras.Test
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using Xunit;

    /// <summary>
    /// Pins the wire format of the collection fast paths added to
    /// <c>CollectionFormatter{TCollection, TItem}</c>.
    /// <para>
    /// <see cref="EnumerableOnlyList{T}"/> is deliberately <em>not</em> a <c>List&lt;T&gt;</c> and
    /// not a <c>T[]</c>, so it still takes the <c>IEnumerable&lt;TItem&gt;</c> branch and is enumerated
    /// through an interface. It honours the same <c>Count</c>/<c>Add</c>/<c>Clear</c> contract as
    /// <c>List&lt;T&gt;</c>, so the two must produce identical bytes for identical contents. That is
    /// what makes the equality assertions below meaningful: they compare the fast path against the
    /// branch it replaced rather than against a hand-written expectation that could drift with it.
    /// The exact-byte assertions then pin the format itself, so a change to either branch is caught
    /// rather than absorbed by both moving together.
    /// </para>
    /// </summary>
    public class CollectionEncodingTests
    {
        sealed class EnumerableOnlyList<T> : ICollection<T>
        {
            readonly List<T> _inner = new();

            public int Count => _inner.Count;
            public bool IsReadOnly => false;
            public void Add(T item) => _inner.Add(item);
            public void Clear() => _inner.Clear();
            public bool Contains(T item) => _inner.Contains(item);
            public void CopyTo(T[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);
            public bool Remove(T item) => _inner.Remove(item);

            // An iterator method, so the enumerator really is an allocation: this is the branch the
            // List fast path is meant to avoid paying for.
            public IEnumerator<T> GetEnumerator()
            {
                foreach (var item in _inner) yield return item;
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        static string Hex(byte[] data) => string.Join(" ", data.Select(b => b.ToString("x2")));

        [Fact]
        public void ListFastPath_MatchesTheEnumeratingBranch_ForPrimitives()
        {
            var ceras = new CerasFastSerializer();

            var fromList = ceras.Serialize(new List<int> { 1, 2, 3 });
            var fromEnumerable = ceras.Serialize(new EnumerableOnlyList<int> { 1, 2, 3 });

            Assert.Equal(Hex(fromEnumerable), Hex(fromList));
            Assert.Equal(new byte[] { 0x02, 0x04, 1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0 }, fromList);
        }

        [Fact]
        public void ListFastPath_MatchesTheEnumeratingBranch_ForStrings()
        {
            var ceras = new CerasFastSerializer();

            var fromList = ceras.Serialize(new List<string> { "a", "bb" });
            var fromEnumerable = ceras.Serialize(new EnumerableOnlyList<string> { "a", "bb" });

            Assert.Equal(Hex(fromEnumerable), Hex(fromList));
        }

        [Fact]
        public void ListFastPath_MatchesTheEnumeratingBranch_ForEmptyAndNull()
        {
            var ceras = new CerasFastSerializer();

            Assert.Equal(
                Hex(ceras.Serialize(new EnumerableOnlyList<int>())),
                Hex(ceras.Serialize(new List<int>())));

            Assert.Equal(
                Hex(ceras.Serialize((EnumerableOnlyList<int>)null)),
                Hex(ceras.Serialize((List<int>)null)));
        }

        /// <summary>
        /// The fast path matches by runtime type, so a <c>List&lt;T&gt;</c> behind an
        /// interface-typed member takes it too - which is the common shape, and the one a
        /// special-cased formatter could not reach, because a formatter is chosen from the member's
        /// *declared* type. Two different holder types cannot be compared byte for byte (the
        /// transient format records the holder's own type name), so this pins the behaviour that
        /// matters: the list survives the round trip either way.
        /// </summary>
        [Fact]
        public void ListFastPath_AppliesThroughAnInterfaceTypedMember()
        {
            var ceras = new CerasFastSerializer();

            var viaInterface = ceras.Deserialize<InterfaceHolder>(ceras.Serialize(new InterfaceHolder { Items = new List<int> { 7, 8 } }));
            var viaConcrete = ceras.Deserialize<ConcreteHolder>(ceras.Serialize(new ConcreteHolder { Items = new List<int> { 7, 8 } }));

            Assert.Equal(new[] { 7, 8 }, viaInterface.Items);
            Assert.Equal(new[] { 7, 8 }, viaConcrete.Items);
        }

        class InterfaceHolder
        {
            public IList<int> Items;
        }

        class ConcreteHolder
        {
            public List<int> Items;
        }
    }
}
