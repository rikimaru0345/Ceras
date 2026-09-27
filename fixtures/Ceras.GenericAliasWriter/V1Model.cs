// The v1 (old build) model of a save whose *generic* types have since been renamed.
//
// This assembly is deliberately never referenced by tests/Ceras.Test: the v1 CLR types must be absent
// from the reader build, so the reader sees only these bytes plus the current model. That is what makes
// the cross-version test real rather than a second reader of the current schema.
//
// Everything here lives in the `Legacy` namespace and is renamed by v2, so the file's schema section
// names closed generic types as `Legacy.Box`1<System.Int32>` and friends - the *closed canonical
// names*, not the open alias a v2 `[PreviousTypeName("Legacy.Box`1")]` can record.
//
// The rename fixtures below use single-member generics on purpose, and the reason is in the reader
// test's header: a *closed* generic's members are written by the definition's shared formatter, in the
// order that definition's members sort in, and a second member whose type sorts differently once closed
// makes that order depend on the argument. `Wrap<T>` below exists precisely to pin that order (its
// schema must list `Tag` before `Value`), while the renamed generics stay minimal so the *identity*
// problem they exist to pin is not entangled with it.
using System.Collections.Generic;
using Ceras;
using Ceras.Generated;

namespace Legacy
{
    /// <summary>
    /// The generic whose rename is the point of these fixtures. v2 renames the member and adds one, which
    /// changes the layout fingerprint, so the file can only be read through the migration interpreter.
    /// </summary>
    [CerasContext("GenericAliasV1")]
    [CerasSerializable]
    public class Box<T>
    {
        public T Value;

        /// <summary>
        /// A generic type *nested inside the renamed generic*. Its canonical name is
        /// <c>Legacy.Box`1+Cell`1&lt;System.Int32, Legacy.Coord&gt;</c> - the containing type's
        /// arguments first - so that name *starts with* the open alias of the enclosing <c>Box`1</c>.
        /// A save holding both is exactly what a text-prefix match would get wrong; a structural match has
        /// to see two different definitions.
        /// </summary>
        [CerasContext("GenericAliasV1")]
        [CerasSerializable]
        public class Cell<U>
        {
            public U Payload;
        }
    }

    /// <summary>A second generic with a different arity, so arity is not accidentally assumed to be one.</summary>
    [CerasContext("GenericAliasV1")]
    [CerasSerializable]
    public class Crate<A, B>
    {
        public A First;
        public B Second;
    }

    /// <summary>
    /// A generic with a member declared as a type parameter <em>and</em> another member: the shape whose
    /// member order depends on the type argument. Every closure is written by the definition's one shared
    /// formatter, so it emits <c>Tag</c> before <c>Value</c> - the parameter name sorts after
    /// <c>System.Int32</c> - while the <em>closed</em> members sort the other way round for an argument
    /// whose name sorts before <c>System.Int32</c>, which is what closing it over <see cref="Coord"/>
    /// does. A save of this type is therefore the file that pins the order a schema has to record.
    /// </summary>
    [CerasContext("GenericAliasV1")]
    [CerasSerializable]
    public class Wrap<T>
    {
        public T Value;
        public int Tag;
    }

    /// <summary>
    /// A renamed struct: the ordinary (non-generic) alias. It is used as a plain member and as a generic
    /// <em>argument</em>, so the argument's own alias has to resolve while a closed name is rebuilt. v2
    /// drops <c>Z</c>, so its bytes have to be skipped inside a fixed-width layout.
    /// </summary>
    [CerasContext("GenericAliasV1")]
    [CerasSerializable]
    public struct Coord
    {
        public int X;
        public int Y;
        public int Z;     // removed in v2
    }

    /// <summary>
    /// Carries <see cref="Wrap{T}"/> and a trailing sentinel, so a read that lost its alignment on the
    /// generic shows up in a member written after it instead of looking like a plausible value.
    /// </summary>
    [CerasContext("GenericAliasV1")]
    [CerasSerializable]
    public class Sleeve
    {
        public Wrap<Coord> Wrapped;
        public string Tail;
    }

    /// <summary>The save root.</summary>
    [CerasContext("GenericAliasV1")]
    [CerasSerializable]
    public class Save
    {
        public int Version;

        // A renamed closed generic as a collection element: the closure's identity has to be rebuilt
        // while a built-in collection is read.
        public List<Box<int>> Ints;

        // The same generic with another argument.
        public Box<string> Text;

        // The renamed struct as a generic argument, so the *argument's* alias has to resolve while the
        // enclosing closed name is being closed with it.
        public Box<Coord> Point;

        // A different arity.
        public Crate<int, string> Pair;

        // The nested type, whose canonical name begins with the enclosing generic's open alias and whose
        // own argument is renamed as well.
        public Box<int>.Cell<Coord> Cell;

        // A renamed generic behind `object`: written as a polymorphic type tag rather than a field type.
        public object Slot;

        // A renamed struct as an ordinary member, and a sentinel that is the last thing written: any
        // desynchronisation before it shows up here.
        public Coord Marker;
        public string Tail;
    }
}
