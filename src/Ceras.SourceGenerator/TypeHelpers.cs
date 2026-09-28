using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Ceras.SourceGenerator {
	internal static class TypeHelpers {
		// --- Attributes ---
		public const string IncludeFqn = "Ceras.IncludeAttribute";
		public const string ExcludeFqn = "Ceras.ExcludeAttribute";
		public const string PreviousNameFqn = "Ceras.PreviousNameAttribute";
		public const string PreviousTypeNameFqn = "Ceras.PreviousTypeNameAttribute";
		public const string SerializedAsFqn = "Ceras.CerasStableNameAttribute";
		public const string CerasSerializableFqn = "Ceras.CerasSerializableAttribute";
		public const string ExcludeFromDiscoveryFqn = "Ceras.ExcludeAttribute";
		public const string BlacklistFqn = "Ceras.CerasGeneratorBlacklistAttribute";
		public const string CerasConstructorFqn = "Ceras.CerasConstructorAttribute";
		public const string OnBeforeSerializeFqn = "Ceras.OnBeforeSerializeAttribute";
		public const string OnAfterSerializeFqn = "Ceras.OnAfterSerializeAttribute";
		public const string OnBeforeDeserializeFqn = "Ceras.OnBeforeDeserializeAttribute";
		public const string OnAfterDeserializeFqn = "Ceras.OnAfterDeserializeAttribute";
		public const string CerasSerializerFqn = "Ceras.CerasSerializer";
		public const string MigrationContextFqn = "Ceras.Format.MigrationContext";

		// --- Naming ---
		public static readonly SymbolDisplayFormat FullyQualifiedNoGlobal = new(
			globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
			typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
			genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
			miscellaneousOptions: SymbolDisplayMiscellaneousOptions.ExpandNullable);

		public static readonly SymbolDisplayFormat SortKeyFormat = new(
			typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
			genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

		public static string MakeFormatterClassName(INamedTypeSymbol type)
			=> SanitizeIdentifier(type.ToDisplayString(FullyQualifiedNoGlobal)) + "Formatter";

		public static string MakeFormatterFieldName(ITypeSymbol type) {
			string raw = SanitizeIdentifier(type.ToDisplayString(FullyQualifiedNoGlobal));
			return "_" + raw + "Formatter";
		}

		/// <summary>
		/// CASE A: detects two <em>distinct</em> member types that map to the same generated formatter
		/// field name. <see cref="SanitizeIdentifier"/> replaces every non-alphanumeric character with
		/// '_', so it is not injective: e.g. <c>A.B_C</c> and the nested <c>A.B.C</c> both sanitize to
		/// <c>A_B_C</c>. The emitters deduplicate fields by name and would silently drop the later one,
		/// leaving Serialize/Deserialize referencing a field typed for the wrong member type. Reporting
		/// an error here is far more actionable than the resulting compile error.
		/// <para>
		/// Must be called with exactly the same member list and the same field-name function the emitter
		/// uses, so the check can never disagree with actual emission. Repeating the same type is fine
		/// (the emitter intentionally emits one field per distinct type).
		/// </para>
		/// </summary>
		public static void ValidateFormatterFieldNameCollisions(SourceProductionContext context, INamedTypeSymbol declaringType, List<SerializedMember> members) {
			var byFieldName = new Dictionary<string, (ITypeSymbol Type, string MemberName)>(StringComparer.Ordinal);

			foreach (var m in members) {
				if (IsFastPrimitive(m.Type)) continue;

				string fieldName = MakeFormatterFieldName(m.Type);
				if (byFieldName.TryGetValue(fieldName, out var prev)) {
					if (!SymbolEqualityComparer.Default.Equals(prev.Type, m.Type)) {
						context.ReportDiagnostic(Diagnostic.Create(
							Diag.FormatterFieldNameCollision,
							m.MemberSymbol?.Locations.FirstOrDefault() ?? declaringType.Locations.FirstOrDefault(),
							declaringType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
							prev.MemberName,
							prev.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
							m.Name,
							m.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
							fieldName));
					}
				}
				else {
					byFieldName[fieldName] = (m.Type, m.Name);
				}
			}
		}

		public static string SanitizeIdentifier(string displayName) {
			var sb = new StringBuilder(displayName.Length);
			foreach (char c in displayName) {
				if (char.IsLetterOrDigit(c)) sb.Append(c);
				else sb.Append('_');
			}
			return sb.ToString();
		}

		public static bool IsFastPrimitive(ITypeSymbol type) {
			switch (type.SpecialType) {
				case SpecialType.System_Boolean:
				case SpecialType.System_Byte:
				case SpecialType.System_SByte:
				case SpecialType.System_Char:
				case SpecialType.System_Int16:
				case SpecialType.System_UInt16:
				case SpecialType.System_Int32:
				case SpecialType.System_UInt32:
				case SpecialType.System_Single:
				case SpecialType.System_Int64:
				case SpecialType.System_UInt64:
				case SpecialType.System_Double:
				case SpecialType.System_Decimal:
					return true;
			}
			string fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			return fqn == "global::System.Guid" || fqn == "global::System.DateTime" || fqn == "global::System.TimeSpan" || fqn == "global::System.DateTimeOffset";
		}

		/// <summary>
		/// True for a type Ceras handles with a built-in formatter/schema of its own, so no generated
		/// formatter and no <c>[CerasSerializable]</c> entry are needed for it. The framework-type
		/// question needs the compilation's attributes: a <c>System.*</c> type can be a user type that its
		/// own author marked serializable, and that must not be mistaken for a built-in shape.
		/// </summary>
		public static bool IsWellKnownType(ITypeSymbol type, AttributeBag? attrs = null) {
			if (type.SpecialType != SpecialType.None) return true; // primitives, string, object, …
			return IsSystemNamespaceType(type, attrs) || IsSystemTupleType(type);
		}

		/// <summary>
		/// True for the <c>System.Tuple</c>/<c>System.ValueTuple</c> families. They are well-known
		/// structural shapes, but a C# tuple type displays as <c>(int, string)</c> rather than
		/// <c>global::System.ValueTuple&lt;int, string&gt;</c>, so the namespace check alone would
		/// mistake them for user types and emit a bogus field-based Struct schema for them
		/// (see the Tuple/ValueTuple/KeyValuePair/Nullable structural shapes).
		/// <para>
		/// The arity is part of the test because the runtime only reserves ids for one through seven
		/// arguments (<c>WellKnownTypes.TupleDefToId</c>); an eight-argument <c>ValueTuple</c> is a type
		/// the format cannot describe, and <see cref="FindUnsupportedFrameworkType"/> reports it rather
		/// than treating it as built-in.
		/// </para>
		/// </summary>
		static bool IsSystemTupleType(ITypeSymbol type) {
			if (type is not INamedTypeSymbol named || !named.IsGenericType) return false;
			var def = named.OriginalDefinition;

			// A type this compilation declares is a user type, so a user type that happens to be called
			// Tuple<…> and to live in the System namespace is not a reserved shape.
			return !IsDeclaredInSource(def) && IsTupleDefinition(GetDefinitionId(def));
		}

		/// <summary>
		/// True when the type is not declared in this compilation and its core named type lives in the
		/// <c>System</c> namespace (or a sub-namespace) — the only case the generator cannot tell apart
		/// from a framework type. Constructed generics are checked through their open definition, because a
		/// special-type alias such as <c>int?</c> does not display as <c>System.Nullable&lt;int&gt;</c> and
		/// would otherwise be mistaken for an ordinary user type and receive a bogus field-less
		/// placeholder schema.
		/// <para>
		/// <b>Two kinds of type are therefore not framework types</b>, and both are excluded before the
		/// namespace is even looked at:
		/// <list type="bullet">
		/// <item>a type the compilation declares in its own sources, whatever namespace it lives in — a
		/// user type in a <c>System.*</c> namespace (e.g. <c>System.Games.Hero</c>) needs a generated
		/// formatter and schema like any other user type. Treating it as well-known silently left it out
		/// of the manifest, so the first robust <c>Save</c> failed with advice to declare the type the
		/// user had just declared;</item>
		/// <item>a type from a referenced assembly that its own author marked <c>[CerasSerializable]</c>.
		/// A framework type cannot carry the attribute, so its presence is positive evidence that this
		/// is a user type — one that lives in another assembly and therefore is not declared in this
		/// compilation's sources, which is the case the source check above cannot see.</item>
		/// </list>
		/// </summary>
		/// <para>
		/// <b>What is left is a namespace heuristic, and only that.</b> Roslyn exposes no way to ask
		/// whether an assembly is a platform/framework one (<c>IAssemblySymbol</c> has no such property, and
		/// the compilation does not separate project references from framework references), so an
		/// unannotated <c>System.*</c> type from a referenced assembly is reported rather than silently
		/// treated as either. The diagnostic says exactly that and points at the
		/// <c>[CerasSerializable]</c> escape hatch instead of claiming the type is a framework type.
		/// </para>
		/// <para>
		/// Every question is asked about one <em>core named type</em> (see
		/// <see cref="GetCoreNamedType"/>), so wrapping a user type in an array or a pointer cannot
		/// change the answer, and the namespace is read from the symbol rather than from its display
		/// string — <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> renders
		/// <c>System.IntPtr</c>/<c>System.UIntPtr</c> as <c>nint</c>/<c>nuint</c>, a display form no
		/// <c>global::System.</c> prefix test can see.
		/// </para>
		/// </summary>
		static bool IsSystemNamespaceType(ITypeSymbol type, AttributeBag? attrs = null) {
			var core = GetCoreNamedType(type);
			if (core == null) return false;                     // a type parameter has no namespace

			// Declared by the compilation itself: the user's own type, whatever namespace.
			if (IsDeclaredInSource(core)) return false;

			// Declared serializable by whoever owns it: a user type from another assembly.
			if (attrs?.CerasSerializable != null && HasAttribute(core, attrs.CerasSerializable)) return false;

			var containing = core.ContainingNamespace;
			if (containing == null || containing.IsGlobalNamespace) return false;

			string name = containing.ToDisplayString();
			return name == "System" || name.StartsWith("System.", StringComparison.Ordinal);
		}

		/// <summary>
		/// The one named type every declaration/namespace question is asked about: a constructed generic's
		/// open definition, or the type an array/pointer holds, however deeply nested. Peeling here (rather
		/// than per question) is what keeps the answer independent of how the type is spelled — a user type
		/// in a <c>System.*</c> namespace must not become a framework type just because a member holds an
		/// array of it.
		/// </summary>
		static INamedTypeSymbol? GetCoreNamedType(ITypeSymbol type) {
			while (true) {
				switch (type) {
					case IArrayTypeSymbol array: type = array.ElementType; continue;
					case IPointerTypeSymbol pointer: type = pointer.PointedAtType; continue;
					case INamedTypeSymbol named: return named.IsGenericType ? named.OriginalDefinition : named;
					default: return null;   // a type parameter, or a function pointer
				}
			}
		}

		/// <summary>
		/// True when the type is declared in the sources of the compilation being analyzed, rather than
		/// coming from a referenced assembly. This is what separates a user type that happens to live in a
		/// <c>System.*</c> namespace from a framework type.
		/// </summary>
		public static bool IsDeclaredInSource(ITypeSymbol type) {
			foreach (var location in type.Locations)
				if (location.IsInSource) return true;
			return false;
		}

		/// <summary>
		/// The metadata identity of a type's generic definition: namespace, then containing types joined
		/// with <c>+</c>, then the metadata name including its arity backtick — e.g.
		/// <c>System.Collections.Generic.List`1</c>. This is the same identity the runtime's
		/// <c>WellKnownTypes</c> tables are keyed by (<c>typeof(List&lt;&gt;)</c>), so the generator can
		/// recognize the reserved shapes without a reference to the runtime assembly.
		/// </summary>
		public static string GetDefinitionId(INamedTypeSymbol named) {
			var sb = new StringBuilder();
			AppendDefinitionId(sb, named);
			return sb.ToString();
		}

		static void AppendDefinitionId(StringBuilder sb, INamedTypeSymbol type) {
			if (type.ContainingType != null) {
				AppendDefinitionId(sb, type.ContainingType);
				sb.Append('+');
			}
			else if (type.ContainingNamespace != null && !type.ContainingNamespace.IsGlobalNamespace) {
				sb.Append(type.ContainingNamespace.ToDisplayString());
				sb.Append('.');
			}

			sb.Append(type.MetadataName);
		}

		/// <summary>
		/// The reserved generic definitions whose instances the format describes as a definition plus
		/// ordered argument type ids: the built-in collections, <c>KeyValuePair</c> and
		/// <c>Nullable</c> (runtime: <c>WellKnownTypes.GenericDefToId</c>).
		/// </summary>
		static readonly HashSet<string> BuiltInGenericDefinitions = new HashSet<string>(StringComparer.Ordinal) {
			"System.Collections.Generic.List`1",
			"System.Collections.Generic.Dictionary`2",
			"System.Collections.Generic.HashSet`1",
			"System.Collections.Generic.Queue`1",
			"System.Collections.Generic.Stack`1",
			"System.Collections.Generic.KeyValuePair`2",
			"System.Nullable`1",
		};

		/// <summary>
		/// True for the reserved <c>Tuple</c>/<c>ValueTuple</c> definitions the format has ids for, which
		/// is arity one through seven (runtime: <c>WellKnownTypes.TupleDefToId</c>).
		/// </summary>
		public static bool IsTupleDefinition(string definitionId) {
			const string tuplePrefix = "System.Tuple`";
			const string valueTuplePrefix = "System.ValueTuple`";

			int start;
			if (definitionId.StartsWith(valueTuplePrefix, StringComparison.Ordinal)) start = valueTuplePrefix.Length;
			else if (definitionId.StartsWith(tuplePrefix, StringComparison.Ordinal)) start = tuplePrefix.Length;
			else return false;

			// The arity is the whole digit run after the backtick; a nested type ("System.Tuple`2+Item")
			// is not a tuple definition and fails here.
			int arity = 0;
			for (int i = start; i < definitionId.Length; i++) {
				if (!char.IsDigit(definitionId[i])) return false;
				arity = arity * 10 + (definitionId[i] - '0');
			}

			return arity >= 1 && arity <= 7;
		}

		/// <summary>
		/// True for a type the robust format has a built-in representation for without any generated
		/// formatter or schema: everything the bake registers structurally on its own (see
		/// <c>Ceras.Format.SchemaSection.TryGetAutoRegistration</c> and
		/// <c>Ceras.Format.SchemaRegistry.TryBuildStructuralSchema</c>). Every branch below names the
		/// runtime table it mirrors, so the two classifications cannot silently disagree:
		/// <list type="bullet">
		/// <item>an enum — its value table is derived from CLR metadata at declaration time;</item>
		/// <item>the reserved primitive/string/object ids, enumerated explicitly rather than "any
		/// <c>SpecialType</c>", because <c>SpecialType</c> also covers <c>IntPtr</c>/<c>UIntPtr</c>,
		/// which have no reserved id and therefore no describable payload;</item>
		/// <item>the fixed-width BCL value types (<c>Guid</c>, <c>DateTime</c>, <c>DateTimeOffset</c>,
		/// <c>TimeSpan</c>), <see cref="System.Type"/>, and <see cref="System.Array"/> itself;</item>
		/// <item>a one-dimensional array (<c>T[]</c> with one element argument);</item>
		/// <item>a closed instantiation of a reserved generic definition — the built-in collections,
		/// <c>KeyValuePair</c>, <c>Nullable</c>, and the <c>Tuple</c>/<c>ValueTuple</c> families;</item>
		/// <item>a collection the built-in collection formatter serves (a <c>List&lt;T&gt;</c> or
		/// <c>Dictionary&lt;K,V&gt;</c> subclass), which is described by its element/key/value shape.</item>
		/// </list>
		/// A pure predicate: it never throws and never reports a diagnostic.
		/// </summary>
		public static bool IsDescribableBuiltinShape(ITypeSymbol type) {
			// Enums: SchemaRegistry.BuildDeclaredSchema derives the value table from CLR metadata.
			if (type.TypeKind == TypeKind.Enum) return true;

			switch (type.SpecialType) {
				// WellKnownTypes.TypeToId — the reserved primitive, string and object ids.
				case SpecialType.System_Boolean:
				case SpecialType.System_Byte:
				case SpecialType.System_SByte:
				case SpecialType.System_Char:
				case SpecialType.System_Int16:
				case SpecialType.System_UInt16:
				case SpecialType.System_Int32:
				case SpecialType.System_UInt32:
				case SpecialType.System_Single:
				case SpecialType.System_Int64:
				case SpecialType.System_UInt64:
				case SpecialType.System_Double:
				case SpecialType.System_Decimal:
				case SpecialType.System_String:
				case SpecialType.System_Object:
					return true;
			}

			// The fixed-width BCL value types, which IsFastPrimitive also recognizes.
			if (IsFastPrimitive(type)) return true;

			if (type is not INamedTypeSymbol named) {
				// A one-dimensional array is described as T[] with a single element argument. A higher
				// rank is not representable at all (see MemberExplorer.IsUnsupportedMultidimensionalArray,
				// which reports it), and a pointer has no representation.
				return type is IArrayTypeSymbol { Rank: 1 };
			}

			var definitionId = GetDefinitionId(named);

			// System.Type is a reserved id, written inline by the robust type formatter, and
			// typeof(Array) is a reserved id too.
			if (definitionId == "System.Type" || definitionId == "System.Array") return true;

			if (named.IsGenericType && !named.IsUnboundGenericType) {
				string openId = GetDefinitionId(named.OriginalDefinition);
				if (BuiltInGenericDefinitions.Contains(openId)) return true;
				if (IsTupleDefinition(openId)) return true;
			}

			// A collection the built-in collection formatter serves is described by the
			// List<>/Dictionary<>/HashSet<> shape of its element/key/value types.
			return IsDescribableCollectionShape(named);
		}

		/// <summary>
		/// True for a collection the bake can describe by the element/key/value shape it resolves for it
		/// (runtime: <c>WellKnownTypes.TryResolveCollectionShape</c> / <c>IsCollectionFormatterShape</c>).
		/// <para>
		/// This is deliberately narrower than <see cref="MemberExplorer.IsCollectionType"/>, which also
		/// matches the <em>non-generic</em> <c>ICollection</c>/<c>IDictionary</c> interfaces. The runtime
		/// resolves a shape from a <em>closed</em> <c>ICollection&lt;T&gt;</c>/<c>IDictionary&lt;K,V&gt;</c>
		/// (found on the type itself, its base chain or its interface set) or from a
		/// <c>HashSet&lt;&gt;</c>/<c>Queue&lt;&gt;</c>/<c>Stack&lt;&gt;</c> base, and nothing else — so a
		/// <c>List&lt;T&gt;</c> subclass, an <c>IList&lt;T&gt;</c> and an <c>ICollection&lt;T&gt;</c> qualify
		/// while <c>ArrayList</c>, <c>Hashtable</c> and a bare <c>IEnumerable&lt;T&gt;</c> do not. Using the
		/// broader predicate here would promise support the bake cannot keep.
		/// </para>
		/// </summary>
		public static bool IsDescribableCollectionShape(INamedTypeSymbol type) {
			if (type.IsUnboundGenericType) return false;
			if (type.IsGenericType && !IsOpenType(type) &&
				BuiltInGenericDefinitions.Contains(GetDefinitionId(type.OriginalDefinition)))
				return true;   // List<>/Dictionary<>/HashSet<>/Queue<>/Stack<> itself, keeping its own definition

			return FindsClosedGeneric(type, "System.Collections.Generic.IDictionary`2")
				|| FindsBaseOf(type, "System.Collections.Generic.HashSet`1")
				|| FindsClosedGeneric(type, "System.Collections.Generic.ICollection`1")
				|| FindsBaseOf(type, "System.Collections.Generic.Queue`1")
				|| FindsBaseOf(type, "System.Collections.Generic.Stack`1");
		}

		/// <summary>
		/// A closed instantiation of <paramref name="definitionId"/> on the type itself, on its base
		/// chain, or anywhere in its interface set (runtime: <c>WellKnownTypes.FindClosedInterface</c>).
		/// </summary>
		static bool FindsClosedGeneric(INamedTypeSymbol type, string definitionId) {
			for (var t = type; t != null; t = t.BaseType)
				if (t.IsGenericType && !IsOpenType(t) && GetDefinitionId(t.OriginalDefinition) == definitionId)
					return true;

			foreach (var iface in type.AllInterfaces)
				if (iface.IsGenericType && GetDefinitionId(iface.OriginalDefinition) == definitionId)
					return true;

			return false;
		}

		/// <summary>
		/// A closed instantiation of <paramref name="definitionId"/> on the type itself or its base
		/// chain (runtime: <c>WellKnownTypes.FindBase</c>).
		/// </summary>
		static bool FindsBaseOf(INamedTypeSymbol type, string definitionId) {
			for (var t = type; t != null; t = t.BaseType)
				if (t.IsGenericType && !IsOpenType(t) && GetDefinitionId(t.OriginalDefinition) == definitionId)
					return true;

			return false;
		}

		/// <summary>
		/// True for a type the robust format has no built-in representation for and that the generator
		/// therefore cannot give a formatter or a schema: not declared in this compilation, attributed to
		/// no author (no <c>[CerasSerializable]</c>), living in a <c>System.*</c> namespace, and not one of
		/// the shapes <see cref="IsDescribableBuiltinShape"/> accepts (<c>System.Uri</c>,
		/// <c>System.Version</c>, <c>System.Numerics.BigInteger</c>, <c>IntPtr</c>, an eight-argument
		/// <c>ValueTuple</c>, …).
		/// <para>
		/// Such a type used to be reported as "it uses a built-in formatter" and then silently omitted from
		/// the generated manifest, so the first robust <c>Save</c> failed at the bake with advice — declare
		/// the type, add <c>[CerasKnownType]</c>, register a custom formatter — that cannot work for a
		/// type the format cannot describe. A type outside the <c>System.*</c> namespaces is deliberately
		/// not covered here: it keeps the existing minimal-registration/custom-formatter path, which is a
		/// different question (and a temporary one, see the robust custom formatter rejection).
		/// </para>
		/// </summary>
		public static bool IsUnsupportedFrameworkType(ITypeSymbol type, AttributeBag? attrs = null)
			=> IsSystemNamespaceType(type, attrs) && !IsDescribableBuiltinShape(type);

		/// <summary>
		/// The collection shapes the project owner has decided Ceras does not support, by symbol
		/// (the generator-side mirror of <c>Ceras.Format.UnsupportedCollectionPolicy</c>, which is the
		/// runtime's single answer).
		/// <para>
		/// The rule is base-<em>type identity</em>, never a name and never a namespace, and it is not a
		/// blanket inheritance ban: the exact <c>List&lt;T&gt;</c> and
		/// <c>Dictionary&lt;TKey,TValue&gt;</c> are supported shapes and only a <em>derived</em> type is
		/// refused. <c>ReadOnlyCollection&lt;T&gt;</c> and <c>ReadOnlyDictionary&lt;K,V&gt;</c> are refused
		/// wherever a value would have to be produced, the built-in type included.
		/// </para>
		/// <para>
		/// An interface is never refused, and that is deliberate rather than incidental. The
		/// <c>IReadOnly*</c> interfaces are ordinary API signatures (a property returning
		/// <c>IReadOnlyList&lt;T&gt;</c>, a method taking an <c>IReadOnlyDictionary&lt;K,V&gt;</c>) and are not
		/// collection shapes at all: the runtime classifier resolves a shape from a <em>class</em>'s closed
		/// <c>ICollection&lt;T&gt;</c>/<c>IDictionary&lt;K,V&gt;</c> or its <c>HashSet&lt;&gt;</c>/<c>Queue&lt;&gt;</c>/
		/// <c>Stack&lt;&gt;</c> base, and <c>IsCollectionFormatterShape</c> rejects interfaces outright. A
		/// name-shaped test would reject hundreds of ordinary declarations that never reach a formatter.
		/// </para>
		/// </summary>
		public static bool IsUnsupportedCollectionType(ITypeSymbol type) {
			if (type is not INamedTypeSymbol named) return false;
			if (type.TypeKind == TypeKind.Interface) return false;   // IReadOnly*/ICollection<>/IList<> are API, not shapes
			if (named.IsUnboundGenericType) return false;           // an open `List<>` names no element type at all

			for (var t = named; t != null; t = t.BaseType) {
				if (!t.IsGenericType) continue;
				string id = GetDefinitionId(t.OriginalDefinition);

				if (id == "System.Collections.ObjectModel.ReadOnlyCollection`1"
					|| id == "System.Collections.ObjectModel.ReadOnlyDictionary`2")
					return true;

				// `t != named` is what says "derived", which is the whole difference between the supported
				// built-in type and a refused subclass of it.
				if (!SymbolEqualityComparer.Default.Equals(t, named)
					&& (id == "System.Collections.Generic.List`1"
						|| id == "System.Collections.Generic.Dictionary`2"))
					return true;
			}

			return false;
		}

		/// <summary>
		/// The first type reachable from <paramref name="type"/> through the shapes the format describes
		/// structurally — an array element, a pointer target, and the argument types of a built-in
		/// collection, <c>KeyValuePair</c>, <c>Nullable</c> or a tuple — that
		/// <see cref="IsUnsupportedCollectionType"/> rejects, or null when every one of them is supported.
		/// <para>
		/// The structural shapes are exactly the ones the bake expands into further type ids, so a refused
		/// type hidden inside one (<c>List&lt;ReadOnlyCollection&lt;int&gt;&gt;</c>,
		/// <c>ReadOnlyCollection&lt;int&gt;[]</c>) is found here rather than only at the bake — and found
		/// much earlier: the member is dropped and the build fails with the type named, instead of a
		/// context that only refuses on its first <c>Save</c>.
		/// </para>
		/// <para>
		/// A user type is never descended into, for the same reason
		/// <see cref="FindUnsupportedFrameworkType"/> does not: it gets a generated schema whose own fields
		/// are checked when that type's own members are walked, so this terminates and reports nothing twice.
		/// </para>
		/// </summary>
		public static ITypeSymbol? FindUnsupportedCollectionType(ITypeSymbol type) {
			if (IsUnsupportedCollectionType(type)) return type;

			switch (type) {
				case IArrayTypeSymbol array:
					return FindUnsupportedCollectionType(array.ElementType);

				case IPointerTypeSymbol pointer:
					return FindUnsupportedCollectionType(pointer.PointedAtType);

				case INamedTypeSymbol named when named.IsGenericType && !named.IsUnboundGenericType: {
					string openId = GetDefinitionId(named.OriginalDefinition);
					if (!BuiltInGenericDefinitions.Contains(openId) && !IsTupleDefinition(openId)) return null;
					foreach (var argument in named.TypeArguments) {
						var offending = FindUnsupportedCollectionType(argument);
						if (offending != null) return offending;
					}
					return null;
				}

				default:
					return null;
			}
		}

		/// <summary>
		/// The first type reachable from <paramref name="type"/> through the shapes the format describes
		/// structurally — an array element, a pointer target, and the argument types of a built-in
		/// collection, <c>KeyValuePair</c>, <c>Nullable</c> or a tuple — that
		/// <see cref="IsUnsupportedFrameworkType"/> rejects, or null when every one of them is
		/// describable. The structural shapes are exactly the ones the bake expands into further type
		/// ids, so a framework type hidden inside one (<c>List&lt;Uri&gt;</c>, <c>Uri[]</c>) is found by
		/// the same walk that would otherwise fail at the bake.
		/// <para>
		/// A user type is never descended into: it gets a generated schema whose own fields are checked
		/// when that type's own members are walked, so this terminates and does not report a type twice.
		/// </para>
		/// </summary>
		public static ITypeSymbol? FindUnsupportedFrameworkType(ITypeSymbol type, AttributeBag? attrs = null) {
			if (IsUnsupportedFrameworkType(type, attrs)) return type;

			switch (type) {
				case IArrayTypeSymbol array:
					return FindUnsupportedFrameworkType(array.ElementType, attrs);

				case IPointerTypeSymbol pointer:
					return FindUnsupportedFrameworkType(pointer.PointedAtType, attrs);

				case INamedTypeSymbol named when named.IsGenericType && !named.IsUnboundGenericType: {
					string openId = GetDefinitionId(named.OriginalDefinition);
					if (!BuiltInGenericDefinitions.Contains(openId) && !IsTupleDefinition(openId)) return null;
					foreach (var argument in named.TypeArguments) {
						var offending = FindUnsupportedFrameworkType(argument, attrs);
						if (offending != null) return offending;
					}
					return null;
				}

				default:
					return null;
			}
		}

		public static string GetRobustName(ITypeSymbol type) {
			var sb = new StringBuilder();
			BuildRobustName(sb, type);
			return sb.ToString();
		}

		/// <summary>
		/// Canonical stable identity: namespace/nested metadata name plus ordered generic arguments
		/// (containing type arguments first). This must match the runtime's
		/// <c>Ceras.Format.TypeIdentity.GetCanonicalName</c> exactly (see TODO Phase 2.3).
		/// </summary>
		static void BuildRobustName(StringBuilder sb, ITypeSymbol type) {
			switch (type) {
				case IArrayTypeSymbol arr:
					BuildRobustName(sb, arr.ElementType);
					sb.Append('[');
					for (int i = 1; i < arr.Rank; i++) sb.Append(',');
					sb.Append(']');
					return;
				case IPointerTypeSymbol ptr:
					BuildRobustName(sb, ptr.PointedAtType);
					sb.Append('*');
					return;
				case ITypeParameterSymbol tp:
					sb.Append('!').Append(tp.Ordinal);
					return;
			}

			if (type is INamedTypeSymbol named) {
				if (named.IsGenericType && !IsOpenType(named)) {
					AppendDefinitionName(sb, named.OriginalDefinition);
					var args = new List<ITypeSymbol>();
					CollectAllTypeArgs(named, args);
					sb.Append('<');
					for (int i = 0; i < args.Count; i++) {
						if (i > 0) sb.Append(',');
						BuildRobustName(sb, args[i]);
					}
					sb.Append('>');
					return;
				}

				AppendDefinitionName(sb, named);
				return;
			}

			sb.Append(type.MetadataName);
		}

		static void AppendDefinitionName(StringBuilder sb, ITypeSymbol type) {
			// The canonical name of a named type is exactly its definition id, so the two cannot drift.
			AppendDefinitionId(sb, (INamedTypeSymbol)type);
		}

		static void CollectAllTypeArgs(INamedTypeSymbol type, List<ITypeSymbol> args) {
			if (type.ContainingType != null) CollectAllTypeArgs(type.ContainingType, args);
			args.AddRange(type.TypeArguments);
		}

		/// <summary>
		/// The <b>only</b> way the generator writes a string literal into generated C#. Every literal
		/// that carries a string — a <c>[CerasSerializable]</c> stable name, a
		/// <c>[PreviousTypeName]</c>/<c>[PreviousName]</c> alias, a <c>[CerasStableName]</c>, a
		/// <c>[CerasContext]</c> name, a canonical CLR identity, an enum member name, an
		/// <c>[UnsafeAccessor(Name = ...)]</c> member name — goes through here, so no call site can
		/// accidentally emit an unescaped value.
		/// <para>
		/// Almost every value that reaches this is fully user-controlled: an attribute argument is
		/// arbitrary text, and <c>$"\"{s}\""</c> turns a <c>"</c> into a compile error, a <c>\</c> into a
		/// silently different string, a newline/control character into a silently different string or a
		/// broken file, and an unpaired surrogate into U+FFFD once the file is encoded as UTF-8.
		/// <c>SymbolDisplay.FormatLiteral</c> escapes exactly those cases.
		/// </para>
		/// <para>
		/// <b>Output stability:</b> for every value without a quote, backslash, control character or
		/// unpaired surrogate — i.e. every ordinary metadata name and identifier — the result is
		/// byte-identical to the naive <c>"s"</c> spelling, including for non-ASCII identifiers (they stay
		/// raw, exactly as before). The file's own encoding is untouched: generated sources are still
		/// added as <c>SourceText.From(text, Encoding.UTF8)</c>, and printable non-ASCII survives that
		/// round trip unchanged, so the runtime value of a name is never altered by quoting.
		/// </para>
		/// <para>
		/// <paramref name="s"/> is nullable so that a genuinely absent value can be emitted as the C#
		/// <c>null</c> literal; every current call site passes a non-null string.
		/// </para>
		/// </summary>
		public static string QuoteLiteral(string? s)
			=> s == null ? "null" : Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(s, quote: true);

		public static string NormalizeName(string name) {
			if (string.IsNullOrEmpty(name)) return name;
			string norm = name.ToLowerInvariant();
			if (norm.StartsWith("_")) norm = norm.Substring(1);
			return norm;
		}

		// --- Attribute Argument Helpers ---
		public static bool TryGetAttributeArgument<T>(AttributeData attr, string argumentName, out T value) {
			// 1. Check named arguments
			foreach (var na in attr.NamedArguments) {
				if (na.Key == argumentName) {
					if (na.Value.Value == null) {
						value = default!;
						return true;
					}

					if (na.Value.Value is T val) {
						value = val;
						return true;
					}

					// Handle enums and other integral conversions
					try {
						value = (T)Convert.ChangeType(na.Value.Value.ToString(), typeof(T));
						return true;
					}
					catch { }
				}
			}

			// 2. Check constructor arguments (mapped by name)
			var ctor = attr.AttributeConstructor;
			if (ctor != null) {
				for (int i = 0; i < ctor.Parameters.Length; i++) {
					if (ctor.Parameters[i].Name == argumentName ||
						NormalizeAttributeParameterName(ctor.Parameters[i].Name) == argumentName) {
						if (i < attr.ConstructorArguments.Length) {
							var arg = attr.ConstructorArguments[i];
							if (arg.Value == null) {
								value = default!;
								return true;
							}

							if (arg.Value is T val) {
								value = val;
								return true;
							}

							// Handle enums and other integral conversions
							try {
								value = (T)Convert.ChangeType(arg.Value.ToString(), typeof(T));
								return true;
							}
							catch { }
						}
					}
				}
			}

			value = default!;
			return false;
		}

		static string NormalizeAttributeParameterName(string? name) {
			if (name == null) return "";
			// Some attributes might have parameters like 'targetMembers' matching property 'TargetMembers'
			if (name.Length > 0)
				return char.ToUpperInvariant(name[0]) + name.Substring(1);
			return name;
		}

		// --- Semantic Checks ---
		public static bool HasAttribute(ISymbol symbol, INamedTypeSymbol? attributeType) {
			if (attributeType == null) return false;
			foreach (var attr in symbol.GetAttributes()) {
				if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeType))
					return true;
			}
			return false;
		}

		/// <summary>
		/// Returns true if the given type or any of its base types or interfaces has the specified attribute.
		/// </summary>
		public static bool HasAttributeInHierarchy(ITypeSymbol type, INamedTypeSymbol? attributeType) {
			if (attributeType == null) return false;

			var current = type;
			while (current != null) {
				if (HasAttribute(current, attributeType)) return true;
				current = current.BaseType;
			}

			foreach (var iface in type.AllInterfaces) {
				if (HasAttribute(iface, attributeType)) return true;
			}

			return false;
		}

		/// <summary>
		/// Peels away Nullable&lt;T&gt;, arrays, and well-known generic collections (List&lt;T&gt;, HashSet&lt;T&gt;,
		/// Dictionary&lt;,&gt;, Queue&lt;T&gt;, Stack&lt;T&gt;) to get to the element/value type that Ceras actually
		/// needs a polymorphic formatter for.  Returns null if the type cannot be meaningfully unwrapped.
		/// </summary>
		public static INamedTypeSymbol? UnwrapCollectionsAndNullable(Compilation compilation, ITypeSymbol type) {
			// Unwrap arrays: T[] -> T
			if (type is IArrayTypeSymbol arr)
				return UnwrapCollectionsAndNullable(compilation, arr.ElementType);

			if (type is not INamedTypeSymbol named) return null;

			// Nullable<T> -> T
			if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && named.TypeArguments.Length == 1)
				return UnwrapCollectionsAndNullable(compilation, named.TypeArguments[0]);

			// Well-known single-element generic collections: List<T>, HashSet<T>, Queue<T>, Stack<T>
			if (named.IsGenericType && named.TypeArguments.Length == 1) {
				var listDef = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
				var hashSetDef = compilation.GetTypeByMetadataName("System.Collections.Generic.HashSet`1");
				var queueDef = compilation.GetTypeByMetadataName("System.Collections.Generic.Queue`1");
				var stackDef = compilation.GetTypeByMetadataName("System.Collections.Generic.Stack`1");

				var def = named.OriginalDefinition;
				if (SymbolEqualityComparer.Default.Equals(def, listDef) ||
					SymbolEqualityComparer.Default.Equals(def, hashSetDef) ||
					SymbolEqualityComparer.Default.Equals(def, queueDef) ||
					SymbolEqualityComparer.Default.Equals(def, stackDef)) {
					return UnwrapCollectionsAndNullable(compilation, named.TypeArguments[0]);
				}
			}

			// Dictionary<K,V>: unwrap the value type (keys are rarely polymorphic user types)
			if (named.IsGenericType && named.TypeArguments.Length == 2) {
				var dictDef = compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2");
				if (SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, dictDef)) {
					return UnwrapCollectionsAndNullable(compilation, named.TypeArguments[1]);
				}
			}

			return named;
		}

		/// <summary>
		/// Returns true for types that Ceras handles natively (the built-in <c>System</c> shapes) and
		/// therefore don't need a user-side [CerasSerializable] entry in the discovery graph. See
		/// <see cref="IsSystemNamespaceType"/> for why a type declared in this compilation, or marked
		/// serializable by its author, is never one of them.
		/// </summary>
		public static bool IsWellKnownOrBanned(INamedTypeSymbol type, AttributeBag? attrs = null) {
			if (type.SpecialType != SpecialType.None) return true; // primitives, string, object, …
			return IsSystemNamespaceType(type, attrs);
		}

		/// <summary>
		/// True when the type still contains type parameters (an open generic definition such as
		/// <c>Foo&lt;T&gt;</c>, or a partially constructed nested type). Formatters are only generated for
		/// closed instantiations; an open definition has no nameable non-generic formatter type.
		/// </summary>
		public static bool IsOpenType(ITypeSymbol type) {
			switch (type) {
				case ITypeParameterSymbol _: return true;
				case IArrayTypeSymbol a: return IsOpenType(a.ElementType);
				case IPointerTypeSymbol p: return IsOpenType(p.PointedAtType);
				case INamedTypeSymbol n:
					if (n.ContainingType != null && IsOpenType(n.ContainingType)) return true;
					foreach (var arg in n.TypeArguments)
						if (IsOpenType(arg)) return true;
					return false;
				default: return false;
			}
		}
	}
}
