namespace ConnectNet;

public class ConnectErrorDetail
{
    public string Type { get; }
    public byte[] Value { get; }

    public ConnectErrorDetail(string type, byte[] value)
    {
        Type = type;
        Value = value;
    }
}
