// The current (v2) side of the renamed-generic fixture, and how to read the v1 side.
//
// v1's CLR model lives in fixtures/Ceras.GenericAliasWriter, an assembly the test project does not
// reference, so the v1 types do not exist here at all: what these tests read is the checked-in bytes plus
// the model below. Two test classes share it, and they pin two different things:
//
//   * RenamedGenericAliasTests      - identity. An open `[PreviousTypeName]` alias has to match the
//                                     *closed* canonical names a file actually contains.
//   * GenericMemberOrderTests       - the member order a closed schema records, which is fixed by the
//                                     definition's one shared writer rather than by the closure.
//
// The two v1 generics they each need are shaped so the two questions do not interfere: the renamed
// generics in v1 have a single member (so their member order cannot disagree with itself), and the
// order-sensitive `Wrap<T>` is only ever loaded through the ordering tests.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;
using Xunit;

// One fixture is a *collection of renamed closures* as its own root, and a built-in collection is never
// part of a generated manifest - it has to be declared as an allowed root shape, exactly as a game would
// declare it for a save that holds `List<SomeType>` at its root.
[assembly: CerasKnownType(typeof(System.Collections.Generic.List<Ceras.Test.GenericAliasFixtures.Box<int>>), "GenericAliasV2")]

namespace Ceras.Test.GenericAliasFixtures
{
    // Every name below is v1's, so a migration through it is a rename rather than a fresh read.

    /// <summary>
    /// v1's <c>Legacy.Box`1</c>. The alias is the *open* name, which is all an attribute can express; the
    /// file names four different closures of it.
    /// </summary>
    [CerasContext("GenericAliasV2")]
    [CerasSerializable]
    [PreviousTypeName("Legacy.Box`1")]
    public class Box<T>
    {
        [PreviousName("Value")]    // v1's member was `Value`
        public T Payload;

        public int Added;           // new in the current model, and what makes the layouts differ

        /// <summary>
        /// v1's <c>Legacy.Box`1+Cell`1</c>: a generic nested in the renamed generic, so the name the file
        /// uses for it starts with the enclosing generic's open alias. The alias names the <em>nested</em>
        /// definition, not the enclosing one - which is the difference a prefix match cannot see.
        /// </summary>
        [CerasContext("GenericAliasV2")]
        [CerasSerializable]
        [PreviousTypeName("Legacy.Box`1+Cell`1")]
        public class Cell<U>
        {
            [PreviousName("Payload")]
            public U Content;

            public string Name;
            public int Added;
        }
    }

    /// <summary>v1's <c>Legacy.Crate`2</c>: a different arity, so arity is not assumed to be one.</summary>
    [CerasContext("GenericAliasV2")]
    [CerasSerializable]
    [PreviousTypeName("Legacy.Crate`2")]
    public class Crate<A, B>
    {
        [PreviousName("First")]
        public A Head;

        [PreviousName("Second")]
        public B Tail;

        public int Added;
    }

    /// <summary>
    /// v1's <c>Legacy.Coord</c>: an ordinary, non-generic rename, and the control for "plain aliases still
    /// work" - both on its own and as the <em>argument</em> of a renamed generic. v1 also had a <c>Z</c>,
    /// which is gone here, so its bytes have to be skipped inside a fixed-width struct.
    /// </summary>
    [CerasContext("GenericAliasV2")]
    [CerasSerializable]
    [PreviousTypeName("Legacy.Coord")]
    public struct Coord
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// v1's <c>Legacy.Wrap`1</c>: the generic whose member order depends on its type argument, and the
    /// only one whose v1 twin has more than one member. Closed over <see cref="Coord"/> - a name that
    /// sorts before <c>System.Int32</c> - the <em>closed</em> members would sort <c>Value</c> before
    /// <c>Tag</c>, while the definition's one shared writer emits <c>Tag</c> first.
    /// </summary>
    [CerasContext("GenericAliasV2")]
    [CerasSerializable]
    [PreviousTypeName("Legacy.Wrap`1")]
    public class Wrap<T>
    {
        public T Value;
        public int Tag;
        public int Added;           // new here, and what makes the layouts differ
    }

    /// <summary>v1's <c>Legacy.Sleeve</c>: carries the order-sensitive generic and a trailing sentinel.</summary>
    [CerasContext("GenericAliasV2")]
    [CerasSerializable]
    [PreviousTypeName("Legacy.Sleeve")]
    public class Sleeve
    {
        public Wrap<Coord> Wrapped;
        public string Tail;
    }

    [CerasContext("GenericAliasV2")]
    [CerasSerializable]
    [PreviousTypeName("Legacy.Save")]
    public class Save
    {
        public int Version;
        public List<Box<int>> Ints;
        public Box<string> Text;
        public Box<Coord> Point;
        public Crate<int, string> Pair;
        public Box<int>.Cell<Coord> Cell;
        public object Slot;
        public Coord Marker;
        public string Tail;
    }
}

namespace Ceras.Test.GenericAliasAmbiguity
{
    // A context of its own, because what follows is a *declaration* error that has to be reported: the two
    // types can only coexist in a manifest that is never successfully baked.
    //
    // Both claim the same open alias. That is not a cosmetic overlap. An alias is what a save file is
    // matched against, so "Legacy.Box`1" would answer for two unrelated definitions and the closure a file
    // named would be bound to whichever registered first - silently, on a save that is otherwise perfectly
    // valid. The bake refuses it instead, naming both types.
    [CerasContext("GenericAliasAmbiguous")]
    [CerasSerializable]
    [PreviousTypeName("Legacy.Box`1")]
    public class First<T>
    {
        public T Value;
    }

    [CerasContext("GenericAliasAmbiguous")]
    [CerasSerializable]
    [PreviousTypeName("Legacy.Box`1")]
    public class Second<T>
    {
        public T Value;
    }

    [CerasContext("GenericAliasAmbiguous")]
    [CerasSerializable]
    public class AmbiguousRoot
    {
        public First<int> A;
        public Second<string> B;
    }
}

namespace Ceras.Test
{
    /// <summary>
    /// Reading the v1 side of the renamed-generic fixture: the golden bytes, the schema section out of
    /// them, and the id map <see cref="CerasRobustSerializer"/> builds from it. One copy of the envelope
    /// parsing, because the offset arithmetic is the kind of thing that has to be right once.
    /// </summary>
    internal static class GenericAliasFixture
    {
        public const string Context = "GenericAliasV2";
        public const string Tail = "TAIL-SENTINEL";

        public static CerasRobustSerializer Reader() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Context));

        public static byte[] Fixture(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "GenericAliasV1", name);
            Assert.True(File.Exists(path), $"missing golden fixture '{name}' at '{path}'. " +
                "Run fixtures/Ceras.GenericAliasWriter to regenerate it (see its README.md).");
            return File.ReadAllBytes(path);
        }

        /// <summary>The payload region of a save, straight out of the header's offsets.</summary>
        public static byte[] PayloadOf(string fixtureName)
        {
            byte[] data = Fixture(fixtureName);
            Ceras.RobustHeader.Read(data, out _, out _, out _, out long dataSectionOffset,
                out long stringTableOffset, out _);
            return data.AsSpan((int)dataSectionOffset, (int)(stringTableOffset - dataSectionOffset)).ToArray();
        }

        /// <summary>
        /// The file's own schema section, read exactly as Load reads it, so every assertion below runs on
        /// real names and real ids rather than on a hand-written stand-in.
        /// </summary>
        public static Dictionary<string, OldTypeSchema> ReadFileSchema(string fixtureName)
        {
            byte[] data = Fixture(fixtureName);

            Ceras.RobustHeader.Read(data, out _, out _, out long schemaStringOffset,
                out long dataSectionOffset, out _, out _);

            var schemaStrings = StringTable.Load(data, (int)schemaStringOffset,
                (int)dataSectionOffset - (int)schemaStringOffset, StringTableCompression.Disabled);

            var sequence = new ReadOnlySequence<byte>(data, Ceras.RobustHeader.Size,
                (int)schemaStringOffset - Ceras.RobustHeader.Size);
            var reader = new SequenceReader<byte>(sequence);
            return SchemaSection.Read(ref reader, schemaStrings);
        }

        /// <summary>
        /// A file's ids mapped onto the current model, through the boundary Load itself uses
        /// (<see cref="CerasRobustSerializer.BuildIdToType"/>).
        /// </summary>
        public static Dictionary<int, Type> IdToType(Dictionary<string, OldTypeSchema> fileSchemas,
            ITypeBinder typeBinder = null)
        {
            var serializer = Reader();
            serializer.Config.SchemaRegistry.Seal();
            return CerasRobustSerializer.BuildIdToType(fileSchemas, serializer.Config.SchemaRegistry,
                typeBinder ?? serializer.Config.TypeBinder);
        }

        /// <summary>
        /// What a file naming exactly one type resolves that type's id to, as a canonical name - or
        /// <c>&lt;unmapped&gt;</c>. One name per call, so a case cannot be satisfied by a neighbour.
        /// </summary>
        public static string IdResolvesTo(string fileName)
        {
            // In the user range, not a well-known id: the id map pre-fills those with framework types, so
            // an id below 128 would answer without the schema ever being consulted.
            const int fileTypeId = 500;
            var schemas = new Dictionary<string, OldTypeSchema>(StringComparer.Ordinal)
            {
                [fileName] = new OldTypeSchema(
                    typeId: fileTypeId, stableName: fileName, previousNames: Array.Empty<string>(),
                    kind: SchemaTypeKind.Class, fields: Array.Empty<OldFieldSchema>(), fixedByteSize: 0,
                    definitionTypeId: 0, argTypeIds: null!, enumValues: null),
            };

            return Canonical(IdToType(schemas, new NullReturningBinder()), fileTypeId);
        }

        /// <summary>What a file's type id resolved to, as a canonical name, or the fact that it did not.</summary>
        public static string Canonical(Dictionary<int, Type> idToType, int id)
            => idToType.TryGetValue(id, out var t) ? TypeIdentity.GetCanonicalName(t) : "<unmapped>";

        /// <summary>A name resolved against the current model, or null when nothing answers for it.</summary>
        public static Type Resolve(string name)
        {
            var registry = Reader().Config.SchemaRegistry;
            registry.Seal();
            return registry.TryResolveType(name, out var t) ? t : null;
        }

        /// <summary>
        /// A binder that answers "no such type" with null. The id map asks one only for an entry the
        /// registry could not resolve, and the default binder throws for a name that names nothing at all -
        /// a real behaviour, but not the one a test about whether an <em>alias</em> matched a name is about.
        /// </summary>
        public sealed class NullReturningBinder : ITypeBinder
        {
            public string GetBaseName(Type type) => type.FullName;
            public Type GetTypeFromBase(string baseTypeName) => null;
            public Type GetTypeFromBaseAndArguments(string baseTypeName, params Type[] genericTypeArguments) => null;
        }
    }
}
