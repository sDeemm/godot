using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

// TODO:
//   Determine a proper way to emit the signal.
//   'Emit(nameof(TheEvent))' creates a StringName everytime and has the overhead of string marshaling.
//   I haven't decided on the best option yet. Some possibilities:
//     - Expose the generated StringName fields to the user, for use with 'Emit(...)'.
//     - Generate a 'EmitSignalName' method for each event signal.

namespace Godot.SourceGenerators
{
    [Generator]
    public class ScriptSignalsGenerator : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context)
        {
        }

        public void Execute(GeneratorExecutionContext context)
        {
            if (context.AreGodotSourceGeneratorsDisabled())
                return;

            INamedTypeSymbol[] godotClasses = context
                .Compilation.SyntaxTrees
                .SelectMany(tree =>
                    tree.GetRoot().DescendantNodes()
                        .OfType<ClassDeclarationSyntax>()
                        .SelectGodotScriptClasses(context.Compilation)
                        // Report and skip non-partial classes
                        .Where(x =>
                        {
                            if (x.cds.IsPartial())
                            {
                                if (x.cds.IsNested() && !x.cds.AreAllOuterTypesPartial(out var typeMissingPartial))
                                {
                                    Common.ReportNonPartialGodotScriptOuterClass(context, typeMissingPartial!);
                                    return false;
                                }

                                return true;
                            }

                            Common.ReportNonPartialGodotScriptClass(context, x.cds, x.symbol);
                            return false;
                        })
                        .Select(x => x.symbol)
                )
                .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
                .ToArray();

            if (godotClasses.Length > 0)
            {
                var typeCache = new MarshalUtils.TypeCache(context.Compilation);

                foreach (var godotClass in godotClasses)
                {
                    VisitGodotScriptClass(context, typeCache, godotClass);
                }
            }
        }

        internal static string SignalDelegateSuffix = "EventHandler";

        private static void VisitGodotScriptClass(
            GeneratorExecutionContext context,
            MarshalUtils.TypeCache typeCache,
            INamedTypeSymbol symbol
        )
        {
            INamespaceSymbol namespaceSymbol = symbol.ContainingNamespace;
            string classNs = namespaceSymbol != null && !namespaceSymbol.IsGlobalNamespace ?
                namespaceSymbol.FullQualifiedName() :
                string.Empty;
            bool hasNamespace = classNs.Length != 0;

            bool isInnerClass = symbol.ContainingType != null;

            string uniqueHint = symbol.FullQualifiedName().SanitizeQualifiedNameForUniqueHint()
                                + "_ScriptSignals_Generated";

            var source = new StringBuilder();

            source.Append("using Godot;\n");
            source.Append("using Godot.NativeInterop;\n");
            source.Append("\n");

            if (hasNamespace)
            {
                source.Append("namespace ");
                source.Append(classNs);
                source.Append(" {\n\n");
            }

            if (isInnerClass)
            {
                var containingType = symbol.ContainingType;

                while (containingType != null)
                {
                    source.Append("partial ");
                    source.Append(containingType.GetDeclarationKeyword());
                    source.Append(" ");
                    source.Append(containingType.NameWithTypeParameters());
                    source.Append("\n{\n");

                    containingType = containingType.ContainingType;
                }
            }

            source.Append("partial class ");
            source.Append(symbol.NameWithTypeParameters());
            source.Append("\n{\n");

            // TODO:
            // The delegate name already needs to end with 'Signal' to avoid collision with the event name.
            // Requiring SignalAttribute is redundant. Should we remove it to make declaration shorter?

            var members = symbol.GetMembers();

            var signalDelegateSymbols = members
                .Where(s => s.Kind == SymbolKind.NamedType)
                .Cast<INamedTypeSymbol>()
                .Where(namedTypeSymbol => namedTypeSymbol.TypeKind == TypeKind.Delegate)
                .Where(s => s.GetAttributes()
                    .Any(a => a.AttributeClass?.IsGodotSignalAttribute() ?? false));

            List<GodotSignalDelegateData> godotSignalDelegates = new();

            foreach (var signalDelegateSymbol in signalDelegateSymbols)
            {
                if (!signalDelegateSymbol.Name.EndsWith(SignalDelegateSuffix))
                {
                    Common.ReportSignalDelegateMissingSuffix(context, signalDelegateSymbol);
                    continue;
                }

                string signalName = signalDelegateSymbol.Name;
                signalName = signalName.Substring(0, signalName.Length - SignalDelegateSuffix.Length);

                var invokeMethodData = signalDelegateSymbol
                    .DelegateInvokeMethod?.HasGodotCompatibleSignature(typeCache);

                if (invokeMethodData == null)
                {
                    if (signalDelegateSymbol.DelegateInvokeMethod is IMethodSymbol methodSymbol)
                    {
                        foreach (var parameter in methodSymbol.Parameters)
                        {
                            if (parameter.RefKind != RefKind.None)
                            {
                                Common.ReportSignalParameterTypeNotSupported(context, parameter);
                                continue;
                            }

                            var marshalType = MarshalUtils.ConvertManagedTypeToMarshalType(parameter.Type, typeCache);

                            if (marshalType == null)
                            {
                                Common.ReportSignalParameterTypeNotSupported(context, parameter);
                            }
                        }

                        if (!methodSymbol.ReturnsVoid)
                        {
                            Common.ReportSignalDelegateSignatureMustReturnVoid(context, signalDelegateSymbol);
                        }
                    }
                    continue;
                }

                godotSignalDelegates.Add(new(signalName, signalDelegateSymbol, invokeMethodData.Value));
            }

            source.Append("    private partial class GodotInternal {\n");

            // Generate cached StringNames for methods and properties, for fast lookup

            foreach (var signalDelegate in godotSignalDelegates)
            {
                string signalName = signalDelegate.Name;
                source.Append("        public static readonly StringName SignalName_");
                source.Append(signalName);
                source.Append(" = \"");
                source.Append(signalName);
                source.Append("\";\n");
            }

            source.Append("    }\n"); // class GodotInternal

            // Generate GetGodotSignalList

            if (godotSignalDelegates.Count > 0)
            {
                source.Append("#pragma warning disable CS0109 // Disable warning about redundant 'new' keyword\n");

                const string listType = "System.Collections.Generic.List<global::Godot.Bridge.MethodInfo>";

                source.Append("    internal new static ")
                    .Append(listType)
                    .Append(" GetGodotSignalList()\n    {\n");

                source.Append("        var signals = new ")
                    .Append(listType)
                    .Append("(")
                    .Append(godotSignalDelegates.Count)
                    .Append(");\n");

                foreach (var signalDelegateData in godotSignalDelegates)
                {
                    var methodInfo = DetermineMethodInfo(signalDelegateData);
                    AppendMethodInfo(source, methodInfo);
                }

                source.Append("        return signals;\n");
                source.Append("    }\n");

                source.Append("#pragma warning restore CS0109\n");
            }

            // Generate signal event

            foreach (var signalDelegate in godotSignalDelegates)
            {
                string signalName = signalDelegate.Name;

                // TODO: Hide backing event from code-completion and debugger
                // The reason we have a backing field is to hide the invoke method from the event,
                // as it doesn't emit the signal, only the event delegates. This can confuse users.
                // Maybe we should directly connect the delegates, as we do with native signals?
                source.Append("    private ")
                    .Append(signalDelegate.DelegateSymbol.FullQualifiedName())
                    .Append(" backing_")
                    .Append(signalName)
                    .Append(";\n");

                source.Append("    public event ")
                    .Append(signalDelegate.DelegateSymbol.FullQualifiedName())
                    .Append(" ")
                    .Append(signalName)
                    .Append(" {\n")
                    .Append("        add => backing_")
                    .Append(signalName)
                    .Append(" += value;\n")
                    .Append("        remove => backing_")
                    .Append(signalName)
                    .Append(" -= value;\n")
                    .Append("}\n");
            }

            // Generate RaiseGodotClassSignalCallbacks

            if (godotSignalDelegates.Count > 0)
            {
                source.Append(
                    "    protected override void RaiseGodotClassSignalCallbacks(in godot_string_name signal, ");
                source.Append("NativeVariantPtrArgs args, int argCount)\n    {\n");

                foreach (var signal in godotSignalDelegates)
                {
                    GenerateSignalEventInvoker(signal, source);
                }

                source.Append("        base.RaiseGodotClassSignalCallbacks(signal, args, argCount);\n");

                source.Append("    }\n");
            }

            source.Append("}\n"); // partial class

            if (isInnerClass)
            {
                var containingType = symbol.ContainingType;

                while (containingType != null)
                {
                    source.Append("}\n"); // outer class

                    containingType = containingType.ContainingType;
                }
            }

            if (hasNamespace)
            {
                source.Append("\n}\n");
            }

            context.AddSource(uniqueHint, SourceText.From(source.ToString(), Encoding.UTF8));
        }

        private static void AppendMethodInfo(StringBuilder source, MethodInfo methodInfo)
        {
            source.Append("        signals.Add(new(name: GodotInternal.SignalName_")
                .Append(methodInfo.Name)
                .Append(", returnVal: ");

            AppendPropertyInfo(source, methodInfo.ReturnVal);

            source.Append(", flags: (Godot.MethodFlags)")
                .Append((int)methodInfo.Flags)
                .Append(", arguments: ");

            if (methodInfo.Arguments is { Count: > 0 })
            {
                source.Append("new() { ");

                foreach (var param in methodInfo.Arguments)
                {
                    AppendPropertyInfo(source, param);

                    // C# allows colon after the last element
                    source.Append(", ");
                }

                source.Append(" }");
            }
            else
            {
                source.Append("null");
            }

            source.Append(", defaultArguments: null));\n");
        }

        private static void AppendPropertyInfo(StringBuilder source, PropertyInfo propertyInfo)
        {
            source.Append("new(type: (Godot.Variant.Type)")
                .Append((int)propertyInfo.Type)
                .Append(", name: \"")
                .Append(propertyInfo.Name)
                .Append("\", hint: (Godot.PropertyHint)")
                .Append((int)propertyInfo.Hint)
                .Append(", hintString: \"")
                .Append(propertyInfo.HintString)
                .Append("\", usage: (Godot.PropertyUsageFlags)")
                .Append((int)propertyInfo.Usage)
                .Append(", exported: ")
                .Append(propertyInfo.Exported ? "true" : "false")
                .Append(")");
        }

        private static MethodInfo DetermineMethodInfo(GodotSignalDelegateData signalDelegateData)
        {
            var invokeMethodData = signalDelegateData.InvokeMethodData;

            PropertyInfo returnVal;

            if (invokeMethodData.RetType != null)
            {
                returnVal = DeterminePropertyInfo(invokeMethodData.RetType.Value, name: string.Empty);
            }
            else
            {
                returnVal = new PropertyInfo(VariantType.Nil, string.Empty, PropertyHint.None,
                    hintString: null, PropertyUsageFlags.Default, exported: false);
            }

            int paramCount = invokeMethodData.ParamTypes.Length;

            List<PropertyInfo>? arguments;

            if (paramCount > 0)
            {
                arguments = new(capacity: paramCount);

                for (int i = 0; i < paramCount; i++)
                {
                    arguments.Add(DeterminePropertyInfo(invokeMethodData.ParamTypes[i],
                        name: invokeMethodData.Method.Parameters[i].Name));
                }
            }
            else
            {
                arguments = null;
            }

            return new MethodInfo(signalDelegateData.Name, returnVal, MethodFlags.Default, arguments,
                defaultArguments: null);
        }

        private static PropertyInfo DeterminePropertyInfo(MarshalType marshalType, string name)
        {
            var memberVariantType = MarshalUtils.ConvertMarshalTypeToVariantType(marshalType)!.Value;

            var propUsage = PropertyUsageFlags.Default;

            if (memberVariantType == VariantType.Nil)
                propUsage |= PropertyUsageFlags.NilIsVariant;

            return new PropertyInfo(memberVariantType, name,
                PropertyHint.None, string.Empty, propUsage, exported: false);
        }

        private static void GenerateSignalEventInvoker(
            GodotSignalDelegateData signal,
            StringBuilder source
        )
        {
            string signalName = signal.Name;
            var invokeMethodData = signal.InvokeMethodData;

            source.Append("        if (signal == GodotInternal.SignalName_");
            source.Append(signalName);
            source.Append(" && argCount == ");
            source.Append(invokeMethodData.ParamTypes.Length);
            source.Append(") {\n");
            source.Append("            backing_");
            source.Append(signalName);
            source.Append("?.Invoke(");

            for (int i = 0; i < invokeMethodData.ParamTypes.Length; i++)
            {
                if (i != 0)
                    source.Append(", ");

                source.AppendNativeVariantToManagedExpr(string.Concat("args[", i.ToString(), "]"),
                    invokeMethodData.ParamTypeSymbols[i], invokeMethodData.ParamTypes[i]);
            }

            source.Append(");\n");

            source.Append("            return;\n");

            source.Append("        }\n");
        }
    }
}
