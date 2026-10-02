using System;
using System.Collections.Generic;
using System.Text;
using ConnectNet.Validation.Cel.Syntax;

namespace ConnectNet.Validation.Cel.Checker;

/// <summary>How an identifier, function call or message literal was resolved.</summary>
internal sealed class Reference
{
    private Reference(string? name, object? constantValue, IReadOnlyList<string>? overloadIds)
    {
        Name = name;
        ConstantValue = constantValue;
        OverloadIds = overloadIds ?? Array.Empty<string>();
    }

    /// <summary>The fully qualified variable or type name, for identifiers and message literals.</summary>
    public string? Name { get; }

    /// <summary>The compile-time value of a constant identifier (an enum value).</summary>
    public object? ConstantValue { get; }

    /// <summary>The candidate overload ids of a call, in declaration order.</summary>
    public IReadOnlyList<string> OverloadIds { get; }

    public static Reference Ident(string name, object? constantValue = null) => new(name, constantValue, null);

    public static Reference Function(IReadOnlyList<string> overloadIds) => new(null, null, overloadIds);
}

internal sealed class CheckResult
{
    public CheckResult(Expr? expr, CelType resultType, IReadOnlyDictionary<long, CelType> types,
        IReadOnlyDictionary<long, Reference> references, SourceInfo sourceInfo, CelErrors errors)
    {
        Expr = expr;
        ResultType = resultType;
        Types = types;
        References = references;
        SourceInfo = sourceInfo;
        Errors = errors;
    }

    /// <summary>The checked tree, with names and functions fully qualified; null on error.</summary>
    public Expr? Expr { get; }

    public CelType ResultType { get; }

    public IReadOnlyDictionary<long, CelType> Types { get; }

    public IReadOnlyDictionary<long, Reference> References { get; }

    public SourceInfo SourceInfo { get; }

    public CelErrors Errors { get; }

    public bool IsSuccess => Expr != null && !Errors.HasErrors;
}

/// <summary>
/// Type checker following the reference semantics: Hindley–Milner style unification over the
/// overload declarations, name resolution through the container, and fully qualified rewrites
/// of identifiers, functions and message names in the output tree.
/// </summary>
internal sealed class Checker
{
    private readonly CheckerEnv _env;
    private readonly SourceInfo _sourceInfo;
    private readonly CelErrors _errors;
    private readonly Dictionary<long, CelType> _types = new();
    private readonly Dictionary<long, Reference> _references = new();
    private TypeMapping _mappings = new();
    private int _freeTypeVarCounter;

    private Checker(CheckerEnv env, SourceInfo sourceInfo)
    {
        _env = env;
        _sourceInfo = sourceInfo;
        _errors = new CelErrors(sourceInfo.Source);
    }

    public static CheckResult Check(ParseResult parsed, CheckerEnv env)
    {
        if (parsed.Expr == null)
            throw new ArgumentException("cannot check a failed parse", nameof(parsed));
        return Check(parsed.Expr, parsed.SourceInfo, env);
    }

    public static CheckResult Check(Expr expr, SourceInfo sourceInfo, CheckerEnv env)
    {
        var checker = new Checker(env, sourceInfo);
        var checked_ = checker.CheckExpr(expr);

        // Walk the type map once more to substitute the inferred type variables, defaulting the
        // ones that stayed free to dyn.
        var ids = new List<long>(checker._types.Keys);
        foreach (var id in ids)
            checker._types[id] = Substitute(checker._mappings, checker._types[id], typeParamToDyn: true);

        var resultType = checker._types.TryGetValue(checked_.Id, out var rt) ? rt : CelType.Error;
        return new CheckResult(checker._errors.HasErrors ? null : checked_, resultType, checker._types,
            checker._references, sourceInfo, checker._errors);
    }

    private int Offset(long id) => _sourceInfo.GetPosition(id);

    private void Report(long id, string message) => _errors.Report(message, Offset(id), id);

    private CelType GetType(Expr e) => _types.TryGetValue(e.Id, out var t) ? t : CelType.Error;

    private void SetType(Expr e, CelType t)
    {
        if (_types.TryGetValue(e.Id, out var old) && !old.IsExactType(t))
        {
            Report(e.Id, $"incompatible type already exists for expression: {e}({e.Id}) old:{old}, new:{t}");
            return;
        }
        _types[e.Id] = t;
    }

    private void SetReference(Expr e, Reference r) => _references[e.Id] = r;

    private Expr CheckExpr(Expr e)
    {
        switch (e)
        {
            case LiteralExpr lit:
                SetType(e, lit.LiteralKind switch
                {
                    LiteralKind.Null => CelType.Null,
                    LiteralKind.Bool => CelType.Bool,
                    LiteralKind.Int => CelType.Int,
                    LiteralKind.Uint => CelType.Uint,
                    LiteralKind.Double => CelType.Double,
                    LiteralKind.String => CelType.String,
                    _ => CelType.Bytes,
                });
                return e;
            case IdentExpr ident:
                return CheckIdent(ident);
            case SelectExpr sel:
                return CheckSelect(sel);
            case CallExpr call:
                return CheckCall(call);
            case ListExpr list:
                return CheckList(list);
            case MapExpr map:
                return CheckMap(map);
            case StructExpr st:
                return CheckStruct(st);
            case ComprehensionExpr comp:
                return CheckComprehension(comp);
            default:
                throw new InvalidOperationException("unexpected expression kind " + e.Kind);
        }
    }

    private Expr CheckIdent(IdentExpr e)
    {
        var resolution = _env.ResolveSimpleIdent(e.Name);
        if (resolution != null)
        {
            SetType(e, resolution.Decl.Type);
            SetReference(e, Reference.Ident(resolution.Name, resolution.Decl.ConstantValue));
            return resolution.Name == e.Name ? e : new IdentExpr(e.Id, resolution.Name);
        }
        SetType(e, CelType.Error);
        Report(e.Id, $"undeclared reference to '{e.Name}' (in container '{_env.Container.Name}')");
        return e;
    }

    private Expr CheckSelect(SelectExpr e)
    {
        // A chain of selects over an identifier may be a qualified name.
        var qualifiers = ComputeQualifiers(e);
        if (qualifiers != null)
        {
            var resolution = _env.ResolveQualifiedIdent(qualifiers);
            if (resolution != null)
            {
                SetType(e, resolution.Decl.Type);
                SetReference(e, Reference.Ident(resolution.Name, resolution.Decl.ConstantValue));
                return new IdentExpr(e.Id, resolution.Name);
            }
        }

        var operand = CheckExpr(e.Operand);
        var resultType = CheckSelectField(e, operand, e.Field);
        if (e.TestOnly)
            resultType = CelType.Bool;
        SetType(e, Substitute(_mappings, resultType, false));
        return ReferenceEquals(operand, e.Operand) ? e : new SelectExpr(e.Id, operand, e.Field, e.TestOnly);
    }

    private static List<string>? ComputeQualifiers(Expr e)
    {
        var qualifiers = new List<string>();
        while (e is SelectExpr sel)
        {
            if (sel.TestOnly)
                return null;
            qualifiers.Add(sel.Field);
            e = sel.Operand;
            if (e is IdentExpr ident)
            {
                qualifiers.Add(ident.Name);
                qualifiers.Reverse();
                return qualifiers;
            }
        }
        return null;
    }

    private CelType CheckSelectField(Expr e, Expr operand, string field)
    {
        var operandType = Substitute(_mappings, GetType(operand), false);
        switch (operandType.Kind)
        {
            case TypeKind.Map:
                return operandType.Parameters[1];
            case TypeKind.Struct:
                return LookupFieldType(e.Id, operandType.Name, field) ?? CelType.Error;
            case TypeKind.TypeParam:
                IsAssignable(CelType.Dyn, operandType);
                return CelType.Dyn;
            default:
                if (!operandType.IsDynOrError)
                    Report(e.Id, $"type '{operandType}' does not support field selection");
                return CelType.Dyn;
        }
    }

    private CelType? LookupFieldType(long id, string messageName, string field)
    {
        if (!_env.Provider.HasMessage(messageName))
        {
            Report(id, $"unexpected failed resolution of '{messageName}'");
            return null;
        }
        var ft = _env.Provider.FindFieldType(messageName, field);
        if (ft == null)
        {
            Report(id, $"undefined field '{field}'");
            return null;
        }
        return ft;
    }

    private Expr CheckCall(CallExpr e)
    {
        var args = new Expr[e.Args.Count];
        for (int i = 0; i < args.Length; i++)
            args[i] = CheckExpr(e.Args[i]);

        if (e.Target == null)
        {
            var fn = _env.LookupFunction(e.Function);
            if (fn == null)
            {
                Report(e.Id, $"undeclared reference to '{e.Function}' (in container '{_env.Container.Name}')");
                SetType(e, CelType.Error);
                return new CallExpr(e.Id, e.Function, null, args);
            }
            var call = new CallExpr(e.Id, fn.Name, null, args);
            ResolveOverloadOrError(call, fn, null, args);
            return call;
        }

        // a.b.c(x) is either a call of the namespaced function a.b.c, or a receiver call of c.
        var qualifiedPrefix = ToQualifiedName(e.Target);
        if (qualifiedPrefix != null)
        {
            var fn = _env.LookupFunction(qualifiedPrefix + "." + e.Function);
            if (fn != null)
            {
                var call = new CallExpr(e.Id, fn.Name, null, args);
                ResolveOverloadOrError(call, fn, null, args);
                return call;
            }
        }

        var target = CheckExpr(e.Target);
        var memberFn = _env.LookupFunction(e.Function);
        var memberCall = new CallExpr(e.Id, e.Function, target, args);
        if (memberFn != null)
        {
            ResolveOverloadOrError(memberCall, memberFn, target, args);
            return memberCall;
        }
        SetType(e, CelType.Error);
        Report(e.Id, $"undeclared reference to '{e.Function}' (in container '{_env.Container.Name}')");
        return memberCall;
    }

    private static string? ToQualifiedName(Expr e)
    {
        switch (e)
        {
            case IdentExpr ident:
                return ident.Name;
            case SelectExpr sel when !sel.TestOnly:
            {
                var prefix = ToQualifiedName(sel.Operand);
                return prefix == null ? null : prefix + "." + sel.Field;
            }
            default:
                return null;
        }
    }

    private void ResolveOverloadOrError(CallExpr call, FunctionDecl fn, Expr? target, Expr[] args)
    {
        var argTypes = new List<CelType>();
        if (target != null)
            argTypes.Add(GetType(target));
        foreach (var a in args)
            argTypes.Add(GetType(a));

        CelType? resultType = null;
        var overloadIds = new List<string>();
        foreach (var overload in fn.Overloads)
        {
            if (_env.IsOverloadDisabled(overload.Id))
                continue;
            if ((target == null && overload.IsMemberFunction) || (target != null && !overload.IsMemberFunction))
                continue;
            if (overload.ArgTypes.Count != argTypes.Count)
                continue;

            if (fn.Name == Operators.LogicalAnd || fn.Name == Operators.LogicalOr)
            {
                bool failed = false;
                for (int i = 0; i < argTypes.Count; i++)
                {
                    if (!IsAssignable(argTypes[i], CelType.Bool))
                    {
                        Report(args[i].Id, $"expected type '{CelType.Bool}' but found '{Substitute(_mappings, argTypes[i], true)}'");
                        failed = true;
                    }
                }
                if (failed)
                {
                    SetType(call, CelType.Error);
                    return;
                }
                SetType(call, CelType.Bool);
                SetReference(call, Reference.Function(new[] { overload.Id }));
                return;
            }

            // Instantiate the overload's type parameters with fresh variables.
            var candidateArgs = overload.ArgTypes;
            var candidateResult = overload.ResultType;
            if (overload.TypeParams.Count > 0)
            {
                var subs = new TypeMapping();
                foreach (var p in overload.TypeParams)
                    subs.Add(CelType.TypeParam(p), NewTypeVar());
                var instantiated = new CelType[candidateArgs.Count];
                for (int i = 0; i < instantiated.Length; i++)
                    instantiated[i] = Substitute(subs, candidateArgs[i], false);
                candidateArgs = instantiated;
                candidateResult = Substitute(subs, candidateResult, false);
            }

            if (IsAssignableList(argTypes, candidateArgs))
            {
                overloadIds.Add(overload.Id);
                var fnResultType = Substitute(_mappings, candidateResult, false);
                if (resultType == null)
                    resultType = fnResultType;
                else if (!resultType.IsDyn && !fnResultType.IsExactType(resultType))
                    resultType = CelType.Dyn;
            }
        }

        if (resultType == null)
        {
            var sb = new StringBuilder();
            sb.Append($"found no matching overload for '{fn.Name}' applied to '");
            FormatSignature(sb, argTypes, target != null);
            sb.Append('\'');
            Report(call.Id, sb.ToString());
            SetType(call, CelType.Error);
            return;
        }

        SetType(call, resultType);
        SetReference(call, Reference.Function(overloadIds));
    }

    private void FormatSignature(StringBuilder sb, List<CelType> argTypes, bool isInstance)
    {
        int start = 0;
        if (isInstance)
        {
            sb.Append(Substitute(_mappings, argTypes[0], true)).Append('.');
            start = 1;
        }
        sb.Append('(');
        for (int i = start; i < argTypes.Count; i++)
        {
            if (i > start) sb.Append(", ");
            sb.Append(Substitute(_mappings, argTypes[i], true));
        }
        sb.Append(')');
    }

    private Expr CheckList(ListExpr e)
    {
        var elements = new Expr[e.Elements.Count];
        CelType? elemsType = null;
        for (int i = 0; i < elements.Length; i++)
        {
            elements[i] = CheckExpr(e.Elements[i]);
            elemsType = JoinTypes(elements[i], elemsType, GetType(elements[i]));
        }
        elemsType ??= NewTypeVar();
        SetType(e, CelType.List(elemsType));
        return new ListExpr(e.Id, elements);
    }

    private Expr CheckMap(MapExpr e)
    {
        var entries = new MapEntry[e.Entries.Count];
        CelType? keyType = null;
        CelType? valueType = null;
        for (int i = 0; i < entries.Length; i++)
        {
            var key = CheckExpr(e.Entries[i].Key);
            keyType = JoinTypes(key, keyType, GetType(key));
            var value = CheckExpr(e.Entries[i].Value);
            valueType = JoinTypes(value, valueType, GetType(value));
            entries[i] = new MapEntry(e.Entries[i].Id, key, value);
        }
        if (keyType == null)
        {
            keyType = NewTypeVar();
            valueType = NewTypeVar();
        }
        SetType(e, CelType.Map(keyType, valueType!));
        return new MapExpr(e.Id, entries);
    }

    private Expr CheckStruct(StructExpr e)
    {
        var ident = _env.ResolveTypeIdent(e.MessageName);
        if (ident == null)
        {
            Report(e.Id, $"undeclared reference to '{e.MessageName}' (in container '{_env.Container.Name}')");
            SetType(e, CelType.Error);
            return e;
        }

        var typeName = ident.Name;
        SetReference(e, Reference.Ident(typeName));
        var resultType = CelType.Error;
        if (ident.Type.Kind == TypeKind.Type && ident.Type.Parameters.Count == 1)
        {
            resultType = ident.Type.Parameters[0];
            var wellKnown = resultType.WellKnownMessageName;
            if (wellKnown != null)
            {
                typeName = wellKnown;
            }
            else if (resultType.Kind == TypeKind.Struct)
            {
                typeName = resultType.Name;
            }
            else
            {
                Report(e.Id, $"'{resultType}' is not a message type");
                resultType = CelType.Error;
            }
        }
        else
        {
            Report(e.Id, $"'{ident.Type}' is not a type");
        }
        SetType(e, resultType);

        var entries = new FieldEntry[e.Entries.Count];
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = e.Entries[i];
            var value = CheckExpr(entry.Value);
            entries[i] = new FieldEntry(entry.Id, entry.Field, value);
            var fieldType = resultType.Kind == TypeKind.Error ? CelType.Error : LookupFieldType(entry.Id, typeName, entry.Field) ?? CelType.Error;
            var valueType = GetType(value);
            if (!IsAssignable(fieldType, valueType))
            {
                Report(entry.Id,
                    $"expected type of field '{entry.Field}' is '{Substitute(_mappings, fieldType, true)}' but provided type is '{Substitute(_mappings, valueType, true)}'");
            }
        }
        return new StructExpr(e.Id, typeName, entries);
    }

    private Expr CheckComprehension(ComprehensionExpr e)
    {
        var iterRange = CheckExpr(e.IterRange);
        var accuInit = CheckExpr(e.AccuInit);
        var rangeType = Substitute(_mappings, GetType(iterRange), false);

        var accuType = GetType(accuInit);
        _env.EnterScope();
        _env.AddVariable(e.AccuVar, accuType);

        CelType varType;
        switch (rangeType.Kind)
        {
            case TypeKind.List:
                varType = rangeType.Parameters[0];
                break;
            case TypeKind.Map:
                varType = rangeType.Parameters[0];
                break;
            case TypeKind.Dyn:
            case TypeKind.Any:
            case TypeKind.Error:
            case TypeKind.TypeParam:
                IsAssignable(CelType.Dyn, rangeType);
                varType = CelType.Dyn;
                break;
            default:
                Report(iterRange.Id,
                    $"expression of type '{rangeType}' cannot be range of a comprehension (must be list, map, or dynamic)");
                varType = CelType.Error;
                break;
        }

        _env.EnterScope();
        _env.AddVariable(e.IterVar, varType);
        var loopCondition = CheckExpr(e.LoopCondition);
        AssertType(loopCondition, CelType.Bool);
        var loopStep = CheckExpr(e.LoopStep);
        AssertType(loopStep, accuType);
        _env.ExitScope();
        var result = CheckExpr(e.Result);
        _env.ExitScope();
        SetType(e, Substitute(_mappings, GetType(result), false));
        return new ComprehensionExpr(e.Id, e.IterVar, iterRange, e.AccuVar, accuInit, loopCondition, loopStep, result);
    }

    private CelType JoinTypes(Expr e, CelType? previous, CelType current)
    {
        if (previous == null)
            return current;
        if (IsAssignable(previous, current))
            return MostGeneral(previous, current);
        if (!_env.Options.HomogeneousAggregateLiterals)
            return CelType.Dyn;
        Report(e.Id, $"expected type '{previous}' but found '{current}'");
        return CelType.Error;
    }

    private void AssertType(Expr e, CelType expected)
    {
        if (!IsAssignable(expected, GetType(e)))
            Report(e.Id, $"expected type '{Substitute(_mappings, expected, true)}' but found '{Substitute(_mappings, GetType(e), true)}'");
    }

    private CelType NewTypeVar() => CelType.TypeParam("_var" + _freeTypeVarCounter++);

    private bool IsAssignable(CelType t1, CelType t2)
    {
        var copy = _mappings.Copy();
        if (InternalIsAssignable(copy, t1, t2))
        {
            _mappings = copy;
            return true;
        }
        return false;
    }

    private bool IsAssignableList(IReadOnlyList<CelType> l1, IReadOnlyList<CelType> l2)
    {
        var copy = _mappings.Copy();
        if (InternalIsAssignableList(copy, l1, l2))
        {
            _mappings = copy;
            return true;
        }
        return false;
    }

    // ---- type unification, following the reference checker ----

    internal sealed class TypeMapping
    {
        private readonly Dictionary<string, CelType> _map;

        public TypeMapping()
        {
            _map = new Dictionary<string, CelType>(StringComparer.Ordinal);
        }

        private TypeMapping(Dictionary<string, CelType> map)
        {
            _map = map;
        }

        public void Add(CelType typeParam, CelType type) => _map[typeParam.Name] = type;

        public bool TryFind(CelType t, out CelType sub)
        {
            if (t.Kind == TypeKind.TypeParam && _map.TryGetValue(t.Name, out sub!))
                return true;
            sub = null!;
            return false;
        }

        public TypeMapping Copy() => new(new Dictionary<string, CelType>(_map, StringComparer.Ordinal));
    }

    private static bool InternalIsAssignable(TypeMapping m, CelType t1, CelType t2)
    {
        var kind1 = t1.Kind;
        var kind2 = t2.Kind;
        if (kind2 == TypeKind.TypeParam)
        {
            var (valid, hasSub) = IsValidTypeSubstitution(m, t1, t2);
            if (valid) return true;
            if (hasSub) return false;
        }
        if (kind1 == TypeKind.TypeParam)
        {
            var (valid, _) = IsValidTypeSubstitution(m, t2, t1);
            return valid;
        }
        if (t1.IsDynOrError || t2.IsDynOrError)
            return true;
        if (kind1 == TypeKind.Null)
            return IsAssignableNull(t2);
        if (kind2 == TypeKind.Null)
            return IsAssignableNull(t1);

        switch (kind1)
        {
            case TypeKind.Bool:
            case TypeKind.Bytes:
            case TypeKind.Double:
            case TypeKind.Int:
            case TypeKind.String:
            case TypeKind.Uint:
            case TypeKind.Any:
            case TypeKind.Duration:
            case TypeKind.Timestamp:
            case TypeKind.Struct:
                return t2.IsAssignableFrom(t1);
            case TypeKind.Type:
                return kind2 == TypeKind.Type;
            case TypeKind.Opaque:
            case TypeKind.List:
            case TypeKind.Map:
                return kind1 == kind2 && t1.Name == t2.Name && InternalIsAssignableList(m, t1.Parameters, t2.Parameters);
            default:
                return false;
        }
    }

    private static (bool Valid, bool HasSub) IsValidTypeSubstitution(TypeMapping m, CelType t1, CelType t2)
    {
        if (t1.Kind == t2.Kind && t1.IsExactType(t2))
            return (true, true);
        if (m.TryFind(t2, out var t2Sub))
        {
            if (t1.Kind == t2Sub.Kind && t1.IsExactType(t2Sub))
                return (true, true);
            if (InternalIsAssignable(m, t1, t2Sub))
            {
                var t2New = MostGeneral(t1, t2Sub);
                if (NotReferencedIn(m, t2, t2New))
                    m.Add(t2, t2New);
                return (true, true);
            }
            return (false, true);
        }
        if (NotReferencedIn(m, t2, t1))
        {
            m.Add(t2, t1);
            return (true, false);
        }
        return (false, false);
    }

    private static bool InternalIsAssignableList(TypeMapping m, IReadOnlyList<CelType> l1, IReadOnlyList<CelType> l2)
    {
        if (l1.Count != l2.Count)
            return false;
        for (int i = 0; i < l1.Count; i++)
        {
            if (!InternalIsAssignable(m, l1[i], l2[i]))
                return false;
        }
        return true;
    }

    private static bool IsAssignableNull(CelType t)
    {
        switch (t.Kind)
        {
            case TypeKind.Opaque:
            case TypeKind.Struct:
            case TypeKind.Any:
            case TypeKind.Duration:
            case TypeKind.Timestamp:
            case TypeKind.Null:
                return true;
            default:
                return t.IsNullable || t.IsDyn;
        }
    }

    private static bool IsEqualOrLessSpecific(CelType t1, CelType t2)
    {
        if (t1.IsDyn || t1.Kind == TypeKind.TypeParam)
            return true;
        if (t2.IsDyn || t2.Kind == TypeKind.TypeParam)
            return false;
        if (t1.Kind != t2.Kind)
            return false;
        switch (t1.Kind)
        {
            case TypeKind.Opaque:
                if (t1.Name != t2.Name || t1.Parameters.Count != t2.Parameters.Count)
                    return false;
                for (int i = 0; i < t1.Parameters.Count; i++)
                {
                    if (!IsEqualOrLessSpecific(t1.Parameters[i], t2.Parameters[i]))
                        return false;
                }
                return true;
            case TypeKind.List:
                return IsEqualOrLessSpecific(t1.Parameters[0], t2.Parameters[0]);
            case TypeKind.Map:
                return IsEqualOrLessSpecific(t1.Parameters[0], t2.Parameters[0])
                    && IsEqualOrLessSpecific(t1.Parameters[1], t2.Parameters[1]);
            case TypeKind.Type:
                return true;
            default:
                return t1.IsExactType(t2);
        }
    }

    private static CelType MostGeneral(CelType t1, CelType t2) => IsEqualOrLessSpecific(t1, t2) ? t1 : t2;

    private static bool NotReferencedIn(TypeMapping m, CelType t, CelType withinType)
    {
        if (t.IsExactType(withinType))
            return false;
        switch (withinType.Kind)
        {
            case TypeKind.TypeParam:
                return !m.TryFind(withinType, out var sub) || NotReferencedIn(m, t, sub);
            case TypeKind.Opaque:
            case TypeKind.List:
            case TypeKind.Map:
            case TypeKind.Type:
                foreach (var p in withinType.Parameters)
                {
                    if (!NotReferencedIn(m, t, p))
                        return false;
                }
                return true;
            default:
                return true;
        }
    }

    internal static CelType Substitute(TypeMapping m, CelType t, bool typeParamToDyn)
    {
        if (m.TryFind(t, out var sub))
            return Substitute(m, sub, typeParamToDyn);
        if (typeParamToDyn && t.Kind == TypeKind.TypeParam)
            return CelType.Dyn;
        switch (t.Kind)
        {
            case TypeKind.Opaque:
            {
                var ps = new CelType[t.Parameters.Count];
                for (int i = 0; i < ps.Length; i++)
                    ps[i] = Substitute(m, t.Parameters[i], typeParamToDyn);
                return CelType.Opaque(t.Name, ps);
            }
            case TypeKind.List:
                return CelType.List(Substitute(m, t.Parameters[0], typeParamToDyn));
            case TypeKind.Map:
                return CelType.Map(Substitute(m, t.Parameters[0], typeParamToDyn),
                    Substitute(m, t.Parameters[1], typeParamToDyn));
            case TypeKind.Type:
                return t.Parameters.Count > 0 ? CelType.TypeOf(Substitute(m, t.Parameters[0], typeParamToDyn)) : t;
            default:
                return t;
        }
    }
}
