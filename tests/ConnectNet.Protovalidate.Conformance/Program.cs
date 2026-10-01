using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Buf.Validate.Conformance.Harness;
using ConnectNet.Validation;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

// The conformance executor: one TestConformanceRequest in on stdin, one
// TestConformanceResponse out on stdout, both in binary form. The case messages arrive as
// Any values; their types are the generated classes of the pinned conformance protos
// compiled into this assembly, so the request's FileDescriptorSet only has to agree with
// them.
var input = ReadAll(Console.OpenStandardInput());
var request = TestConformanceRequest.Parser.ParseFrom(input);

var registry = TypeRegistry.FromFiles(GeneratedFiles(typeof(Program).Assembly));
var validator = new ProtoValidator();
var response = new TestConformanceResponse();

foreach (var (name, any) in request.Cases)
{
    response.Results[name] = RunCase(any);
}

var output = Console.OpenStandardOutput();
response.WriteTo(output);
output.Flush();
return 0;

TestResult RunCase(Any any)
{
    var typeName = Any.GetTypeName(any.TypeUrl);
    var descriptor = registry.Find(typeName);
    if (descriptor == null)
        return new TestResult { UnexpectedError = $"unknown message type {typeName}" };

    IMessage message;
    try
    {
        message = descriptor.Parser.ParseFrom(any.Value);
    }
    catch (Exception e)
    {
        return new TestResult { UnexpectedError = $"failed to parse {typeName}: {e.Message}" };
    }

    try
    {
        var result = validator.Validate(message);
        if (result.IsValid)
            return new TestResult { Success = true };
        var violations = new Buf.Validate.Violations();
        foreach (var violation in result.Violations)
            violations.Violations_.Add(violation.ToProto());
        return new TestResult { ValidationError = violations };
    }
    catch (ValidationCompilationException e)
    {
        return new TestResult { CompilationError = e.Message };
    }
    catch (ValidationEvaluationException e)
    {
        return new TestResult { RuntimeError = e.Message };
    }
    catch (Exception e)
    {
        return new TestResult { UnexpectedError = e.ToString() };
    }
}

static byte[] ReadAll(Stream stream)
{
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
}

/// <summary>Every file descriptor generated into an assembly (the <c>*Reflection.Descriptor</c> properties).</summary>
static IEnumerable<FileDescriptor> GeneratedFiles(Assembly assembly)
{
    foreach (var type in assembly.GetTypes())
    {
        if (!type.IsAbstract || !type.IsSealed) continue; // static classes only
        var property = type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static);
        if (property?.PropertyType == typeof(FileDescriptor) && property.GetValue(null) is FileDescriptor file)
            yield return file;
    }
}

internal static partial class Program
{
}
