namespace TSC.Ares;

/// <summary>
/// The fixed ARES metadata used by all editor saves and exports. Chart content
/// stays editable, while opaque ARES metadata is emitted in one consistent form.
/// </summary>
public static class AresMetadataDefaults
{
    public const uint RawMagic = 901785156u;
    public const int TrackCount = 64;
    public const string HeaderTemplateBase64 = "QVJFU9tuTjGEIwpjo7VlcEMZqoDA0ROPUa3BNw==";

    private static readonly byte[] HeaderTemplateBytes = Convert.FromBase64String(HeaderTemplateBase64);

    public static byte[] GetHeaderTemplateBytes() => HeaderTemplateBytes.ToArray();

    public static void Apply(AresChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        chart.RawMagic = RawMagic;
        chart.AresTrackCount = TrackCount;
        chart.InstrumentNames.Clear();
        chart.InstrumentNames.Add("custom.ogg");
        chart.InstrumentNameHuffmanFrequencies.Clear();
        chart.InstrumentNameHuffmanFrequencies.Add(1u);
        chart.InstrumentNameReferences.Clear();
        chart.Instruments.Clear();
        chart.Instruments.Add(new AresInstrumentDefinition(1, false, uint.MaxValue));
        chart.AresHeaderTemplate = GetHeaderTemplateBytes();
    }
}
