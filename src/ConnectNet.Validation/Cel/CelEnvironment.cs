using System;
using System.Collections.Generic;
using ConnectNet.Validation.Cel.Checker;
using ConnectNet.Validation.Cel.Runtime;
using ConnectNet.Validation.Cel.Syntax;

namespace ConnectNet.Validation.Cel;

/// <summary>Raised for an expression that fails to parse or check.</summary>
internal sealed class CelCompilationException : Exception
{
    public CelCompilationException(string message, IReadOnlyList<CelError> errors) : base(message)
    {
        Errors = errors;
    }

    public IReadOnlyList<CelError> Errors { get; }
}

/// <summary>
/// A compilation environment: the container, the type provider, the declarations the checker
/// sees and the functions the evaluator can call. Environments are immutable once built and
/// may be shared across threads; programs compiled from one are shareable too.
/// </summary>
internal sealed class CelEnvironment
{
    private readonly List<VariableDecl> _variables = new();
    private readonly List<FunctionDecl> _functionDecls = new();
    private readonly FunctionRegistry _functions = new();
    private readonly IMessageFactoryProvider? _messageFactories;
    private bool _standardLibrary;
    private bool _stringsExtension;

    public CelEnvironment(Container? container = null, TypeProvider? provider = null,
        IMessageFactoryProvider? messageFactories = null, CheckerOptions? checkerOptions = null,
        ParserOptions? parserOptions = null)
    {
        Container = container ?? Container.Root;
        Provider = provider ?? EmptyTypeProvider.Instance;
        _messageFactories = messageFactories;
        CheckerOptions = checkerOptions ?? CheckerOptions.Default;
        ParserOptions = parserOptions ?? ParserOptions.Default;
    }

    public Container Container { get; }

    public TypeProvider Provider { get; }

    public CheckerOptions CheckerOptions { get; }

    public ParserOptions ParserOptions { get; }

    public CelEnvironment AddStandardLibrary()
    {
        _standardLibrary = true;
        StandardFunctions.AddTo(_functions);
        return this;
    }

    public CelEnvironment AddStringsExtension()
    {
        _stringsExtension = true;
        StringsExtension.AddTo(_functions);
        return this;
    }

    public CelEnvironment AddVariable(string name, CelType type)
    {
        _variables.Add(new VariableDecl(name, type));
        return this;
    }

    public CelEnvironment AddVariable(VariableDecl decl)
    {
        _variables.Add(decl);
        return this;
    }

    /// <summary>Declares a function for the checker and binds its runtime implementation.</summary>
    public CelEnvironment AddFunction(FunctionDecl decl, ICelFunction implementation)
    {
        _functionDecls.Add(decl);
        _functions.Add(implementation);
        return this;
    }

    internal CheckerEnv NewCheckerEnv()
    {
        var env = new CheckerEnv(Container, Provider, CheckerOptions);
        if (_standardLibrary) env.AddStandardLibrary();
        if (_stringsExtension) env.AddStringsExtension();
        foreach (var v in _variables) env.AddVariable(v);
        foreach (var f in _functionDecls) env.AddFunction(f);
        return env;
    }

    public ParseResult Parse(string expression) => Parser.Parse(expression, ParserOptions);

    public CheckResult Check(ParseResult parsed) => Checker.Checker.Check(parsed, NewCheckerEnv());

    /// <summary>Parses, checks and plans; throws <see cref="CelCompilationException"/> on any error.</summary>
    public CelProgram Compile(string expression, out CheckResult checkResult)
    {
        var parsed = Parse(expression);
        if (!parsed.IsSuccess)
            throw new CelCompilationException(parsed.Errors.FormatAll(), parsed.Errors.Errors);
        checkResult = Check(parsed);
        if (!checkResult.IsSuccess)
            throw new CelCompilationException(checkResult.Errors.FormatAll(), checkResult.Errors.Errors);
        return Planner.Plan(checkResult, Container, Provider, _functions, _messageFactories);
    }

    public CelProgram Compile(string expression) => Compile(expression, out _);

    /// <summary>Plans a parsed expression without checking it; names resolve at evaluation time.</summary>
    public CelProgram PlanUnchecked(ParseResult parsed) =>
        Planner.Plan(parsed, Container, Provider, _functions, _messageFactories);

    public CelProgram PlanChecked(CheckResult checked_) =>
        Planner.Plan(checked_, Container, Provider, _functions, _messageFactories);
}
