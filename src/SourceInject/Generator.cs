using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace SourceInject;

[Generator]
public class Generator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context)
    {
        const string attribute = @"// <auto-generated />
using Microsoft.Extensions.DependencyInjection;
[System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal class InjectAttribute : System.Attribute
{
    internal InjectAttribute(ServiceLifetime serviceLifetime = ServiceLifetime.Transient) { }
}
[System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal class InjectSingletonAttribute : System.Attribute
{
}
[System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal class InjectScopedAttribute : System.Attribute
{
}
[System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal class InjectTransientAttribute : System.Attribute
{
}
";
        context.RegisterForPostInitialization(context => context.AddSource("Inject.Generated.cs", SourceText.From(attribute, Encoding.UTF8)));
        context.RegisterForSyntaxNotifications(() => new ServicesReceiver());
    }

    public void Execute(GeneratorExecutionContext context)
    {
        var receiver = (ServicesReceiver?)context.SyntaxReceiver;
        if (receiver == null || !receiver.ClassesToRegister.Any())
            return;
        var registrations = new StringBuilder();
        const string spaces = "            ";
        foreach (var clazz in receiver.ClassesToRegister)
        {
            var semanticModel = context.Compilation.GetSemanticModel(clazz.SyntaxTree);
            if (semanticModel == null)
                continue;
            var symbol = semanticModel.GetDeclaredSymbol(clazz);
            if (symbol == null)
                return;
            var lifetime = GetLifetime(symbol.GetAttributes());
            switch (lifetime)
            {
                case Lifetime.Singleton:
                    registrations.Append(spaces);
                    registrations.AppendLine($"services.AddSingleton<{symbol.ToDisplayString(qualifiedFormat)}>();");
                    break;
                case Lifetime.Scoped:
                    registrations.Append(spaces);
                    registrations.AppendLine($"services.AddScoped<{symbol.ToDisplayString(qualifiedFormat)}>();");
                    break;
                case Lifetime.Transient:
                    registrations.Append(spaces);
                    registrations.AppendLine($"services.AddTransient<{symbol.ToDisplayString(qualifiedFormat)}>();");
                    break;
                default:
                    break;
            }
            foreach (var interf in ((ITypeSymbol)symbol).AllInterfaces)
            {
                if (interf.DeclaredAccessibility != Accessibility.Public &&
                    !SymbolEqualityComparer.Default.Equals(interf.ContainingModule, context.Compilation.SourceModule))
                {
                    continue;
                }

                switch (lifetime)
                {
                    case Lifetime.Singleton:
                        registrations.Append(spaces);
                        registrations.AppendLine($"services.AddSingleton<{interf.ToDisplayString(qualifiedFormat)}, {symbol.ToDisplayString(qualifiedFormat)}>();");
                        break;
                    case Lifetime.Scoped:
                        registrations.Append(spaces);
                        registrations.AppendLine($"services.AddScoped<{interf.ToDisplayString(qualifiedFormat)}, {symbol.ToDisplayString(qualifiedFormat)}>();");
                        break;
                    case Lifetime.Transient:
                        registrations.Append(spaces);
                        registrations.AppendLine($"services.AddTransient<{interf.ToDisplayString(qualifiedFormat)}, {symbol.ToDisplayString(qualifiedFormat)}>();");
                        break;
                    default:
                        break;
                }

            }
        }


        ISymbol? methodSymbol = null;
        if (receiver.InvocationSyntaxNode != null)
        {

            var invocationSemanticModel = context.Compilation.GetSemanticModel(receiver.InvocationSyntaxNode.SyntaxTree);
            var methodSyntax = receiver.InvocationSyntaxNode.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            methodSymbol = methodSyntax == null ? null : invocationSemanticModel.GetDeclaredSymbol(methodSyntax);
        }

        if (context.Compilation.AssemblyName == null)
            return;
        var safeAssemblyName = context.Compilation.AssemblyName.Replace(".", "_");
        var extensionCode = $@"
    public static class GeneratedServicesExtension
    {{
        public static void DiscoverIn{safeAssemblyName}(this IServiceCollection services) => services.Discover();
        internal static void Discover(this IServiceCollection services)
        {{
{registrations}        }}
    }}";
        if (methodSymbol == null || methodSymbol.ContainingNamespace.IsGlobalNamespace)
        {
            var newClassCodeBuilder = new StringBuilder();
            foreach (var line in extensionCode.Split(new[] { @"
" }, StringSplitOptions.None))
            {
                if (line.Length > 4 && line.Substring(0, 4) == "    ")
                    newClassCodeBuilder.AppendLine(line.Substring(4, line.Length - 4));
                else
                    newClassCodeBuilder.AppendLine(line);
            }
            extensionCode = newClassCodeBuilder.ToString();
        }
        else
        {
            var ns = methodSymbol.ContainingNamespace.Name.ToString();
            extensionCode = $@"using {ns};

namespace {ns}
{{{extensionCode}
}}
";
        }
        var discovererCode = $@"
public static class {safeAssemblyName}Discoverer
{{
    public static void Discover(IServiceCollection services) => services.Discover();
}}
";
        var finalCode = @"// <auto-generated />
using Microsoft.Extensions.DependencyInjection;
" + extensionCode + discovererCode;
        context.AddSource("GeneratedServicesExtension.Generated.cs", SourceText.From(finalCode, Encoding.UTF8));
    }

    private static Lifetime GetLifetime(IImmutableList<AttributeData> attributes)
    {
        if (attributes.Any(a => a.AttributeClass?.Name == "InjectSingletonAttribute"))
            return Lifetime.Singleton;
        if (attributes.Any(a => a.AttributeClass?.Name == "InjectScopedAttribute"))
            return Lifetime.Scoped;
        if (attributes.Any(a => a.AttributeClass?.Name == "InjectTransientAttribute"))
            return Lifetime.Transient;
        var injectAttribute = attributes.FirstOrDefault(a => a.AttributeClass?.Name == "InjectAttribute");
        if (injectAttribute == null)
            return Lifetime.None;
        var injectArg = injectAttribute.ConstructorArguments.FirstOrDefault();
        if (injectArg.IsNull || injectArg.Kind != TypedConstantKind.Enum || injectArg.Type?.ToString() != "Microsoft.Extensions.DependencyInjection.ServiceLifetime")
            return Lifetime.None;
        return injectArg.Value switch
        {
            1 => Lifetime.Scoped,
            2 => Lifetime.Transient,
            // 0 (singleton) or others
            _ => Lifetime.Singleton,
        };
    }

    private static readonly SymbolDisplayFormat qualifiedFormat = new(globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    enum Lifetime
    {
        None, Singleton, Scoped, Transient
    }
}


