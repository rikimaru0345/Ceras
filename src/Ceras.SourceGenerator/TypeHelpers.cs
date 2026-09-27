using System;
using System.Collections.Generic;
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
					return true;
			}
			string fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			return fqn == "global::System.Guid" || fqn == "global::System.DateTime" || fqn == "global::System.TimeSpan";
		}

		public static bool IsWellKnownType(ITypeSymbol type) {
			if (type.SpecialType != SpecialType.None) return true; // primitives, string, object, …
			var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			return fqn.StartsWith("global::System.");
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
			if (type.ContainingType != null) {
				AppendDefinitionName(sb, type.ContainingType);
				sb.Append('+');
			}
			else if (type.ContainingNamespace != null && !type.ContainingNamespace.IsGlobalNamespace) {
				sb.Append(type.ContainingNamespace.ToDisplayString());
				sb.Append('.');
			}

			sb.Append(type.MetadataName);
		}

		static void CollectAllTypeArgs(INamedTypeSymbol type, List<ITypeSymbol> args) {
			if (type.ContainingType != null) CollectAllTypeArgs(type.ContainingType, args);
			args.AddRange(type.TypeArguments);
		}

		public static string QuoteString(string s) => s == null ? "null" : $"\"{s}\"";

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
		/// Returns true for types that Ceras handles natively (System.* well-known types) and therefore
		/// don't need a user-side [CerasSerializable] entry in the discovery graph.
		/// </summary>
		public static bool IsWellKnownOrBanned(INamedTypeSymbol type) {
			if (type.SpecialType != SpecialType.None) return true; // primitives, string, object, …
			var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			return fqn.StartsWith("global::System.");
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
