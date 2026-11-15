namespace CryptArc.Core.Sarc;

/// <summary>
/// The failure codes of FORMAT_SPEC_P2 §5. Every rejected SARC volume is reported with
/// exactly one of them, so a reader can be checked against <c>expected_failures.json</c>.
/// </summary>
public enum SarcErrorCode
{
    /// <summary><c>E_SHORT</c>: the volume is shorter than 73 bytes (header plus tag).</summary>
    Short,

    /// <summary><c>E_MAGIC</c>: the magic is not <c>SARC</c> or the version is not 1.</summary>
    Magic,

    /// <summary><c>E_LENGTH</c>: the volume length disagrees with <c>ciphertext_len</c>.</summary>
    Length,

    /// <summary><c>E_HMAC</c>: the tag does not match: altered data, altered tag or a wrong password.</summary>
    Hmac,

    /// <summary>
    /// <c>E_PAYLOAD</c>: non-zero reserved bits, unknown flags, <c>file_type</c> above 6,
    /// section lengths that disagree, trailing bytes, an unsafe file name.
    /// </summary>
    Payload,

    /// <summary>
    /// <c>E_CODE</c>: a code longer than 15 bits, a broken Kraft equality, a symbol outside
    /// its alphabet, a reference before the start of the output, non-zero padding.
    /// </summary>
    Code,

    /// <summary><c>E_CRC</c>: the CRC-32 of a reassembled file does not match the recorded one.</summary>
    Crc,

    /// <summary><c>E_VOLUMES</c>: the set of volumes is incomplete, has duplicates or mixes archives.</summary>
    Volumes,
}

/// <summary>Conversions for <see cref="SarcErrorCode"/>.</summary>
public static class SarcErrorCodes
{
    /// <summary>The code as the specification spells it, e.g. <c>E_HMAC</c>.</summary>
    /// <param name="code">The failure.</param>
    /// <returns>The specification's name for it.</returns>
    public static string ToSpecName(this SarcErrorCode code) => code switch
    {
        SarcErrorCode.Short => "E_SHORT",
        SarcErrorCode.Magic => "E_MAGIC",
        SarcErrorCode.Length => "E_LENGTH",
        SarcErrorCode.Hmac => "E_HMAC",
        SarcErrorCode.Payload => "E_PAYLOAD",
        SarcErrorCode.Code => "E_CODE",
        SarcErrorCode.Crc => "E_CRC",
        SarcErrorCode.Volumes => "E_VOLUMES",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };
}

/// <summary>
/// A SARC volume or volume set was rejected, with the specification's code. SARC decoders
/// throw this where the rest of the project throws <see cref="InvalidDataException"/>: that
/// type is sealed, so it cannot carry the code that <c>expected_failures.json</c> checks.
/// </summary>
public sealed class SarcException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">Why the input was rejected.</param>
    /// <param name="message">What exactly was wrong, for a person.</param>
    public SarcException(SarcErrorCode code, string message)
        : base($"{code.ToSpecName()}: {message}")
    {
        Code = code;
    }

    /// <summary>Why the input was rejected.</summary>
    public SarcErrorCode Code { get; }

    /// <summary>The code as the specification spells it, e.g. <c>E_HMAC</c>.</summary>
    public string SpecName => Code.ToSpecName();
}
