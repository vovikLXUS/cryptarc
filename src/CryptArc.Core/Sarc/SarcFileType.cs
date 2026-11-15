namespace CryptArc.Core.Sarc;

/// <summary>
/// The seven corpus groups of Project 2, stored as <c>file_type</c> (FORMAT_SPEC_P2 §2.2).
/// A value above 6 makes a volume invalid.
/// </summary>
public enum SarcFileType : byte
{
    /// <summary>0 — natural language text.</summary>
    NaturalText = 0,

    /// <summary>1 — source code.</summary>
    SourceCode = 1,

    /// <summary>2 — structured data: CSV, JSON, logs.</summary>
    StructuredData = 2,

    /// <summary>3 — uncompressed raster: BMP, PGM.</summary>
    UncompressedRaster = 3,

    /// <summary>4 — executable: .exe, .so.</summary>
    Executable = 4,

    /// <summary>5 — already compressed: JPEG, MP3, ZIP.</summary>
    AlreadyCompressed = 5,

    /// <summary>6 — synthetic data: zeros, random noise.</summary>
    Synthetic = 6,
}
