using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using uplink.NET.IntegrationTests.Infrastructure;
using uplink.NET.Native;

namespace uplink.NET.IntegrationTests;

/// <summary>
/// Validates every P/Invoke struct in <see cref="UplinkInterop"/> has the same
/// size and field offsets as the C struct it mirrors.
///
/// A layout mismatch between C# and native headers silently corrupts memory on
/// every call that touches the affected struct — the kind of bug that surfaces
/// as intermittent SIGSEGV far from the actual cause. Catching it at test time
/// is much cheaper than chasing it in production core dumps.
///
/// The authoritative values come from a CSV produced by compiling
/// <c>scripts/ci/struct_layout_check.c</c> against the uplink-c headers in CI
/// and running it against the same libuplink we load at runtime. The file path
/// is passed in via the <c>UPLINK_NATIVE_STRUCT_LAYOUT</c> environment
/// variable; if it's not set, the test is skipped (keeping local <c>dotnet
/// test</c> runs green when no CSV is generated).
///
/// CSV format, one line per size or field:
///   &lt;StructName&gt;,SIZE,&lt;sizeof&gt;
///   &lt;StructName&gt;,&lt;field&gt;,&lt;offset&gt;
/// </summary>
public class NativeStructLayoutTests
{
    // C field name → C# field name for the handful of places they diverge.
    // C uses reserved C# identifiers (object, string) which we can't bind to
    // a matching field name on the managed side.
    private static readonly Dictionary<string, string> FieldNameOverrides = new(StringComparer.Ordinal)
    {
        ["object"] = "object_",
        ["string"] = "stringValue",
    };

    [NativeStructLayoutFact]
    public void Layout_matches_native_headers()
    {
        var csvPath = Environment.GetEnvironmentVariable(NativeStructLayoutFactAttribute.LayoutPathVariable)!;
        var entries = ParseLayout(csvPath);

        // Every struct the C side emitted must exist on the C# side.
        // (Extra C# structs without a C counterpart are allowed — some are
        // pure-managed scratch types, but currently all our P/Invoke structs
        // have a native equivalent.)
        var interopTypes = typeof(UplinkInterop)
            .GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
            .Where(t => t.IsValueType)
            .ToDictionary(t => t.Name, StringComparer.Ordinal);

        var failures = new List<string>();

        foreach (var (structName, expected) in entries)
        {
            if (!interopTypes.TryGetValue(structName, out var type))
            {
                failures.Add(
                    $"native struct '{structName}' has no C# counterpart in UplinkInterop");
                continue;
            }

            var actualSize = Marshal.SizeOf(type);
            if (actualSize != expected.Size)
            {
                failures.Add(
                    $"{structName}: size mismatch — C header={expected.Size}, " +
                    $"Marshal.SizeOf<{type.Name}>()={actualSize}");
            }

            foreach (var (fieldName, expectedOffset) in expected.Fields)
            {
                var csharpFieldName = FieldNameOverrides.TryGetValue(fieldName, out var mapped)
                    ? mapped : fieldName;

                var field = type.GetField(csharpFieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field is null)
                {
                    failures.Add(
                        $"{structName}.{fieldName}: no matching C# field '{csharpFieldName}' on {type.Name}");
                    continue;
                }

                var actualOffset = (int)Marshal.OffsetOf(type, csharpFieldName);
                if (actualOffset != expectedOffset)
                {
                    failures.Add(
                        $"{structName}.{fieldName}: offset mismatch — C header={expectedOffset}, " +
                        $"Marshal.OffsetOf<{type.Name}>({csharpFieldName})={actualOffset}");
                }
            }
        }

        Assert.True(failures.Count == 0,
            "Native struct layout mismatches detected:\n  " + string.Join("\n  ", failures));
    }

    private sealed record ExpectedLayout(int Size, List<(string Field, int Offset)> Fields);

    private static Dictionary<string, ExpectedLayout> ParseLayout(string path)
    {
        var result = new Dictionary<string, ExpectedLayout>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var parts = line.Split(',', 3);
            if (parts.Length != 3)
                throw new FormatException($"Malformed layout line: '{raw}'");

            var structName = parts[0];
            var fieldOrMarker = parts[1];
            var value = int.Parse(parts[2], CultureInfo.InvariantCulture);

            if (!result.TryGetValue(structName, out var layout))
            {
                layout = new ExpectedLayout(Size: -1, Fields: []);
                result[structName] = layout;
            }

            if (fieldOrMarker == "SIZE")
            {
                result[structName] = layout with { Size = value };
            }
            else
            {
                layout.Fields.Add((fieldOrMarker, value));
            }
        }

        foreach (var (name, layout) in result)
        {
            if (layout.Size < 0)
                throw new FormatException($"Layout for '{name}' has no SIZE entry");
        }

        return result;
    }
}
