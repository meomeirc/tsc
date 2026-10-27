namespace TSC.Ares;

/// <summary>One raw file entry used when constructing an FPK bundle.</summary>
public sealed record FpkWriteEntry(string Name, byte[] Data);
