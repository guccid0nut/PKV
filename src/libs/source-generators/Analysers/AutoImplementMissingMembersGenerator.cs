// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.SourceGenerator.Analysers;

/// <summary>
///     Generates NotImplementedException-throwing implementations for all unimplemented interface members in classes
///     decorated with <c>[AutoImplementMissingMembers]</c>.
/// </summary>
/// <remarks>
///     This generator inspects each class with the attribute, and determines which interface it implements.
///     Then finds all members not explicitly implemented by the class, and generates stub implementations which throw
///     <see cref="NotImplementedException" />.
/// </remarks>
[Generator]
public class AutoImplementMissingMembersGenerator : IIncrementalGenerator
{
    private static readonly string AttributeFullName = typeof(AutoImplementMissingMembersAttribute).FullName ??
                                                       "ASPIRE.SourceGenerator.AutoImplementMissingMembersAttribute";

    // Diagnostic Descriptors
    private static readonly DiagnosticDescriptor ClassMustBePartialRule = new
    (
        "NX0001",
        "Class Must Be Partial",
        """Class "{0}" has the [AutoImplementMissingMembers] attribute but is not declared as partial. Add the "partial" keyword to the class declaration.""",
        "NEXUS.SourceGenerator",
        DiagnosticSeverity.Error,
        true,
        "Classes decorated with [AutoImplementMissingMembers] must be declared as partial to allow the source generator to add missing interface implementations."
    );

    /// <summary>
    ///     Initializes the incremental source generator by registering syntax providers and source outputs with the specified
    ///     context.
    /// </summary>
    /// <remarks>
    ///     Call this method from the generator's initialization entry point to set up the necessary syntax providers and
    ///     output registrations.
    ///     This method should be invoked only once per generator instance.
    /// </remarks>
    /// <param name="context">The context used to configure the incremental generator and register source outputs.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Identify Classes Annotated With [AutoImplementMissingMembers]
        IncrementalValuesProvider<ClassData> classDeclarations = context.SyntaxProvider // Access Syntax Provider API
            .ForAttributeWithMetadataName( // Filter Nodes By Attribute Metadata Name
                AttributeFullName, // Specify Fully Qualified Attribute Name
                static (syntaxNode, _) =>
                    syntaxNode is ClassDeclarationSyntax, // Restrict Matches To Class Declarations
                static (attributeContext, _) => new ClassData // Create ClassData Wrapper For Match
                (
                    (INamedTypeSymbol)attributeContext.TargetSymbol, // Capture The Class Symbol
                    (ClassDeclarationSyntax)attributeContext.TargetNode) // Capture The Class Syntax Node
            );

        // Register Source Output For Each Discovered Class Declaration
        context.RegisterSourceOutput(classDeclarations, // Provide Sequence Of Annotated Classes
            static (sourceProductionContext, classData) =>
                Execute(classData,
                    sourceProductionContext)); // Register Source Generation Callback Invoked For Each Annotated Class
    }

    /// <summary>
    ///     Analyzes a class annotated with <c>[AutoImplementMissingMembers]</c>, determines interface members not yet
    ///     implemented, enforces the partial class requirement when stubs are needed, and emits a companion partial class
    ///     containing <see cref="NotImplementedException" />-throwing stubs for missing methods and properties.
    /// </summary>
    /// <remarks>
    ///     <code>
    ///         1. Gathers all interface members (methods and properties).
    ///         2. Filters out members already implemented (including explicit interface implementations).
    ///         3. If at least one member is missing and the class is not declared <c>partial</c>, reports diagnostic NX0001 and aborts generation.
    ///         4. Otherwise, generates a source file named <c>{ClassName}.g.cs</c> with a partial class implementing the missing members by throwing <see
    ///             cref="NotImplementedException" />.
    ///     </code>
    /// </remarks>
    /// <param name="classData">Symbol and syntax data for the target class.</param>
    /// <param name="context">Source production context for reporting diagnostics and adding generated source.</param>
    private static void Execute(ClassData classData, SourceProductionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        // Extract Class Symbol
        INamedTypeSymbol classSymbol = classData.Symbol;

        // Extract Class Declaration Syntax
        ClassDeclarationSyntax classDeclaration = classData.Syntax;

        // Check If There Are Unimplemented Members
        ImmutableArray<ISymbol> unimplementedMembers = GetUnimplementedInterfaceMembers(classSymbol);

        // Only Enforce Partial Requirement If There Are Unimplemented Members
        if (!unimplementedMembers.IsEmpty)
        {
            // Check If Class Is Declared As Partial
            bool isPartial = classDeclaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword));

            if (!isPartial)
            {
                // Report Diagnostic Error
                Diagnostic diagnostic = Diagnostic.Create
                (
                    ClassMustBePartialRule,
                    classDeclaration.Identifier.GetLocation(),
                    classSymbol.Name
                );

                context.ReportDiagnostic(diagnostic);

                // Skip Code Generation For This Class
                return;
            }
        }

        // Generate Source Code
        string source = GenerateImplementations(classSymbol, unimplementedMembers);

        // Add Generated Source To Source Production Context
        context.AddSource($"{classSymbol.Name}.g.cs", source);
    }

    /// <summary>
    ///     Generates the partial class with all missing interface member implementations.
    /// </summary>
    private static string GenerateImplementations(INamedTypeSymbol classSymbol,
        ImmutableArray<ISymbol> unimplementedMembers)
    {
        StringBuilder sourceBuilder = new();

        // Add File Header
        sourceBuilder.AppendLine("// <auto-generated/>");
        sourceBuilder.AppendLine();

        // Disable Nullability Warnings
        sourceBuilder.AppendLine("# nullable disable");
        sourceBuilder.AppendLine();

        // Suppress Pragma Warnings
        sourceBuilder.AppendLine(
            "# pragma warning disable SER001, SER002, SER003, SER004, SER005, SER006 // Suppress StackExchange.Redis Experimental API Warnings");
        sourceBuilder.AppendLine();

        // Add Namespace
        string namespaceName = classSymbol.ContainingNamespace.ToDisplayString();
        sourceBuilder.AppendLine($"namespace {namespaceName};");
        sourceBuilder.AppendLine();

        // Start Partial Class
        string accessibility = GetAccessibilityString(classSymbol.DeclaredAccessibility);
        sourceBuilder.AppendLine($"{accessibility} partial class {classSymbol.Name}");
        sourceBuilder.AppendLine("{");

        // Generate Each Missing Member
        foreach (ISymbol member in unimplementedMembers) GenerateMemberImplementation(sourceBuilder, member);

        // Ensure Exactly One Newline Character Before Closing Brace
        string body = sourceBuilder.ToString().TrimEnd('\r', '\n');
        sourceBuilder.Clear();
        sourceBuilder.Append(body);
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("}");

        return sourceBuilder.ToString();
    }

    /// <summary>
    ///     Finds all interface members that are not implemented by the class.
    /// </summary>
    private static ImmutableArray<ISymbol> GetUnimplementedInterfaceMembers(INamedTypeSymbol classSymbol)
    {
        Dictionary<string, ISymbol> uniqueInterfaceMembers = [];
        StringBuilder keyBuilder = new();

        // Deduplicate Interface Members By Name And Signature
        foreach (ISymbol? member in from interfaceSymbol in classSymbol.AllInterfaces
                 from member in interfaceSymbol.GetMembers()
                 where member.Kind is SymbolKind.Method or SymbolKind.Property or SymbolKind.Event
                 select member)
        {
            switch (member)
            {
                // Skip Property Accessor Methods As They Are Generated With The Property
                case IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet }:
                // Skip Event Accessor Methods As They Are Generated With The Event
                case IMethodSymbol { MethodKind: MethodKind.EventAdd or MethodKind.EventRemove }:
                    continue;
            }

            // Create Unique Key From Member Name And Signature
            keyBuilder.Clear();
            keyBuilder.Append(member.Name);
            keyBuilder.Append('|');
            keyBuilder.Append(member.Kind);

            switch (member)
            {
                case IMethodSymbol methodSymbol:
                {
                    keyBuilder.Append('|');

                    for (int i = 0; i < methodSymbol.Parameters.Length; i++)
                    {
                        if (i > 0) keyBuilder.Append(',');

                        keyBuilder.Append(methodSymbol.Parameters[i].Type
                            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                    }

                    if (methodSymbol.IsGenericMethod)
                    {
                        keyBuilder.Append('|');
                        keyBuilder.Append(methodSymbol.Arity);
                    }

                    break;
                }
                case IPropertySymbol propertySymbol:
                    keyBuilder.Append('|');
                    keyBuilder.Append(propertySymbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                    break;
                case IEventSymbol eventSymbol:
                    keyBuilder.Append('|');
                    keyBuilder.Append(eventSymbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                    break;
            }

            string memberKey = keyBuilder.ToString();

            if (!uniqueInterfaceMembers.ContainsKey(memberKey)) uniqueInterfaceMembers[memberKey] = member;
        }

        ImmutableArray<ISymbol> allInterfaceMembers = [.. uniqueInterfaceMembers.Values];

        IEnumerable<ISymbol> unimplemented = allInterfaceMembers // Begin Filtering For Unimplemented Members
            .Where(interfaceMember =>
                !IsImplemented(interfaceMember, classSymbol)); // Exclude Already Implemented Members

        List<ISymbol> finalUniqueMembers = [];

        // Additional Deduplication Using Signature Matching
        foreach (ISymbol member in unimplemented)
        {
            bool isDuplicate = finalUniqueMembers.Any(existingMember =>
                existingMember.Name == member.Name && SignaturesMatch(existingMember, member));

            if (!isDuplicate) finalUniqueMembers.Add(member);
        }

        return [.. finalUniqueMembers]; // Materialize Unimplemented Members Into Immutable Array
    }

    /// <summary>
    ///     Checks if an interface member is already implemented by the class.
    /// </summary>
    private static bool IsImplemented(ISymbol interfaceMember, INamedTypeSymbol classSymbol)
    {
        ISymbol? implementation = classSymbol.FindImplementationForInterfaceMember(interfaceMember);

        // Check Explicit Implementations
        if (implementation is not null && implementation.ContainingType.TypeKind != TypeKind.Interface &&
            !implementation.IsAbstract)
            return true;

        IEnumerable<ISymbol> membersByName = classSymbol.GetMembers(interfaceMember.Name);

        // Check All Members By Name (Including Partial Class Members)
        return membersByName.Where(member => !member.IsImplicitlyDeclared && !member.IsAbstract)
            .Any(member => SignaturesMatch(member, interfaceMember));
    }

    /// <summary>
    ///     Checks if two members have matching signatures.
    /// </summary>
    private static bool SignaturesMatch(ISymbol memberOne, ISymbol memberTwo)
    {
        return memberOne.Kind == memberTwo.Kind && (memberOne, memberTwo) switch
        {
            (IMethodSymbol methodOne, IMethodSymbol methodTwo) =>
                methodOne.Parameters.Length == methodTwo.Parameters.Length &&
                methodOne.Parameters.Zip(methodTwo.Parameters, (parameterOne, parameterTwo) =>
                        SymbolEqualityComparer.Default.Equals(parameterOne.Type, parameterTwo.Type))
                    .All(parametersMatch => parametersMatch),
            (IPropertySymbol propertyOne, IPropertySymbol propertyTwo) =>
                SymbolEqualityComparer.Default.Equals(propertyOne.Type, propertyTwo.Type),
            (IEventSymbol eventOne, IEventSymbol eventTwo) =>
                SymbolEqualityComparer.Default.Equals(eventOne.Type, eventTwo.Type),
            _ => false
        };
    }

    /// <summary>
    ///     Generates the implementation code for a single member.
    /// </summary>
    private static void GenerateMemberImplementation(StringBuilder builder, ISymbol member)
    {
        switch (member)
        {
            case IMethodSymbol method:
                GenerateMethodImplementation(builder, method);
                break;

            case IPropertySymbol property:
                GeneratePropertyImplementation(builder, property);
                break;

            case IEventSymbol eventSymbol:
                GenerateEventImplementation(builder, eventSymbol);
                break;
        }
    }

    /// <summary>
    ///     Generates a method implementation that throws NotImplementedException.
    /// </summary>
    private static void GenerateMethodImplementation(StringBuilder builder, IMethodSymbol method)
    {
        string returnType = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        string parameters = string.Join(", ", method.Parameters.Select(parameter =>
            $"{parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {parameter.Name}"));

        string typeParameters = "";

        // Handle Generic Type Parameters
        if (method.IsGenericMethod)
        {
            string typeParameterList =
                string.Join(", ", method.TypeParameters.Select(typeParameter => typeParameter.Name));

            typeParameters = $"<{typeParameterList}>";
        }

        builder.AppendLine($"    public {returnType} {method.Name}{typeParameters}({parameters})");
        builder.AppendLine("    {");
        builder.AppendLine(
            $"        throw new System.NotImplementedException(@\"Method \"\"{method.Name}\"\" Is Not Implemented In Test Double\");");
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    /// <summary>
    ///     Generates a property implementation that throws NotImplementedException.
    /// </summary>
    private static void GeneratePropertyImplementation(StringBuilder builder, IPropertySymbol property)
    {
        string propertyType = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        builder.AppendLine($"    public {propertyType} {property.Name}");
        builder.AppendLine("    {");

        if (property.GetMethod is not null)
            builder.AppendLine(
                $"        get => throw new System.NotImplementedException(@\"Property \"\"{property.Name}\"\" Getter Is Not Implemented In Test Double\");");

        if (property.SetMethod is not null)
            builder.AppendLine(
                $"        set => throw new System.NotImplementedException(@\"Property \"\"{property.Name}\"\" Setter Is Not Implemented In Test Double\");");

        builder.AppendLine("    }");
        builder.AppendLine();
    }

    /// <summary>
    ///     Generates an event implementation with empty add/remove accessors.
    /// </summary>
    private static void GenerateEventImplementation(StringBuilder builder, IEventSymbol eventSymbol)
    {
        string eventType = eventSymbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        builder.AppendLine($"    public event {eventType}? {eventSymbol.Name}");
        builder.AppendLine("    {");
        builder.AppendLine("        add { }");
        builder.AppendLine("        remove { }");
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    /// <summary>
    ///     Converts accessibility to a string keyword.
    /// </summary>
    private static string GetAccessibilityString(Accessibility accessibility)
    {
        return accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Protected => "protected",
            Accessibility.Private => "private",
            _ => "internal"
        };
    }

    /// <summary>
    ///     Holds class symbol and syntax information for code generation.
    /// </summary>
    private sealed class ClassData(INamedTypeSymbol symbol, ClassDeclarationSyntax syntax)
    {
        public INamedTypeSymbol Symbol { get; } = symbol;

        public ClassDeclarationSyntax Syntax { get; } = syntax;
    }
}
