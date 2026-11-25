using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Pec.Qc.Mcp.Scripting;

public sealed record ScriptViolation(int Line, string What);

/// <summary>
/// What a tool written by Claude may not do. The code is compiled and then every name it uses is looked up: anything that reaches the network, starts a program, uses reflection or
/// native code, reads the environment or touches files other than through the <c>Qc</c> object is refused with the line it is on. This is a strong filter, not a security boundary
/// on its own: the real controls are that other people only run code the QC lead approved (pinned by its SHA-256), that it runs in a separate process with a time limit, and that
/// the <c>Qc</c> object only reads the files the user gave it and writes into one output folder.
/// </summary>
public static class ScriptPolicy
{
    private static readonly string[] DeniedNamespaces =
    [
        "System.Net", "System.Reflection", "System.Runtime.InteropServices", "System.Runtime.Loader", "System.Runtime.Remoting", "System.Runtime.Serialization", "System.Security", "System.CodeDom",
        "System.Linq.Expressions", "System.IO.Pipes", "System.IO.MemoryMappedFiles", "System.IO.Compression", "System.Management", "System.ServiceProcess", "System.Data", "System.Windows",
        "System.Drawing", "System.Diagnostics", "Microsoft.Win32", "Microsoft.CodeAnalysis", "System.Threading.Channels", "System.Composition", "System.ComponentModel.Composition", "System.Resources",
        "System.Xml.Xsl", "System.Transactions", "System.DirectoryServices", "System.Printing", "System.Speech", "System.Media",
    ];

    private static readonly HashSet<string> DeniedTypes = new(StringComparer.Ordinal)
    {
        "System.IO.File", "System.IO.Directory", "System.IO.FileInfo", "System.IO.DirectoryInfo", "System.IO.FileStream", "System.IO.DriveInfo", "System.IO.FileSystemWatcher", "System.IO.FileSystemInfo",
        "System.IO.FileSystemEnumerable", "System.Environment", "System.AppDomain", "System.AppContext", "System.Activator", "System.Type", "System.GC", "System.Console", "System.Delegate", "System.MulticastDelegate",
        "System.Threading.Thread", "System.Threading.ThreadPool", "System.Threading.Timer", "System.Threading.Process", "System.Threading.Mutex", "System.Threading.Semaphore", "System.Threading.EventWaitHandle",
        "System.RuntimeTypeHandle", "System.RuntimeMethodHandle", "System.TypedReference", "System.ArgIterator", "System.OperatingSystem", "System.Buffers.ArrayPool", "System.Runtime.CompilerServices.RuntimeHelpers",
        "System.Runtime.CompilerServices.Unsafe", "System.Runtime.GCSettings", "System.Runtime.MemoryFailPoint", "System.Runtime.ProfileOptimization", "System.Runtime.AssemblyTargetedPatchBandAttribute",
    };

    /// <summary>Members that give a way round a denied type.</summary>
    private static readonly HashSet<string> DeniedMembers = new(StringComparer.Ordinal) { "System.Object.GetType", "System.Exception.TargetSite", "System.Exception.GetType" };

    private static bool Denied(ITypeSymbol? t, out string why)
    {
        why = "";
        while (t is IArrayTypeSymbol arr) t = arr.ElementType;
        if (t is null) return false;
        if (t is IPointerTypeSymbol) { why = "pointers"; return true; }
        if (t.TypeKind == TypeKind.Dynamic) { why = "dynamic"; return true; }
        var named = t as INamedTypeSymbol; var def = named?.OriginalDefinition ?? t;
        var full = def.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.None));
        if (DeniedTypes.Contains(full)) { why = full; return true; }
        var ns = def.ContainingNamespace?.ToDisplayString() ?? "";
        foreach (var d in DeniedNamespaces)
            if (ns == d || ns.StartsWith(d + ".", StringComparison.Ordinal))
            {
                if (full == "System.Diagnostics.Stopwatch") return false;           // timing is harmless
                why = full; return true;
            }
        return false;
    }

    /// <summary>The reasons a script is refused. Empty = allowed. <paramref name="compilation"/> is the script's compilation (it must have no errors).</summary>
    public static List<ScriptViolation> Check(Compilation compilation, SyntaxTree tree)
    {
        var v = new List<ScriptViolation>();
        var model = compilation.GetSemanticModel(tree);
        void Add(SyntaxNode n, string what) => v.Add(new ScriptViolation(tree.GetLineSpan(n.Span).StartLinePosition.Line + 1, what));
        var root = tree.GetRoot();

        foreach (var d in root.DescendantTrivia(descendIntoTrivia: true).Where(t => t.IsKind(SyntaxKind.ReferenceDirectiveTrivia) || t.IsKind(SyntaxKind.LoadDirectiveTrivia)))
            v.Add(new ScriptViolation(tree.GetLineSpan(d.Span).StartLinePosition.Line + 1, "#r and #load are not allowed: a tool can only use the libraries it is given"));

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case UnsafeStatementSyntax or StackAllocArrayCreationExpressionSyntax or FixedStatementSyntax: Add(node, "unsafe code"); continue;
                case LocalFunctionStatementSyntax or MethodDeclarationSyntax or ClassDeclarationSyntax or StructDeclarationSyntax:
                    if (node is MemberDeclarationSyntax m && m.Modifiers.Any(x => x.IsKind(SyntaxKind.UnsafeKeyword) || x.IsKind(SyntaxKind.ExternKeyword))) Add(node, "unsafe or extern members");
                    break;
                case AttributeSyntax a when model.GetSymbolInfo(a).Symbol?.ContainingType is { } at && Denied(at, out var aw): Add(node, $"the attribute {aw}"); break;
                case UsingDirectiveSyntax u when u.Name is { } un && model.GetSymbolInfo(un).Symbol is INamespaceSymbol nsSym:
                    {
                        var s = nsSym.ToDisplayString();
                        if (DeniedNamespaces.Any(d => s == d || s.StartsWith(d + ".", StringComparison.Ordinal)) && s != "System.Diagnostics") Add(node, $"using {s}");
                        break;
                    }
            }
            if (node is IdentifierNameSyntax or GenericNameSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)
            {
                var sym = model.GetSymbolInfo(node).Symbol;
                if (sym is not null)
                {
                    var type = sym as ITypeSymbol ?? sym.ContainingType;
                    if (Denied(type, out var why)) { Add(node, why); continue; }
                    if (sym is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol && sym.ContainingType is { } ct && DeniedMembers.Contains(ct.ToDisplayString() + "." + sym.Name)) { Add(node, ct.ToDisplayString() + "." + sym.Name + " (a way round the restrictions)"); continue; }
                    // a member inherited from Object, such as GetType(), is reported by the type it was declared on
                    if (sym is IMethodSymbol ms && ms.OriginalDefinition.ContainingType?.SpecialType == SpecialType.System_Object && ms.Name == "GetType") { Add(node, "GetType() (reflection)"); continue; }
                }
            }
            if (node is ExpressionSyntax e && node is not (IdentifierNameSyntax or GenericNameSyntax) && Denied(model.GetTypeInfo(e).Type, out var tw)) Add(node, $"a value of type {tw}");
        }
        // one violation per line and kind is enough to fix it
        return v.GroupBy(x => (x.Line, x.What)).Select(g => g.First()).OrderBy(x => x.Line).ToList();
    }
}
