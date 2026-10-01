using System;
using System.Collections;
using ConnectNet.Validation.Cel.Runtime;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Cel.Protobuf;

/// <summary>A protobuf message as a CEL value.</summary>
internal sealed class ProtoMessageValue : MessageValue
{
    public ProtoMessageValue(ProtoTypeProvider provider, IMessage message)
    {
        Provider = provider;
        Message = message;
    }

    public ProtoTypeProvider Provider { get; }

    public IMessage Message { get; }

    public MessageDescriptor Descriptor => Message.Descriptor;

    public override string TypeName => Message.Descriptor.FullName;

    public override CelValue GetField(string name)
    {
        var field = Provider.FindField(Descriptor, name);
        if (field == null)
            return new ErrorValue("no such field '" + name + "'");
        return ProtoValues.FieldToCel(Provider, Message, field);
    }

    public override CelValue HasField(string name)
    {
        var field = Provider.FindField(Descriptor, name);
        if (field == null)
            return new ErrorValue("no such field '" + name + "'");
        return BoolValue.Of(ProtoValues.IsSet(Message, field));
    }

    public override bool EqualsValue(CelValue other) =>
        other is ProtoMessageValue m && ProtoEquality.Equal(Provider, Message, m.Message);

    public override string ToString() => TypeName + "{" + Message + "}";
}

/// <summary>
/// Protobuf message equality as CEL defines it: same type, same set fields, IEEE doubles
/// (NaN never equal), unpacked <c>Any</c> comparison, repeated in order and maps unordered.
/// </summary>
internal static class ProtoEquality
{
    public static bool Equal(ProtoTypeProvider provider, IMessage x, IMessage y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x.Descriptor != y.Descriptor) return false;

        if (x is Google.Protobuf.WellKnownTypes.Any ax && y is Google.Protobuf.WellKnownTypes.Any ay)
        {
            if (ax.TypeUrl != ay.TypeUrl) return false;
            if (ax.Value.Equals(ay.Value)) return true;
            var ux = ProtoValues.TryUnpack(provider, ax);
            var uy = ProtoValues.TryUnpack(provider, ay);
            return ux != null && uy != null && Equal(provider, ux, uy);
        }

        foreach (var field in AllFields(provider, x.Descriptor))
        {
            bool setX = ProtoValues.IsSet(x, field);
            bool setY = ProtoValues.IsSet(y, field);
            if (setX != setY) return false;
            if (!setX) continue;
            if (!FieldEqual(provider, field, field.Accessor.GetValue(x), field.Accessor.GetValue(y)))
                return false;
        }
        return true;
    }

    private static System.Collections.Generic.IEnumerable<FieldDescriptor> AllFields(ProtoTypeProvider provider, MessageDescriptor d)
    {
        foreach (var f in d.Fields.InDeclarationOrder())
            yield return f;
        foreach (var ext in provider.ExtensionsOf(d))
            yield return ext;
    }

    private static bool FieldEqual(ProtoTypeProvider provider, FieldDescriptor field, object? x, object? y)
    {
        if (field.IsMap)
        {
            var mx = (IDictionary)x!;
            var my = (IDictionary)y!;
            if (mx.Count != my.Count) return false;
            var valueField = field.MessageType.FindFieldByNumber(2);
            foreach (DictionaryEntry entry in mx)
            {
                if (!my.Contains(entry.Key)) return false;
                if (!ValueEqual(provider, valueField, entry.Value, my[entry.Key])) return false;
            }
            return true;
        }
        if (field.IsRepeated)
        {
            var lx = (IList)x!;
            var ly = (IList)y!;
            if (lx.Count != ly.Count) return false;
            for (int i = 0; i < lx.Count; i++)
            {
                if (!ValueEqual(provider, field, lx[i], ly[i])) return false;
            }
            return true;
        }
        return ValueEqual(provider, field, x, y);
    }

    private static bool ValueEqual(ProtoTypeProvider provider, FieldDescriptor field, object? x, object? y)
    {
        if (x == null || y == null) return x == null && y == null;
        switch (field.FieldType)
        {
            case FieldType.Message:
            case FieldType.Group:
                if (x is IMessage mx && y is IMessage my)
                    return Equal(provider, mx, my);
                // Wrapper-typed fields are represented by nullable primitives.
                return x.Equals(y);
            case FieldType.Double:
                return (double)x == (double)y;
            case FieldType.Float:
                return (float)x == (float)y;
            case FieldType.Bytes:
                return ((ByteString)x).Equals((ByteString)y);
            default:
                return x.Equals(y);
        }
    }
}
