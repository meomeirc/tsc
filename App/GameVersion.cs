using System.Reflection;

namespace TSC;

/// <summary>Application version loaded from the embedded, editable version.txt file.</summary>
internal static class GameVersion
{
    private static readonly Lazy<string> LoadedVersion = new(Load);

    public static string Current => LoadedVersion.Value;

    private static string Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TSC.version.txt");
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                var value = reader.ReadToEnd().Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        catch { }
        return "0.0.1";
    }
}
