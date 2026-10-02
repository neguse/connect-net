using System;
using System.Collections.Generic;

namespace ConnectNet.Validation.Cel.Checker;

internal sealed class CheckerOptions
{
    public static readonly CheckerOptions Default = new();

    /// <summary>Allow <c>int &lt; double</c> and the other cross-type numeric comparisons. Protovalidate enables this.</summary>
    public bool CrossTypeNumericComparisons { get; init; } = true;

    /// <summary>
    /// When true, a list or map literal whose elements have different types is an error instead
    /// of a <c>dyn</c>-typed aggregate.
    /// </summary>
    public bool HomogeneousAggregateLiterals { get; init; }
}

/// <summary>
/// The declarations visible to the checker: a container for name resolution, a type provider,
/// variables in lexical scopes, and functions. Comprehensions push scopes for their variables.
/// </summary>
internal sealed class CheckerEnv
{
    private readonly List<Dictionary<string, VariableDecl>> _scopes = new();
    private readonly Dictionary<string, FunctionDecl> _functions = new(StringComparer.Ordinal);

    public CheckerEnv(Container? container = null, TypeProvider? provider = null, CheckerOptions? options = null)
    {
        Container = container ?? Container.Root;
        Provider = provider ?? EmptyTypeProvider.Instance;
        Options = options ?? CheckerOptions.Default;
        _scopes.Add(new Dictionary<string, VariableDecl>(StringComparer.Ordinal));
    }

    public Container Container { get; }

    public TypeProvider Provider { get; }

    public CheckerOptions Options { get; }

    /// <summary>Adds the standard type identifiers and functions.</summary>
    public CheckerEnv AddStandardLibrary()
    {
        foreach (var v in StandardDecls.TypeIdents) AddVariable(v);
        foreach (var f in StandardDecls.Functions) AddFunction(f);
        return this;
    }

    public CheckerEnv AddStringsExtension()
    {
        foreach (var f in StandardDecls.StringsExtension) AddFunction(f);
        return this;
    }

    public CheckerEnv AddVariable(VariableDecl decl)
    {
        _scopes[_scopes.Count - 1][decl.Name] = decl;
        return this;
    }

    public CheckerEnv AddVariable(string name, CelType type) => AddVariable(new VariableDecl(name, type));

    public CheckerEnv AddFunction(FunctionDecl decl)
    {
        if (_functions.TryGetValue(decl.Name, out var existing))
            existing.Merge(decl);
        else
            _functions[decl.Name] = new FunctionDecl(decl.Name).Merge(decl);
        return this;
    }

    internal void EnterScope() => _scopes.Add(new Dictionary<string, VariableDecl>(StringComparer.Ordinal));

    internal void ExitScope() => _scopes.RemoveAt(_scopes.Count - 1);

    private VariableDecl? LookupLocalIdent(string name)
    {
        for (int i = _scopes.Count - 1; i >= 1; i--)
        {
            if (_scopes[i].TryGetValue(name, out var decl))
                return decl;
        }
        return null;
    }

    private VariableDecl? LookupGlobalIdent(string candidate)
    {
        if (_scopes[0].TryGetValue(candidate, out var decl))
            return decl;
        var type = Provider.FindType(candidate);
        if (type != null)
            return new VariableDecl(candidate, CelType.TypeOf(type));
        if (Provider.TryFindEnumValue(candidate, out var enumValue))
            return new VariableDecl(candidate, CelType.Int, enumValue);
        return null;
    }

    /// <summary>The resolution of an identifier, with the fully qualified name to record.</summary>
    internal sealed class Resolution
    {
        public Resolution(VariableDecl decl, bool requiresDisambiguation)
        {
            Decl = decl;
            Name = requiresDisambiguation ? "." + decl.Name.TrimStart('.') : decl.Name.TrimStart('.');
        }

        public VariableDecl Decl { get; }

        /// <summary>The name to record; a leading dot marks a global shadowed by a local of the same name.</summary>
        public string Name { get; }
    }

    internal Resolution? ResolveSimpleIdent(string name)
    {
        var local = LookupLocalIdent(name);
        if (local != null && !name.StartsWith(".", StringComparison.Ordinal))
            return new Resolution(local, false);
        foreach (var candidate in Container.ResolveCandidateNames(name))
        {
            var ident = LookupGlobalIdent(candidate);
            if (ident != null)
                return new Resolution(ident, local != null);
        }
        return null;
    }

    internal Resolution? ResolveQualifiedIdent(IReadOnlyList<string> qualifiers)
    {
        if (qualifiers.Count == 1)
            return ResolveSimpleIdent(qualifiers[0]);
        var local = LookupLocalIdent(qualifiers[0]);
        if (local != null && !qualifiers[0].StartsWith(".", StringComparison.Ordinal))
            return null;
        var varName = string.Join(".", qualifiers);
        foreach (var candidate in Container.ResolveCandidateNames(varName))
        {
            var ident = LookupGlobalIdent(candidate);
            if (ident != null)
                return new Resolution(ident, local != null);
        }
        return null;
    }

    /// <summary>Resolves a message or well-known type name for construction.</summary>
    internal VariableDecl? ResolveTypeIdent(string name)
    {
        foreach (var candidate in Container.ResolveCandidateNames(name))
        {
            var type = Provider.FindType(candidate);
            if (type != null)
                return new VariableDecl(candidate, CelType.TypeOf(type));
        }
        return null;
    }

    internal FunctionDecl? LookupFunction(string name)
    {
        foreach (var candidate in Container.ResolveCandidateNames(name))
        {
            if (_functions.TryGetValue(candidate, out var fn))
                return fn;
        }
        return null;
    }

    internal bool IsOverloadDisabled(string overloadId) =>
        !Options.CrossTypeNumericComparisons && StandardDecls.IsCrossTypeNumericComparison(overloadId);
}
