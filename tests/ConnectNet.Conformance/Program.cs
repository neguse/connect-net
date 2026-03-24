using System;
using System.Threading.Tasks;

namespace ConnectNet.Conformance;

class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            var mode = args.Length > 1 && args[0] == "--mode" ? args[1] : null;
            switch (mode)
            {
                case "server":
                    await ServerHarness.RunAsync();
                    return 0;
                case "client":
                    await ClientHarness.RunAsync();
                    return 0;
                default:
                    Console.Error.WriteLine("Usage: --mode server|client");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[harness] FATAL: {ex}");
            return 1;
        }
    }
}
