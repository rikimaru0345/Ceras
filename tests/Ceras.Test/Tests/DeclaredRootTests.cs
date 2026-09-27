using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Generated;

namespace Ceras.Test.Declared
{
    // Deliberately NOT declared with [CerasSerializable]/[CerasKnownType].
    public class UndeclaredThing
    {
        public int X;
    }

    /// <summary>
    /// Phase 2.6/3.3: the generated manifest is authoritative. Undeclared user types are rejected
    /// with actionable guidance, while well-known collections and arrays are valid roots even though
    /// the generator skips generating formatters for them.
    /// </summary>
    public class DeclaredRootTests
    {
        static CerasRobustSerializer Generated() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));

        [Fact]
        public void CollectionRoot_RoundTrips()
        {
            var list = new List<int> { 1, 2, 3 };
            var bytes = Generated().Save(list, 1);
            var clone = Generated().Load<List<int>>(bytes, 1);

            Assert.Equal(new[] { 1, 2, 3 }, clone);
        }

        [Fact]
        public void ArrayRoot_RoundTrips()
        {
            var arr = new[] { 4, 5, 6 };
            var bytes = Generated().Save(arr, 1);
            var clone = Generated().Load<int[]>(bytes, 1);

            Assert.Equal(new[] { 4, 5, 6 }, clone);
        }

        [Fact]
        public void DictionaryRoot_RoundTrips()
        {
            var dict = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
            var bytes = Generated().Save(dict, 1);
            var clone = Generated().Load<Dictionary<string, int>>(bytes, 1);

            Assert.Equal(1, clone["a"]);
            Assert.Equal(2, clone["b"]);
        }

        [Fact]
        public void UndeclaredUserType_Save_ThrowsActionableError()
        {
            var robust = Generated();
            var ex = Assert.Throws<Ceras.Exceptions.CerasException>(() => robust.Save(new UndeclaredThing { X = 1 }, 1));

            Assert.Contains("not declared", ex.Message);
            Assert.Contains("CerasKnownType", ex.Message);
        }

        [Fact]
        public void UndeclaredUserType_Load_ThrowsActionableError()
        {
            // Produce bytes for a declared type, then attempt to load them as an undeclared type.
            var bytes = Generated().Save(new List<int> { 1 }, 1);
            var ex = Assert.Throws<Ceras.Exceptions.CerasException>(() => Generated().Load<UndeclaredThing>(bytes, 1));
            Assert.Contains("not declared", ex.Message);
        }
    }
}
