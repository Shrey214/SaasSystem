using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HotelSaas.BuildingBlocks.Persistence.Errors;

// Finds the first stack frame that belongs to us.
public static class FaultLocator
{
    private const string OwnAssemblyPrefix = "HotelSaas.";

    public sealed record FaultLocation(string Assembly, string Type, string Method, string? File, int? Line);

    // Walks the stack for the first HotelSaas.* frame.
    //
    // The topmost frame of a real failure is nearly always framework code -
    // Npgsql.NpgsqlConnector, System.Text.Json - which is true and useless.
    // The first frame in our own assemblies is the line a developer can
    // actually go and look at.
    public static FaultLocation? Locate(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // fNeedFileInfo requires the .pdb next to the assembly, which is why
        // DebugType is portable and pdbs ship with the container image.
        StackTrace trace = new(exception, fNeedFileInfo: true);

        foreach (StackFrame frame in trace.GetFrames())
        {
            System.Reflection.MethodBase? method = frame.GetMethod();
            string? assembly = method?.DeclaringType?.Assembly.GetName().Name;

            if (assembly is null || !assembly.StartsWith(OwnAssemblyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            int line = frame.GetFileLineNumber();

            return new FaultLocation(
                assembly,
                method!.DeclaringType!.FullName ?? method.DeclaringType.Name,
                method.Name,
                frame.GetFileName(),
                line == 0 ? null : line);
        }

        return null;
    }

    // Groups repeats of the same bug. Deliberately excludes the message,
    // which often contains ids and would make every occurrence unique.
    public static string Fingerprint(Exception exception, FaultLocation? location)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string material = string.Join(
            '|',
            exception.GetType().FullName ?? "unknown",
            location?.Type ?? "unknown",
            location?.Method ?? "unknown",
            location?.Line?.ToString(CultureInfo.InvariantCulture) ?? "0");

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(hash)[..16];
    }

    // The full inner-exception chain. The outermost message is often a
    // generic wrapper and the real cause is three levels down.
    public static string? DescribeInnerExceptions(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        List<string> chain = [];
        Exception? inner = exception.InnerException;
        int depth = 0;

        while (inner is not null && depth < 10)
        {
            chain.Add($"{inner.GetType().FullName}: {inner.Message}");
            inner = inner.InnerException;
            depth++;
        }

        return chain.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(chain);
    }
}
