// Runtime coverage for the public nullable contracts this branch settled. The annotations themselves
// are already compile-time enforced inside src/Ceras, so what is pinned here is only the behaviour
// they promise: a test that fails if the code changes, not one that fails if a signature is
// re-annotated. Each case has its control next to it, so none can be satisfied by blanket behaviour.
using System;
using System.Collections.Generic;
using System.Reflection;
using Ceras;
using Ceras.Exceptions;
using Ceras.Formatters;
using Ceras.Resolvers;
using Xunit;

namespace Ceras.Test.PublicContracts
{
    public class PublicNullabilityContractTests
    {
        [Fact]
        public void CerasSerializable_StableName_Is_Absent_Unless_Pinned()
        {
            // StableName is string? because the parameterless [CerasSerializable] every type in a
            // context carries has no override, and that is a normal value rather than a missing one.
            // Read back off a real attribute usage as well, since that is the form the generator sees.
            Assert.Null(new CerasSerializableAttribute().StableName);
            Assert.Null(typeof(PlainRoot).GetCustomAttribute<CerasSerializableAttribute>().StableName);
            Assert.Equal("Pinned", new CerasSerializableAttribute("Pinned").StableName);
            Assert.Equal("Pinned", typeof(PinnedRoot).GetCustomAttribute<CerasSerializableAttribute>().StableName);
        }

        [Fact]
        public void PreviousName_AlternativeNames_Never_Null()
        {
            // The field is annotated non-nullable, so the constructor has to deliver on it: the
            // [PreviousName("New", null)] spelling is normalized to the empty array the other two
            // constructors already produce, rather than handed on as a null.
            Assert.Empty(new PreviousNameAttribute("New").AlternativeNames);
            Assert.Empty(new PreviousNameAttribute("New", null).AlternativeNames);
            Assert.Equal(new[] { "A" }, new PreviousNameAttribute("New", "A").AlternativeNames);
        }

        [Theory]
        // A closed generic is named by its definition, so the reader can rebuild it from that name
        // plus the arguments recorded next to it. Naming the closed form would name nothing resolvable.
        [InlineData(typeof(int), "System.Int32")]
        [InlineData(typeof(List<int>), "System.Collections.Generic.List`1")]
        public void SimpleTypeBinder_Names_A_Closed_Type(Type type, string expected)
        {
            Assert.Equal(expected, new SimpleTypeBinder().GetBaseName(type));
        }

        [Fact]
        public void SimpleTypeBinder_Refuses_A_Generic_Type_Parameter()
        {
            // A type parameter has no metadata name, so there is nothing to write. The contract is a
            // named CerasException rather than a null, which used to be written out as a null name
            // marker and so failed only later, as corrupt data naming neither the value nor the fix.
            var ex = Assert.Throws<CerasException>(
                () => new SimpleTypeBinder().GetBaseName(typeof(List<>).GetGenericArguments()[0]));

            Assert.Contains("generic type parameter", ex.Message);
            Assert.Contains("Close the generic", ex.Message);
        }

        [Fact]
        public void An_Unnameable_Type_Fails_The_Write_And_Leaves_The_Serializer_Usable()
        {
            // Refused where the value is written, so the diagnosis names the value instead of the file
            // failing to load later. Written as a root Type so the refusal surfaces unwrapped.
            var ceras = new CerasFastSerializer();

            var ex = Assert.Throws<CerasException>(
                () => ceras.Serialize<Type>(typeof(List<>).GetGenericArguments()[0]));
            Assert.Contains("Close the generic", ex.Message);

            // The aborted write must leave no per-operation state behind for the next one to trip on.
            var clone = ceras.Deserialize<Payload>(
                ceras.Serialize(new Payload { Number = 7, Items = new List<string> { "a" } }));

            Assert.Equal(7, clone.Number);
            Assert.Equal(new[] { "a" }, clone.Items);
        }

        [Fact]
        public void CollectionFormatterResolver_Declines_Non_Collections_And_Serves_A_Collection()
        {
            // GetFormatter is IFormatter? because declining is how a resolver hands a type to the next
            // one. The decline is the only null it produces, so the answer it does give is pinned to
            // the formatter type it builds rather than just "not null".
            var resolver = (CollectionFormatterResolver)new CerasFastSerializer()
                .GetFormatterResolver<CollectionFormatterResolver>();

            Assert.Null(resolver.GetFormatter(typeof(string)));
            Assert.Null(resolver.GetFormatter(typeof(int)));
            Assert.IsType<CollectionFormatter<List<int>, int>>(resolver.GetFormatter(typeof(List<int>)));
        }

        // Members are given initializers because the assignments live in the object initializers of
        // the tests, which the compiler does not count towards a field being assigned (CS0649).
        [CerasSerializable] class PlainRoot { public int Value = 0; }
        [CerasSerializable("Pinned")] class PinnedRoot { public int Value = 0; }

        class Payload { public int Number = 0; public List<string> Items = null; }
    }
}
