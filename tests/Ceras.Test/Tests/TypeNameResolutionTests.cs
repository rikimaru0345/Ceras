// What a name resolves to, and what nothing may resolve it to.
//
// A stable name is how a save file is matched onto this build, so "which type does this string stand
// for" has to be a fact about the context, not about the process. TryResolveType used to end in a
// bounded walk of every loaded assembly, which made that answer depend on what happened to be loaded
// at read time: a save written on a host with a plugin loaded described a different graph from the
// same bytes read on a host without it. There are now exactly three identities, in this order:
//
//   1. the manifest's own names — each registered entry's stable name and its [PreviousTypeName]
//      aliases (SchemaRegistry.ClaimNames);
//   2. Type.GetType, for the framework and assembly-qualified spellings;
//   3. the context's configured ITypeBinder, for everything else, reached only from the canonical-name
//      parser's definition lookup and memoized per name (SchemaRegistry.ResolveDefinitionName).
//
// A name none of the three answers resolves to nothing. The binder is the one door outside the
// manifest, it is explicit, and it is installed once at context setup from Config.TypeBinder, so the
// same bytes resolve the same way in any process that configured the same context.
using System;
using System.Collections.Generic;
using Ceras.Exceptions;
using Ceras.Format;
using Xunit;

namespace Ceras.Test.NameResolution
{
    // Local and unserialized: these are identities, not model shapes. Every descriptor below is
    // hand-written, so a member's type name is a string the bake has to resolve rather than something
    // the CLR supplies — which is the mechanism under test, and why none of them carries a field.
    sealed class Unregistered<T> { }
    class UnregisteredHolder { }
    class Plain { }

    [PreviousTypeName("NameResolution.Legacy.Plain")]
    class Aliased { }

    class BinderNamed { }
    class BinderNamedOther { }

    public class TypeNameResolutionTests
    {
        const string PlainName = "NameResolution.Plain";
        const string AliasName = "NameResolution.Legacy.Plain";
        const string UnregisteredClosed = "Ceras.Test.NameResolution.Unregistered`1<System.Int32>";

        static SchemaTypeInfo Info(string stableName, params string[] aliases)
            => new SchemaTypeInfo(stableName, aliases, SchemaTypeKind.Class, Array.Empty<SchemaFieldInfo>(), 0);

        /// <summary>A registry with no binder, which is the state of every hand-built one.</summary>
        static SchemaRegistry Bare() => new SchemaRegistry();

        [Fact]
        public void Every_Registered_Name_And_Alias_Resolves_Without_Any_Scan()
        {
            var registry = Bare();
            registry.Register(typeof(Plain), Info(PlainName));
            registry.Register(typeof(Aliased), Info("NameResolution.Aliased", AliasName));

            Assert.True(registry.TryResolveType(PlainName, out var plain));
            Assert.Equal(typeof(Plain), plain);

            // The alias is a manifest name like any other, so it resolves with no binder and no scan.
            Assert.True(registry.TryResolveType(AliasName, out var aliased));
            Assert.Equal(typeof(Aliased), aliased);
            Assert.True(registry.TryResolveType("NameResolution.Aliased", out var byStable));
            Assert.Equal(typeof(Aliased), byStable);

            // … and it stays true with a binder installed, which is the state a real context is in: the
            // manifest is still the answer, and it is asked first.
            registry.SetTypeBinder(new SimpleTypeBinder());
            Assert.True(registry.TryResolveType(PlainName, out var stillPlain));
            Assert.Equal(typeof(Plain), stillPlain);
            Assert.True(registry.TryResolveType(AliasName, out var stillAliased));
            Assert.Equal(typeof(Aliased), stillAliased);
        }

        [Fact]
        public void An_Unregistered_Type_Resolves_To_Nothing()
        {
            var registry = Bare();
            registry.Register(typeof(Plain), Info(PlainName));

            // Declared in this assembly but not in this manifest, so neither the name nor any alias of
            // it stands for a type here. The assembly it lives in is irrelevant to the answer.
            Assert.False(registry.TryResolveType("Ceras.Test.NameResolution.Plain", out var a));
            Assert.Null(a);
            Assert.False(registry.TryResolveType("Ceras.Test.NameResolution.NotRegistered", out var b));
            Assert.Null(b);
            Assert.False(registry.TryResolveType("Ceras.Test.NameResolution.NotRegistered.Legacy", out _));
            Assert.False(registry.TryResolveType("", out _));
            Assert.False(registry.TryResolveType(null, out _));
        }

        [Fact]
        public void A_Registered_Closure_Resolves_Under_Its_Canonical_Closed_Name()
        {
            var registry = Bare();
            registry.Register(typeof(List<int>), Info("System.Collections.Generic.List`1<System.Int32>"));

            // The canonical closed name is the entry's own key, so it resolves through the manifest
            // without consulting Type.GetType or any binder.
            Assert.True(registry.TryResolveType("System.Collections.Generic.List`1<System.Int32>", out var closed));
            Assert.Equal(typeof(List<int>), closed);

            // A closure of the same definition with a different argument is not in the manifest, and it
            // still resolves — by Type.GetType's knowledge of the framework definition, which is the
            // boundary of step 2. The runtime covers the framework and nothing else, so a *user*
            // definition with no manifest entry and no binder resolves to nothing (the case below).
            Assert.False(registry.TryGetId(typeof(List<string>), out _));
            Assert.True(registry.TryResolveType("System.Collections.Generic.List`1<System.String>", out var other));
            Assert.Equal(typeof(List<string>), other);
        }

        [Fact]
        public void A_Framework_Definition_Resolves_Through_Type_GetType_With_No_Binder()
        {
            var registry = Bare();

            // No manifest entry and no binder, so the only thing that can answer is the runtime itself.
            // A framework definition is a fact about the platform rather than a choice, which is why it
            // resolves whether or not this context configured a binder.
            Assert.True(registry.TryResolveType("System.Collections.Generic.List`1", out var listDef));
            Assert.Equal(typeof(List<>), listDef);

            // And a closed canonical name built from it, which goes through the parser.
            Assert.True(registry.TryResolveType("System.Collections.Generic.List`1<System.String>", out var closed));
            Assert.Equal(typeof(List<string>), closed);

            // A framework type Type.GetType cannot know by a bare name, because it lives in an assembly
            // the runtime does not search, resolves to nothing like any other miss. This is the case the
            // binder exists for — and the next test.
            Assert.False(registry.TryResolveType("System.Uri", out _));
        }

        [Fact]
        public void An_External_Definition_Resolves_Through_The_Configured_Binder()
        {
            var registry = Bare();
            registry.SetTypeBinder(new SimpleTypeBinder());

            // System.Uri is in System.Private.Uri, which Type.GetType does not search, so the binder is
            // the only door — and it is a door somebody opened on purpose.
            Assert.True(registry.TryResolveType("System.Uri", out var uri));
            Assert.Equal(typeof(Uri), uri);

            // A different name for the same type is not answered: a binder is consulted for a name, not
            // asked whether some type happens to exist.
            Assert.False(registry.TryResolveType("Uri", out _));
            Assert.False(registry.TryResolveType("Not.System.Anything", out _));
        }

        [Fact]
        public void An_Unregistered_User_Generic_Definition_Reaches_The_Bake_Time_Error()
        {
            var registry = Bare();
            registry.Register(typeof(UnregisteredHolder), new SchemaTypeInfo(
                stableName: "NameResolution.UnregisteredHolder",
                previousTypeNames: Array.Empty<string>(),
                kind: SchemaTypeKind.Class,
                fields: new[] { new SchemaFieldInfo("Value", Array.Empty<string>(), UnregisteredClosed, true) },
                fixedByteSize: 0));

            // The definition the name is built from is not in the manifest, and with no binder nothing
            // can resolve it — so the closed name cannot be parsed into a type either.
            Assert.False(registry.TryResolveType("Ceras.Test.NameResolution.Unregistered`1", out _));
            Assert.False(registry.TryResolveType(UnregisteredClosed, out _));

            // The bake then fails loudly and actionably, naming the member and the unresolvable name.
            // This is the existing message for a name that resolves to no CLR type, and it is the right
            // advice here: the type is not declared in this compilation, so declaring it is the fix.
            var ex = Assert.Throws<CerasSchemaException>(() => registry.Seal());
            Assert.Contains(UnregisteredClosed, ex.Message, StringComparison.Ordinal);
            Assert.Contains("field 'Value'", ex.Message, StringComparison.Ordinal);
            Assert.Contains("does not resolve", ex.Message, StringComparison.Ordinal);
            Assert.Contains("[CerasSerializable]", ex.Message, StringComparison.Ordinal);

            // Nothing half-baked was published: a refused bake leaves the context unsealed and unbaked.
            Assert.False(registry.IsSealed);
            Assert.False(registry.HasBakedSchema);
        }

        [Fact]
        public void A_Binder_That_Throws_On_Every_Call_Does_Not_Break_A_Manifest_Name()
        {
            var registry = Bare();
            registry.Register(typeof(Plain), Info(PlainName));
            registry.SetTypeBinder(new ThrowingBinder());

            // SimpleTypeBinder reports "no such type" by throwing (GetTypeFromBase searches every loaded
            // assembly and then throws), so the broad catch is what a real context depends on.
            Assert.True(registry.TryResolveType(PlainName, out var plain));
            Assert.Equal(typeof(Plain), plain);

            // The throw is a miss, not a failure of the resolution: a name nothing holds still resolves
            // to nothing rather than propagating out of the bake.
            Assert.False(registry.TryResolveType("Ceras.Test.NameResolution.NotRegistered", out var a));
            Assert.Null(a);
            Assert.False(registry.TryResolveType(UnregisteredClosed, out var b));
            Assert.Null(b);
        }

        [Fact]
        public void A_Binder_Name_Shadowing_A_Different_Manifest_Entry_Is_Refused_At_Seal()
        {
            var config = new CerasRobustConfig();
            config.TypeBinder = new MisnamingBinder();
            config.SchemaRegistry.Register(typeof(BinderNamed), Info("NameResolution.BinderNamed"));
            config.SchemaRegistry.Register(typeof(BinderNamedOther), Info("NameResolution.BinderNamedOther"));

            var ex = Assert.Throws<CerasSchemaException>(() => config.Seal());

            // A save file saying the shadowing name would bind to one type through the manifest and to
            // another through the binder, depending on which of the two reads it. The message names both
            // claimants, the name they disagree about, and the fix.
            Assert.Contains("NameResolution.BinderNamedOther", ex.Message, StringComparison.Ordinal);
            Assert.Contains("NameResolution.BinderNamed", ex.Message, StringComparison.Ordinal);
            Assert.Contains("mis-binding", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Config.TypeBinder", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Binder_Naming_An_Entry_Under_Its_Own_Manifest_Name_Is_Fine()
        {
            var config = new CerasRobustConfig();
            config.TypeBinder = new SimpleTypeBinder();
            config.SchemaRegistry.Register(typeof(BinderNamed), Info("NameResolution.BinderNamed"));
            config.SchemaRegistry.Register(typeof(BinderNamedOther), Info("NameResolution.BinderNamedOther"));

            // The ordinary case: the binder names each type by the name the manifest already uses for
            // it, which is the overlap ClaimNames allows, so the seal is silent. (The registry itself is
            // not sealed — CerasRobustConfig.Seal leaves baking to the first Save/Load.)
            config.Seal();
            Assert.False(config.SchemaRegistry.IsSealed);

            // Both entries still resolve to the type they belong to, not to whatever the binder calls them.
            Assert.True(config.SchemaRegistry.TryResolveType("NameResolution.BinderNamed", out var named));
            Assert.Equal(typeof(BinderNamed), named);
            Assert.True(config.SchemaRegistry.TryResolveType("NameResolution.BinderNamedOther", out var other));
            Assert.Equal(typeof(BinderNamedOther), other);
        }

        [Fact]
        public void Installing_A_Different_Binder_Drops_The_Memoized_Answers()
        {
            var registry = Bare();
            // A binder that can name exactly one type, so its answer is a fact about the binder rather
            // than about what happens to be loaded — which is what makes a call count meaningful.
            var binder = new SingleTypeBinder(typeof(Plain), PlainName);
            registry.SetTypeBinder(binder);

            Assert.True(registry.TryResolveType(PlainName, out var plain));
            Assert.Equal(typeof(Plain), plain);
            Assert.Equal(1, binder.Calls);

            // The memo answers the second time without asking again, which is the point: a binder answer
            // costs an assembly search, and a miss costs an exception.
            Assert.True(registry.TryResolveType(PlainName, out _));
            Assert.Equal(1, binder.Calls);

            // Installing a different binder drops the memo, because the answers came from the previous
            // binder's. The same name now has no answer, and that miss is memoized in turn.
            registry.SetTypeBinder(new NullReturningBinder());
            Assert.False(registry.TryResolveType(PlainName, out _));
            Assert.False(registry.TryResolveType(PlainName, out _));

            // InternalClear releases the memo too, so a reused registry cannot carry answers computed
            // before it was reset.
            registry.SetTypeBinder(binder);
            registry.InternalClear();
            registry.SetTypeBinder(new SingleTypeBinder(typeof(Aliased), "NameResolution.Aliased"));
            Assert.False(registry.TryResolveType(PlainName, out _));
            Assert.True(registry.TryResolveType("NameResolution.Aliased", out var aliased));
            Assert.Equal(typeof(Aliased), aliased);
        }

        [Fact]
        public void A_Memoized_Miss_Does_Not_Shadow_A_Later_Registration()
        {
            var registry = Bare();
            registry.SetTypeBinder(new NullReturningBinder());

            // Asked before the type joins: the manifest is consulted before the memo, so this is a miss
            // (and the miss is what gets recorded).
            Assert.False(registry.TryResolveType(PlainName, out _));

            // The manifest is consulted first on every call, so the memo cannot keep answering "no such
            // name" for a name the manifest has since claimed. That ordering is what makes the bake work:
            // it resolves names while registering the types they describe.
            registry.Register(typeof(Plain), Info(PlainName));
            Assert.True(registry.TryResolveType(PlainName, out var plain));
            Assert.Equal(typeof(Plain), plain);
        }

        /// <summary>A binder that fails the way SimpleTypeBinder fails a name it cannot find.</summary>
        sealed class ThrowingBinder : ITypeBinder
        {
            public string GetBaseName(Type type) => throw new InvalidOperationException("no name");
            public Type GetTypeFromBase(string baseTypeName) => throw new InvalidOperationException("no such type");
            public Type GetTypeFromBaseAndArguments(string baseTypeName, params Type[] genericTypeArguments)
                => throw new InvalidOperationException("no such type");
        }

        /// <summary>A binder that reports a miss the other legitimate way, by returning null.</summary>
        sealed class NullReturningBinder : ITypeBinder
        {
            public string GetBaseName(Type type) => type.FullName;
            public Type GetTypeFromBase(string baseTypeName) => null;
            public Type GetTypeFromBaseAndArguments(string baseTypeName, params Type[] genericTypeArguments) => null;
        }

        /// <summary>Names <see cref="BinderNamedOther"/> under <see cref="BinderNamed"/>'s manifest
        /// name — the silent mis-binding the seal is asked to refuse.</summary>
        sealed class MisnamingBinder : ITypeBinder
        {
            public string GetBaseName(Type type)
                => type == typeof(BinderNamedOther) ? "NameResolution.BinderNamed" : type.FullName;
            public Type GetTypeFromBase(string baseTypeName) => null;
            public Type GetTypeFromBaseAndArguments(string baseTypeName, params Type[] genericTypeArguments) => null;
        }

        sealed class SingleTypeBinder : ITypeBinder
        {
            readonly Type _type;
            readonly string _name;
            public int Calls;

            public SingleTypeBinder(Type type, string name) { _type = type; _name = name; }

            public string GetBaseName(Type type) => type == _type ? _name : type.FullName;
            public Type GetTypeFromBase(string baseTypeName)
            {
                Calls++;
                return baseTypeName == _name ? _type : null;
            }
            public Type GetTypeFromBaseAndArguments(string baseTypeName, params Type[] genericTypeArguments)
                => throw new NotSupportedException("no generic instantiations in this test");
        }
    }
}
