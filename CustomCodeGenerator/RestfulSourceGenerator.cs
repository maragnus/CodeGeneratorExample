using System.Collections.Immutable;
using System.Text;
using CustomCodeGenerator.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static CustomCodeGenerator.Helpers;

namespace CustomCodeGenerator;

[Generator(LanguageNames.CSharp)]
public class RestfulSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types =
            context.SyntaxProvider.CreateSyntaxProvider(HasOurAttribute, GetTypeFromAttribute)
                .Where(t => t != null)
                .Collect();

        context.RegisterSourceOutput(types, GenerateSource!);
    }

    private static bool HasOurAttribute(SyntaxNode syntaxNode, CancellationToken cancellationToken) =>
        syntaxNode is AttributeSyntax attribute
        && attribute.Name switch
        {
            SimpleNameSyntax ins => ins.Identifier.Text,
            QualifiedNameSyntax qns => qns.Right.Identifier.Text,
            _ => null
        } is "AddImplementation" or "AddImplementationAttribute";

    private static void GenerateSource(SourceProductionContext context, ImmutableArray<ITypeSymbol> types)
    {
        if (types.IsDefaultOrEmpty)
            return;

        foreach (var type in types)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            
            SourceText source;
            try
            {
                source = GenerateSourceCode(type);
            }
            catch (Exception ex)
            {
                source = new SourceCodeBuilder().AppendLine("/*").AppendLine(ex.Message).AppendLine(ex.StackTrace!).AppendLine("*/");
            }

            var hintSymbolDisplayFormat = new SymbolDisplayFormat(
                globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                miscellaneousOptions:
                SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

            var hintName = type.ToDisplayString(hintSymbolDisplayFormat)
                .Replace('<', '[')
                .Replace('>', ']');

            context.AddSource($"{hintName}.g.cs", source);
        }
    }

    private static SourceText GenerateSourceCode(ITypeSymbol type)
    {
        var interfaces = GetInterfaces(type);

        var implements = string.Join(", ",
            interfaces.Select(x => x.Interface.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));

        var usings = new HashSet<string>();
        usings.AddRange(
            interfaces.Select(interfaceType => interfaceType.Interface.ContainingNamespace.ToDisplayString()));

        var source = new SourceCodeBuilder();
        source.AppendLine();
        source.AppendLine($"namespace {type.ContainingNamespace};");
        source.AppendLine();
        source.AppendLine($"partial class {type.Name} : {implements}");
        source.OpenBlock();
        source.AppendLine();

        var argBuilder = new StringBuilder();

        foreach (var implementType in interfaces)
        {
            // Get the root uri
            var members = implementType.Interface.GetMembers()
                .Select(x => x as IMethodSymbol).Where(x => x != null);
            foreach (var member in members)
            {
                if (member == null || member.IsStatic || member.MethodKind != MethodKind.Ordinary) continue;

                try
                {
                    // using for Return types
                    usings.AddRange(GetUsings(member.ReturnType));

                    // Extract response type
                    var taskType = member.ReturnType as INamedTypeSymbol ??
                                   throw new InvalidOperationException(
                                       $"Method {member.Name} must have a Task return type");
                    var returnType = taskType.IsGenericType ? taskType.TypeArguments.Single() : null;
                    var returnTypeName = returnType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

                    // using for method Parameters
                    foreach (var argument in member.Parameters)
                    {
                        usings.AddRange(GetUsings(argument.Type));
                    }

                    // List the method arguments
                    var arguments = string.Join(", ", member.Parameters
                        .Select(x => x.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));

                    // OUTPUT - Method declaration
                    source.AppendLine(
                        $@"async {member.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {implementType.Interface.Name}.{member.Name}({arguments})");
                    source.OpenBlock();

                    // Aggregate the argument names
                    var contentArgs = member.Parameters
                        .Select(x => x.ToDisplayParts(SymbolDisplayFormat.MinimallyQualifiedFormat).Last().ToString())
                        .ToHashSet();
                    
                    if (returnType != null)
                    {
                        source.AppendLine(@$"return default;");
                    }
                    else
                    {
                        source.AppendLine(@$"return;");
                    }

                    source.CloseBlock();
                }
                catch (Exception ex)
                {
                    source.AppendLine("/*");
                    source.AppendLine(ex.Message);
                    source.AppendLine(ex.StackTrace ?? "");
                    source.AppendLine("*/");
                    source.CloseBlock();
                    Console.Error.WriteLine(ex);
                }
            }
        }

        source.CloseBlock();

        source.PrependLines(usings.Order()
            .Where(namespaceName => !namespaceName.Contains("global namespace"))
            .Select(namespaceName => $"using {namespaceName};"));
        source.PrependLine();
        source.PrependLine("#nullable enable");

        return source;
    }
}