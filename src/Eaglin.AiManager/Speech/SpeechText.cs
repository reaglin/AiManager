using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Eaglin.AiManager.Speech;

/// <summary>
/// Splits long text into pieces Google text-to-speech accepts in one request (1.8.0). Google's
/// limit is 5,000 bytes of input (UTF-8) per request; pieces are kept under
/// <see cref="DefaultMaxBytes"/> to leave room. Text breaks at the end of a sentence where it can,
/// else after a comma, semicolon, colon or dash, else at a space, else (a very long word) between
/// characters — never inside a character. SSML breaks only between top-level elements and
/// between sentences of top-level text, and every piece is wrapped in its own &lt;speak&gt;.
/// </summary>
public static partial class SpeechText
{
    /// <summary>Google's per-request input limit, in UTF-8 bytes.</summary>
    public const int GoogleMaxBytes = 5000;

    /// <summary>The size pieces are kept under: Google's limit less room for the SSML wrapper.</summary>
    public const int DefaultMaxBytes = 4500;

    [GeneratedRegex(@"(?<=[.!?…。！？]['""”’)\]]*)\s+|\n\s*", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"(?<=[,;:—–])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex ClauseBreak();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceBreak();

    [GeneratedRegex(@"^\s*(<\?xml[^>]*\?>\s*)?<speak(\s[^>]*)?>(?<body>.*)</speak>\s*$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SpeakWrapper();

    public static int Bytes(string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>Plain text in pieces of at most <paramref name="maxBytes"/> UTF-8 bytes, in order, each trimmed; blank pieces dropped.</summary>
    public static IReadOnlyList<string> Split(string text, int maxBytes = DefaultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 8);
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0) return [];
        if (Bytes(text) <= maxBytes) return [text];
        return Pack(Atoms(text, maxBytes, level: 0), maxBytes);
    }

    /// <summary>
    /// SSML in pieces, each a whole &lt;speak&gt; document of at most <paramref name="maxBytes"/> bytes.
    /// Throws <see cref="AiException"/> when one top-level element is itself too long to send.
    /// </summary>
    public static IReadOnlyList<string> SplitSsml(string ssml, int maxBytes = DefaultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 32);
        const string open = "<speak>", close = "</speak>";
        var body = Unwrap(ssml).Trim();
        if (body.Length == 0) return [];
        var room = maxBytes - Bytes(open) - Bytes(close);
        if (Bytes(body) <= room) return [open + body + close];

        var atoms = new List<string>();
        foreach (var (part, isElement) in TopLevel(body))
        {
            if (!isElement) { atoms.AddRange(Atoms(part, room, 0)); continue; }
            if (Bytes(part) > room)
                throw new AiException(AiErrorKind.ProviderError, AiProviderType.GoogleTts,
                    $"One part of the SSML is longer than Google accepts in one request ({GoogleMaxBytes:N0} bytes): " +
                    $"\"{Shorten(part)}\". Break it into smaller parts.");
            atoms.Add(part);
        }
        return Pack(atoms, room).Select(p => open + p + close).ToList();
    }

    /// <summary>The inside of a &lt;speak&gt; document, or the text as it is when it has no wrapper.</summary>
    public static string Unwrap(string ssml)
    {
        var m = SpeakWrapper().Match(ssml ?? string.Empty);
        return m.Success ? m.Groups["body"].Value : ssml ?? string.Empty;
    }

    // ── Splitting ─────────────────────────────────────────────────────────────

    /// <summary>Pieces no larger than max, cut at the coarsest break that works (sentence, clause, space, character).</summary>
    private static List<string> Atoms(string text, int maxBytes, int level)
    {
        var result = new List<string>();
        if (Bytes(text) <= maxBytes) { result.Add(text); return result; }
        if (level >= 3) { result.AddRange(ByCharacters(text, maxBytes)); return result; }

        var re = level switch { 0 => SentenceBreak(), 1 => ClauseBreak(), _ => SpaceBreak() };
        int start = 0;
        foreach (Match m in re.Matches(text))
        {
            var end = m.Index + m.Length;          // the break's spaces stay with the piece before it
            Add(text[start..end]);
            start = end;
        }
        Add(text[start..]);
        return result;

        void Add(string seg)
        {
            if (seg.Length == 0) return;
            if (Bytes(seg) <= maxBytes) result.Add(seg);
            else result.AddRange(Atoms(seg, maxBytes, level + 1));
        }
    }

    private static IEnumerable<string> ByCharacters(string text, int maxBytes)
    {
        var sb = new StringBuilder();
        int bytes = 0;
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var el = (string)e.Current;
            var b = Bytes(el);
            if (bytes + b > maxBytes && sb.Length > 0) { yield return sb.ToString(); sb.Clear(); bytes = 0; }
            sb.Append(el); bytes += b;
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>Joins atoms, in order, into as few pieces as fit.</summary>
    private static List<string> Pack(IEnumerable<string> atoms, int maxBytes)
    {
        var pieces = new List<string>();
        var sb = new StringBuilder();
        int bytes = 0;
        foreach (var a in atoms)
        {
            var b = Bytes(a);
            if (bytes + b > maxBytes && sb.Length > 0) { Flush(); }
            sb.Append(a); bytes += b;
        }
        Flush();
        return pieces;

        void Flush()
        {
            var p = sb.ToString().Trim();
            if (p.Length > 0) pieces.Add(p);
            sb.Clear(); bytes = 0;
        }
    }

    /// <summary>SSML body as top-level parts: runs of text, and whole elements (with what is inside them).</summary>
    private static IEnumerable<(string Part, bool IsElement)> TopLevel(string body)
    {
        int i = 0, textStart = 0;
        while (i < body.Length)
        {
            if (body[i] != '<') { i++; continue; }
            if (i > textStart) yield return (body[textStart..i], false);
            int start = i, depth = 0;
            do
            {
                int gt = body.IndexOf('>', i);
                if (gt < 0) { i = body.Length; break; }
                var tag = body.AsSpan(i, gt - i + 1);
                if (tag.StartsWith("<!--") || tag.StartsWith("<?")) { }
                else if (tag.StartsWith("</")) depth--;
                else if (!tag.EndsWith("/>")) depth++;
                i = gt + 1;
                if (depth <= 0) break;
                int next = body.IndexOf('<', i);
                if (next < 0) { i = body.Length; break; }
                i = next;
            } while (true);
            yield return (body[start..i], true);
            textStart = i;
        }
        if (textStart < body.Length) yield return (body[textStart..], false);
    }

    private static string Shorten(string s) => s.Length <= 60 ? s : s[..60] + "…";
}

/// <summary>Joins the sound of several text-to-speech pieces into one playable file (1.8.0).</summary>
public static class AudioJoin
{
    public static byte[] Join(IReadOnlyList<byte[]> pieces, AiAudioFormat format) =>
        format == AiAudioFormat.Wav ? Wav(pieces) : Mp3(pieces);

    /// <summary>MP3 frames play end to end, so the pieces are concatenated; an ID3 tag at the start of a later piece is dropped.</summary>
    public static byte[] Mp3(IReadOnlyList<byte[]> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        if (pieces.Count == 1) return pieces[0];
        using var ms = new MemoryStream();
        for (int i = 0; i < pieces.Count; i++)
        {
            var p = pieces[i];
            var skip = i == 0 ? 0 : Id3Length(p);
            ms.Write(p, skip, p.Length - skip);
        }
        return ms.ToArray();
    }

    /// <summary>The length of an ID3v2 tag at the start of an MP3 (header, body and footer), or 0.</summary>
    public static int Id3Length(byte[] mp3)
    {
        if (mp3.Length < 10 || mp3[0] != (byte)'I' || mp3[1] != (byte)'D' || mp3[2] != (byte)'3') return 0;
        int size = (mp3[6] & 0x7F) << 21 | (mp3[7] & 0x7F) << 14 | (mp3[8] & 0x7F) << 7 | (mp3[9] & 0x7F);
        int total = 10 + size + ((mp3[5] & 0x10) != 0 ? 10 : 0);
        return Math.Min(total, mp3.Length);
    }

    /// <summary>One WAV: the first piece's format, every piece's samples in order, the sizes rewritten.</summary>
    public static byte[] Wav(IReadOnlyList<byte[]> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        if (pieces.Count == 0) return [];
        byte[]? fmt = null;
        using var data = new MemoryStream();
        foreach (var p in pieces)
        {
            var (f, d) = ReadWav(p);
            fmt ??= f;
            data.Write(d.Span);
        }
        fmt ??= DefaultFormat();

        var pcm = data.ToArray();
        using var outStream = new MemoryStream();
        var w = new BinaryWriter(outStream);
        w.Write("RIFF"u8);
        w.Write(4 + 8 + fmt.Length + (fmt.Length & 1) + 8 + pcm.Length + (pcm.Length & 1));
        w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(fmt.Length); w.Write(fmt); if ((fmt.Length & 1) != 0) w.Write((byte)0);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm); if ((pcm.Length & 1) != 0) w.Write((byte)0);
        w.Flush();
        return outStream.ToArray();
    }

    /// <summary>The "fmt " chunk body (null when absent) and the samples. Bytes without a RIFF header are taken as samples.</summary>
    internal static (byte[]? Fmt, ReadOnlyMemory<byte> Data) ReadWav(byte[] wav)
    {
        if (wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return (null, wav);
        byte[]? fmt = null;
        ReadOnlyMemory<byte> data = ReadOnlyMemory<byte>.Empty;
        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.AsSpan(pos, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos + 4, 4));
            int body = pos + 8;
            int len = (int)Math.Min(size, wav.Length - body);
            if (id.SequenceEqual("fmt "u8)) fmt = wav.AsSpan(body, len).ToArray();
            else if (id.SequenceEqual("data"u8)) { data = wav.AsMemory(body, len); break; }
            pos = body + len + (len & 1);
        }
        return (fmt, data);
    }

    /// <summary>16-bit mono PCM at 24 kHz — Google's usual LINEAR16 — for samples that came without a header.</summary>
    private static byte[] DefaultFormat()
    {
        var f = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0), 1);          // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(2), 1);          // mono
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(4), 24000);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(8), 48000);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(14), 16);
        return f;
    }
}
