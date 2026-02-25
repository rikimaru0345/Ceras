using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static Ceras.SourceGenerator.TypeHelpers;

namespace Ceras.SourceGenerator
{
    [Generator]
    public class CerasFormatterGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var provider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => node is TypeDeclarationSyntax { AttributeLists.Count: > 0 },
                    transform: static (ctx, _) => GetTypeIfAnnotated(ctx))
                .Where(static t => t != null)!;

            var compilationAndTypes = context.CompilationProvider.Combine(provider.Collect());

            context.RegisterSourceOutput(compilationAndTypes,
                static (spc, source) => Execute(spc, source.Left, source.Right.Where(t => t != null).Cast<INamedTypeSymbol>().ToImmutableArray()));
        }

        static INamedTypeSymbol? GetTypeIfAnnotated(GeneratorSyntaxContext ctx)
        {
            var typeDecl = (TypeDeclarationSyntax)ctx.Node;
            foreach (var attrList in typeDecl.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    var symbol = ctx.SemanticModel.GetSymbolInfo(attr).Symbol;
                    if (symbol is IMethodSymbol method)
                    {
                        var attrType = method.ContainingType;
                        var attrFqn = attrType.ToDisplayString();
                        if (attrFqn == "Ceras.CerasSerializableAttribute" || 
                            attrFqn == "Ceras.CerasSerializableAttribute<T>")
                        {
                            return ctx.SemanticModel.GetDeclaredSymbol(typeDecl);
                        }
                    }
                }
            }
            return null;
        }

        static void Execute(
            SourceProductionContext context,
            Compilation compilation,
            ImmutableArray<INamedTypeSymbol> types)
        {
            types = types.Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>().ToImmutableArray();

            var generateFmtAttr = compilation.GetTypeByMetadataName(GenerateFormatterFqn);
            var excludeFromDiscoveryAttr = compilation.GetTypeByMetadataName(ExcludeFromDiscoveryFqn);
            var blacklistAttr = compilation.GetTypeByMetadataName(BlacklistFqn);
            var includeAttr = compilation.GetTypeByMetadataName(IncludeFqn);
            var excludeAttr = compilation.GetTypeByMetadataName(ExcludeFqn);
            var memberConfigAttr = compilation.GetTypeByMetadataName(MemberConfigFqn);
            var previousNameAttr = compilation.GetTypeByMetadataName(PreviousNameFqn);
            var previousTypeAttr = compilation.GetTypeByMetadataName(PreviousTypeNameFqn);
            var serializedAsAttr = compilation.GetTypeByMetadataName(SerializedAsFqn);
            var monomorphicAttr = compilation.GetTypeByMetadataName(MonomorphicFqn);
            var polymorphicAttr = compilation.GetTypeByMetadataName(PolymorphicFqn);
            var cerasConstructorAttr = compilation.GetTypeByMetadataName(CerasConstructorFqn);

            var attrBag = new AttributeBag(includeAttr, excludeAttr, memberConfigAttr,
                previousNameAttr, previousTypeAttr, serializedAsAttr, monomorphicAttr,
                polymorphicAttr, generateFmtAttr, cerasConstructorAttr);

            var blacklistPatterns = new List<string>();
            foreach (var attr in compilation.Assembly.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, blacklistAttr) &&
                    attr.ConstructorArguments.Length > 0 &&
                    attr.ConstructorArguments[0].Value is string pattern)
                {
                    blacklistPatterns.Add(pattern);
                }
            }

            var results = DiscoveryEngine.DiscoverAllTypes(
                context, compilation, types, attrBag, excludeFromDiscoveryAttr, blacklistPatterns);

            var finalTypes = results.Keys.ToImmutableArray();

            // Report discovery paths for auto-included types
            foreach (var r in results.Values)
            {
                if (r.DiscoveryPath != null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diag.DiscoveredTypePropagation,
                        r.Type.Locations.FirstOrDefault(),
                        $"Generating a formatter for '{r.Type.Name}' because: {r.DiscoveryPath}"));
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable disable");
            sb.AppendLine("#pragma warning disable 649");
            sb.AppendLine();
            sb.AppendLine("using Ceras;");
            sb.AppendLine("using Ceras.Formatters;");
            sb.AppendLine("using Ceras.Helpers;");
            sb.AppendLine("using Ceras.Format;");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Buffers;");
            sb.AppendLine();
            sb.AppendLine("namespace Ceras.Generated");
            sb.AppendLine("{");

            sb.AppendLine("    public static class GeneratedFormatters");
            sb.AppendLine("    {");
            sb.AppendLine("        public static void UseFormatters(global::Ceras.SerializerConfig config)");
            sb.AppendLine("        {");

            foreach (var type in finalTypes)
            {
                if (type.TypeKind == TypeKind.Interface || type.IsAbstract) continue;
                
                // Only register if we can actually construct it (to avoid broken formatters being used)
                if (!type.IsValueType && !MemberExplorer.CanCerasConstruct(type, cerasConstructorAttr))
                    continue;

                string typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                string formatterName = MakeFormatterClassName(type);
                bool vt = results[type].IsVersionTolerant;
                
                string varName = "tc_" + SanitizeIdentifier(type.ToDisplayString(FullyQualifiedNoGlobal));
                sb.AppendLine($"            var {varName} = config.ConfigType<{typeFqn}>();");
                sb.AppendLine($"            if ({varName}.CustomFormatter == null) {varName}.CustomFormatter = new {formatterName}();");
                
                var bestCtor = MemberExplorer.GetBestConstructor(type, cerasConstructorAttr);
                if (bestCtor != null && bestCtor.Parameters.Length > 0)
                {
                    sb.AppendLine($"            global::Ceras.CerasSerializer.AddFormatterConstructedType(typeof({typeFqn}));");
                }

                if (vt) sb.AppendLine($"            config.Robust.SchemaRegistry.Register(typeof({typeFqn}), {formatterName}.Schema);");
            }

            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            var allPrivateMembersByDeclaringType = new Dictionary<INamedTypeSymbol, List<SerializedMember>>(SymbolEqualityComparer.Default);

            foreach (var type in finalTypes)
            {
                if (type.TypeKind == TypeKind.Interface || type.IsAbstract) continue;

                bool canConstruct = type.IsValueType || MemberExplorer.CanCerasConstruct(type, cerasConstructorAttr);
                if (!canConstruct)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diag.NotConstructable, type.Locations.FirstOrDefault(), type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                    continue;
                }

                MemberExplorer.ValidateIncludeExcludeConflicts(context, type, attrBag);
                var members = MemberExplorer.DiscoverMembers(context, type, attrBag);

                if (members.Count == 0)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diag.NoSerializableMembers, type.Locations.FirstOrDefault(), type.Name));
                }

                foreach (var m in members)
                {
                    if (MemberExplorer.NeedsAccessHelper(m))
                    {
                        if (!SymbolEqualityComparer.Default.Equals(m.DeclaringType.ContainingAssembly, compilation.Assembly))
                        {
                            context.ReportDiagnostic(Diagnostic.Create(Diag.PrivateMemberFromAnotherAssembly, type.Locations.FirstOrDefault(), m.Name, m.DeclaringType.Name));
                            continue;
                        }
                        if (!allPrivateMembersByDeclaringType.TryGetValue(m.DeclaringType, out var list))
                        {
                            list = new List<SerializedMember>();
                            allPrivateMembersByDeclaringType[m.DeclaringType] = list;
                        }
                        if (!list.Any(existing => existing.Name == m.Name)) list.Add(m);
                    }
                }

                MemberExplorer.ValidateCerasConstructor(context, compilation, type, members, attrBag);
                MemberExplorer.ValidateDuplicateWireNames(context, type, members);
                MemberExplorer.ValidatePartialRequirement(context, compilation, type, members);
                MemberExplorer.ValidateReadonlyMembers(context, type, members);

                bool typeVt = results[type].IsVersionTolerant;
                CodeEmitter.WriteFormatterClass(sb, type, members, attrBag, typeVt);
            }

            foreach (var kvp in allPrivateMembersByDeclaringType)
            {
                AccessHelperGenerator.WritePartialClassHelper(context, kvp.Key, kvp.Value);
            }

            sb.AppendLine("}");
            sb.AppendLine("#nullable restore");
            sb.AppendLine("#pragma warning restore 649");

            context.AddSource("CerasGeneratedFormatters.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }
    }
}
