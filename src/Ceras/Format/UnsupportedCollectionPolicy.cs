using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Ceras.Exceptions;

namespace Ceras.Format
{
    /// <summary>
    /// The collection policy the project owner decided on, in one place: which collection shapes Ceras
    /// refuses, and the sentence it refuses them with.
    /// <para>
    /// <b>Refused:</b>
    /// <list type="bullet">
    /// <item><see cref="ReadOnlyCollection{T}"/> — the built-in type itself and every subclass;</item>
    /// <item><see cref="ReadOnlyDictionary{TKey,TValue}"/> — the built-in type itself and every subclass;</item>
    /// <item>any subclass of <see cref="List{T}"/>;</item>
    /// <item>any subclass of <see cref="Dictionary{TKey,TValue}"/>.</item>
    /// </list>
    /// <b>Kept:</b> the exact built-in <c>List&lt;T&gt;</c> and <c>Dictionary&lt;TKey,TValue&gt;</c>;
    /// <c>HashSet&lt;T&gt;</c> and its subclasses; the exact <c>Queue&lt;T&gt;</c> and <c>Stack&lt;T&gt;</c> (a
    /// subclass of either is refused, by the older and separate
    /// <see cref="CollectionShapeRejection.UnsupportedQueueOrStack"/> rule, not by this policy); arrays;
    /// and every other collection the built-in formatter serves.
    /// </para>
    /// <para>
    /// <b>Why the whole family goes — per shape, because they do not fail alike.</b> What was measured by
    /// executing each case against the code as it stood before this policy:
    /// <list type="bullet">
    /// <item><description><b>A <c>List&lt;T&gt;</c>/<c>Dictionary&lt;TKey,TValue&gt;</c> subclass with no
    /// parameterless constructor loses its elements silently on migration.</b> The interpreter's
    /// wrap-and-copy fallback (<see cref="CurrentObjectFactory.CreateCollection"/>) constructs the
    /// wrapper from a <em>freshly empty</em> backing collection and only then adds the elements to that
    /// backing collection (<see cref="CollectionFill"/>) — which a copying constructor has already taken
    /// its copy of. What comes back is a real instance of the subclass holding zero elements, the
    /// following members still aligned, and no error anywhere. The matching-layout reader does not get
    /// that far: it allocates with <c>Activator.CreateInstance</c> without arguments and surfaces a
    /// <c>MissingMethodException</c>.</description></item>
    /// <item><description><b>A <c>ReadOnlyCollection&lt;T&gt;</c> subclass fails on the way <em>out</em>,
    /// not on the way in.</b> The migration interpreter reconstructs it correctly, because the wrapper
    /// <em>retains</em> the list it is handed instead of copying it, so filling the backing list after
    /// construction does reach the wrapper. What fails is the write: the dedicated formatter was typed
    /// for the <em>base</em> <c>ReadOnlyCollection&lt;T&gt;</c>, so the reference wrapper could not bind
    /// a serializer for the subclass — a bare <c>ArgumentNullException("method")</c> for a typed slot and
    /// an <c>InvalidCastException</c> for a polymorphic <c>object</c> slot, neither naming the type nor
    /// the member.</description></item>
    /// <item><description><b>The base <c>ReadOnlyCollection&lt;T&gt;</c> worked</b>: it round-tripped
    /// through the raw serializer, including a wrapper holding a reference to itself. It is refused
    /// because it cannot be told apart from the subclass above it — the base is what the subclass
    /// resolves to — so refusing the derived type while keeping the base is not a distinction the code
    /// reaching these shapes can make.</description></item>
    /// </list>
    /// The decision is to refuse the four families as a whole, with one sentence, rather than to promise
    /// a shape whose members each fail differently and one of them fails silently.
    /// </para>
    /// <para>
    /// <b>Why the exact-type distinction is the test.</b> The exact built-in types are matched by
    /// <em>identity</em>, never through inheritance: a <c>BanBase</c>-style check on <c>List&lt;&gt;</c>
    /// would take <c>List&lt;int&gt;</c> itself down with its subclasses, and that is exactly the shape
    /// that has to keep working.
    /// </para>
    /// <para>
    /// <b>Where the refusal is enforced.</b> The policy is one predicate, asked at every layer that would
    /// have to <em>produce</em> a value for one of these types:
    /// <list type="bullet">
    /// <item>the source generator, at an annotated member (<c>CERAS033</c>) and at a declaration or
    /// context root (<c>CERAS034</c>);</item>
    /// <item>the robust bake — the structural schema, and the member/argument walk in
    /// <c>SchemaSection</c> — through <see cref="WellKnownTypes.TryResolveCollectionShape"/>;</item>
    /// <item>the raw/transient serializer, through <c>BannedTypes</c> (members, reference slots,
    /// <see cref="Type"/> slots) and through <c>CollectionFormatterResolver</c> (roots, reads);</item>
    /// <item>the migration interpreter, when a save written by an older build names one of these types.</item>
    /// </list>
    /// <b>One position is deliberately exempt:</b> the interpreter's <em>skip</em> path. A refused shape
    /// in a member the user has since <em>removed</em> has no value to produce, and its payload still has
    /// to be accounted for byte-exactly, so refusing it would turn an intentional deletion into an
    /// unloadable save. Only the read path — the one that would have to invent a value — refuses. This is
    /// the same asymmetry the <c>Queue&lt;&gt;</c>/<c>Stack&lt;&gt;</c> subclass rejection has.
    /// </para>
    /// <para>
    /// Nested reachability is part of the same question rather than a separate one: the bake expands a
    /// described collection into its element/key/value type identities, so
    /// <c>List&lt;ReadOnlyCollection&lt;int&gt;&gt;</c> and <c>ReadOnlyCollection&lt;int&gt;[]</c> are
    /// refused at the argument that is refused, and the generator reports the same walk at compile time.
    /// </para>
    /// <para>
    /// The policy does not change any byte for a shape it keeps, and it is not a format change: a
    /// context whose manifest contains none of these shapes bakes exactly as before.
    /// </para>
    /// </summary>
    static class UnsupportedCollectionPolicy
    {
        /// <summary>
        /// The built-in types that are refused wherever a value would be produced, the type itself
        /// included. Matched by generic-type-definition identity anywhere in the base chain.
        /// </summary>
        static readonly Type[] RefusedEverywhere =
        {
            typeof(ReadOnlyCollection<>),
            typeof(ReadOnlyDictionary<,>),
        };

        /// <summary>
        /// The built-in types that stay supported, but only when the type <em>is</em> one of them: a
        /// derived type is refused. This array is deliberately not a "base" ban — see the remarks.
        /// </summary>
        static readonly Type[] RefusedWhenDerived =
        {
            typeof(List<>),
            typeof(Dictionary<,>),
        };

        /// <summary>The actionable half of the message, identical at every site.</summary>
        internal const string Advice =
            "Store the elements in a List<T> / Dictionary<TKey,TValue> member (or another collection shape Ceras " +
            "describes) instead; the refused wrapper or subclass is not part of the saved graph.";

        /// <summary>
        /// The reason, used wherever a fixed string is required (the banned-type tables, whose entries carry
        /// one reason each). Each family is named separately, so a reader can tell which of the four rules
        /// the type broke without counting list items.
        /// </summary>
        internal const string BanReason =
            "Ceras does not support ReadOnlyCollection<T> or ReadOnlyDictionary<TKey,TValue> — neither the built-in type nor any " +
            "subclass of either — nor a subclass of List<T> nor a subclass of Dictionary<TKey,TValue>. " + Advice;

        /// <summary>
        /// True for a type the policy refuses. A pure predicate: it answers for every type, never raises,
        /// and null is simply "no type to refuse".
        /// </summary>
        internal static bool IsUnsupported(Type? type)
        {
            if (type == null) return false;

            for (var t = type; t != null; t = t.BaseType)
            {
                // IsGenericType is the cheap gate; GetGenericTypeDefinition is only asked of a closed
                // generic, so an ordinary class costs two iterations and no reflection call at all.
                if (!t.IsGenericType || t.IsGenericTypeDefinition) continue;

                var definition = t.GetGenericTypeDefinition();

                foreach (var refused in RefusedEverywhere)
                    if (definition == refused) return true;

                // The exact built-in type keeps its own definition and stays supported; only something
                // derived from it is refused. `t != type` is what says "derived".
                if (t != type)
                {
                    foreach (var keptExact in RefusedWhenDerived)
                        if (definition == keptExact) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The full sentence: the refused type, the position it was refused in, and the way out.
        /// <para>
        /// <paramref name="where"/> is phrased as the object of "cannot be used with", because that is
        /// the only part each caller knows and every caller names a different position (a schema, a
        /// migration, a reference slot).
        /// </para>
        /// </summary>
        internal static string Describe(Type? type, string where)
            => $"The collection type '{TypeIdentity.GetCanonicalNameOrUnknown(type)}' cannot be used with {where}. {BanReason}";

        /// <summary>
        /// The typed refusal. Always a <see cref="CerasSchemaException"/>, so a caller can catch "this
        /// shape is not describable" without matching message text — the same contract the bake's other
        /// refusals already have.
        /// </summary>
        internal static CerasSchemaException Reject(Type? type, string where)
            => new CerasSchemaException(Describe(type, where));
    }
}
