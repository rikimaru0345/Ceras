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

            // 1. Generate the 'UseFormatters' file
            var sbUse = new StringBuilder();
            sbUse.AppendLine("// <auto-generated/>");
            sbUse.AppendLine("#nullable disable");
            sbUse.AppendLine();
            sbUse.AppendLine("using Ceras;");
            sbUse.AppendLine("using Ceras.Formatters;");
            sbUse.AppendLine("using Ceras.Helpers;");
            sbUse.AppendLine("using Ceras.Format;");
            sbUse.AppendLine("using System;");
            sbUse.AppendLine("using System.Collections.Generic;");
            sbUse.AppendLine();
            sbUse.AppendLine("namespace Ceras.Generated");
            sbUse.AppendLine("{");
            sbUse.AppendLine("    public static class GeneratedFormatters");
            sbUse.AppendLine("    {");
            sbUse.AppendLine("        public static void UseFormatters(global::Ceras.SerializerConfig config)");
            sbUse.AppendLine("        {");

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
                sbUse.AppendLine($"            var {varName} = config.ConfigType<{typeFqn}>();");
                sbUse.AppendLine($"            if ({varName}.CustomFormatter == null) {varName}.CustomFormatter = new {formatterName}();");
                
                var bestCtor = MemberExplorer.GetBestConstructor(type, cerasConstructorAttr);
                if (bestCtor != null && bestCtor.Parameters.Length > 0)
                {
                    sbUse.AppendLine($"            global::Ceras.CerasSerializer.AddFormatterConstructedType(typeof({typeFqn}));");
                }

                if (vt) sbUse.AppendLine($"            config.Robust.SchemaRegistry.Register(typeof({typeFqn}), {formatterName}.Schema);");
            }

            sbUse.AppendLine("        }");
            sbUse.AppendLine("    }");
            sbUse.AppendLine("}");

            context.AddSource("CerasGeneratedFormatters.g.cs", SourceText.From(sbUse.ToString(), Encoding.UTF8));


            // 2. Generate per-type formatter files
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

                var sbFmt = new StringBuilder();
                sbFmt.AppendLine("// <auto-generated/>");
                sbFmt.AppendLine("#nullable disable");
                sbFmt.AppendLine("#pragma warning disable 649");
                sbFmt.AppendLine();
                sbFmt.AppendLine("using Ceras;");
                sbFmt.AppendLine("using Ceras.Formatters;");
                sbFmt.AppendLine("using Ceras.Helpers;");
                sbFmt.AppendLine("using Ceras.Format;");
                sbFmt.AppendLine("using System;");
                sbFmt.AppendLine("using System.Collections.Generic;");
                sbFmt.AppendLine("using System.Buffers;");
                sbFmt.AppendLine();
                sbFmt.AppendLine("namespace Ceras.Generated");
                sbFmt.AppendLine("{");

                CodeEmitter.WriteFormatterClass(sbFmt, type, members, attrBag, typeVt);

                sbFmt.AppendLine("}");
                sbFmt.AppendLine("#nullable restore");
                sbFmt.AppendLine("#pragma warning restore 649");

                string typeName = SanitizeIdentifier(type.ToDisplayString(FullyQualifiedNoGlobal));
                context.AddSource($"{typeName}.Ceras.g.cs", SourceText.From(sbFmt.ToString(), Encoding.UTF8));
            }

            foreach (var kvp in allPrivateMembersByDeclaringType)
            {
                AccessHelperGenerator.WritePartialClassHelper(context, kvp.Key, kvp.Value);
            }
        }
    }
}
