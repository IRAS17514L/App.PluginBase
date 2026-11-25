using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PotatoVN.App.PluginBase.Guides;

public enum LocalGuideEncoding
{
    Auto, Utf8, Utf16Le, Utf16Be, Gb18030, ShiftJis, Big5, EucJp, Windows1252, Utf32Le, Utf32Be, Dos437,
}

public sealed record LocalGuideMount(string Path, bool InGameFolder, LocalGuideEncoding Encoding,
    GuideBookmark? Bookmark = null);

internal sealed record LocalGuideCandidate(string Path, bool RememberedByHost);

internal static class LocalGuideFile
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxCandidates = 24;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "", ".txt", ".text", ".md", ".markdown", ".log", ".nfo", ".rst", ".org", ".adoc", ".asc", ".csv", ".tsv",
    };
    private static readonly string[] GuideNames =
        ["攻略", "走法", "路线", "路線", "流程", "チャート", "walkthrough", "guide", "solution"];

    internal static IReadOnlyList<(LocalGuideEncoding Value, string Label)> Encodings { get; } =
    [
        (LocalGuideEncoding.Auto, "自动识别"),
        (LocalGuideEncoding.Utf8, "UTF-8"),
        (LocalGuideEncoding.Gb18030, "GB18030 / GBK"),
        (LocalGuideEncoding.ShiftJis, "Shift-JIS（CP932）"),
        (LocalGuideEncoding.Big5, "Big5"),
        (LocalGuideEncoding.EucJp, "EUC-JP"),
        (LocalGuideEncoding.Utf16Le, "UTF-16 LE"),
        (LocalGuideEncoding.Utf16Be, "UTF-16 BE"),
        (LocalGuideEncoding.Utf32Le, "UTF-32 LE"),
        (LocalGuideEncoding.Utf32Be, "UTF-32 BE"),
        (LocalGuideEncoding.Windows1252, "Windows-1252"),
        (LocalGuideEncoding.Dos437, "DOS / CP437"),
    ];

    internal static LocalGuideMount Mount(string path, string? gameFolder, LocalGuideEncoding encoding)
    {
        string fullPath = Path.GetFullPath(path);
        if (TryFullPath(gameFolder) is { } folder)
        {
            string relative = Path.GetRelativePath(folder, fullPath);
            if (IsContained(relative)) return new LocalGuideMount(relative, true, encoding);
        }
        return new LocalGuideMount(fullPath, false, encoding);
    }

    internal static string? Resolve(LocalGuideMount mount, string? gameFolder)
    {
        if (!mount.InGameFolder) return TryFullPath(mount.Path);
        if (TryFullPath(gameFolder) is not { } folder || string.IsNullOrWhiteSpace(mount.Path) ||
            Path.IsPathRooted(mount.Path)) return null;
        string? fullPath = TryFullPath(Path.Combine(folder, mount.Path));
        return fullPath is not null && IsContained(Path.GetRelativePath(folder, fullPath)) ? fullPath : null;
    }

    private static bool IsContained(string relative) =>
        !Path.IsPathRooted(relative) && relative is not ("." or "..") &&
        !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string? TryFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    internal static IReadOnlyList<LocalGuideCandidate> Discover(string? gameFolder, string? hostText, CancellationToken token)
    {
        var results = new List<LocalGuideCandidate>();
        string? root = TryFullPath(gameFolder);
        if (!string.IsNullOrWhiteSpace(hostText) && !Path.IsPathRooted(hostText) && root is not null)
            hostText = Path.Combine(root, hostText);
        string? remembered = TryFullPath(hostText);
        if (remembered is not null && TextExtensions.Contains(Path.GetExtension(remembered)) && File.Exists(remembered))
            results.Add(new LocalGuideCandidate(remembered, true));

        if (root is not null)
        {
            var files = new List<string>();
            try
            {
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
                };
                foreach (string path in Directory.EnumerateFiles(root, "*", options).Take(2048))
                {
                    token.ThrowIfCancellationRequested();
                    if (TextExtensions.Contains(Path.GetExtension(path)) &&
                        !string.Equals(path, remembered, StringComparison.OrdinalIgnoreCase))
                        files.Add(path);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            results.AddRange(files
                .OrderByDescending(path => GuideNames.Any(term => Path.GetFileNameWithoutExtension(path)
                    .Contains(term, StringComparison.OrdinalIgnoreCase)))
                .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Take(MaxCandidates - results.Count)
                .Select(path => new LocalGuideCandidate(path, false)));
        }
        token.ThrowIfCancellationRequested();
        return results;
    }

    internal static async Task<string> ReadAsync(string path, LocalGuideEncoding encoding, CancellationToken token)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = source.Length;
        if (length > MaxBytes) throw new InvalidDataException("本地攻略超过 4 MB，请选择较小的文本文件。");

        using var buffer = new MemoryStream((int)length);
        var chunk = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaxBytes)
                throw new InvalidDataException("本地攻略超过 4 MB，请选择较小的文本文件。");
            buffer.Write(chunk, 0, count);
        }

        token.ThrowIfCancellationRequested();
        string text = Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), encoding);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("这份攻略没有正文。");
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\f', '\n')
            .Replace('\u2028', '\n').Replace('\u2029', '\n');
    }

    internal static string Decode(ReadOnlySpan<byte> bytes, LocalGuideEncoding requested)
    {
        if (IsDocumentContainer(bytes))
            throw new InvalidDataException("请选择纯文本攻略。压缩包请先解压，PDF 或 Word 可先另存为文本。");

        (LocalGuideEncoding bomEncoding, int bomLength) = ReadBom(bytes);
        if (requested != LocalGuideEncoding.Auto)
            return TryDecode(bomEncoding == requested ? bytes[bomLength..] : bytes, requested) ??
                throw DecodeError();
        if (bomLength > 0)
            return TryDecode(bytes[bomLength..], bomEncoding) ?? throw DecodeError();
        if (TryDecode(bytes, LocalGuideEncoding.Utf8) is { } utf8) return utf8;
        if (DetectUtf16(bytes) is { } utf16 && TryDecode(bytes, utf16) is { } unicode) return unicode;

        string? chinese = TryDecode(bytes, LocalGuideEncoding.Gb18030);
        string? japanese = TryDecode(bytes, LocalGuideEncoding.ShiftJis);
        string? euc = TryDecode(bytes, LocalGuideEncoding.EucJp);
        if (HasKanaRun(japanese)) return japanese!;
        if (HasKanaRun(euc)) return euc!;
        return chinese ?? japanese ?? TryDecode(bytes, LocalGuideEncoding.Big5) ?? euc ?? throw DecodeError();
    }

    private static InvalidDataException DecodeError() =>
        new("无法识别文本。请换用另一种文件编码，或将攻略另存为 UTF-8。");

    private static bool IsDocumentContainer(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith("PK\u0003\u0004"u8) || bytes.StartsWith("Rar!\u001a\u0007"u8) ||
        bytes.StartsWith(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }) ||
        bytes.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }) ||
        bytes.StartsWith("%PDF-"u8) || bytes.StartsWith("{\\rtf"u8);

    private static (LocalGuideEncoding Encoding, int Length) ReadBom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Encoding.UTF32.Preamble)) return (LocalGuideEncoding.Utf32Le, 4);
        if (bytes.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) return (LocalGuideEncoding.Utf32Be, 4);
        if (bytes.StartsWith(Encoding.UTF8.Preamble)) return (LocalGuideEncoding.Utf8, 3);
        if (bytes.StartsWith(Encoding.Unicode.Preamble)) return (LocalGuideEncoding.Utf16Le, 2);
        if (bytes.StartsWith(Encoding.BigEndianUnicode.Preamble)) return (LocalGuideEncoding.Utf16Be, 2);
        return default;
    }

    private static LocalGuideEncoding? DetectUtf16(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes.Length % 2 != 0) return null;
        int even = 0;
        int odd = 0;
        int size = Math.Min(bytes.Length, 512);
        for (int i = 0; i < size; i += 2)
        {
            if (bytes[i] == 0) even++;
            if (bytes[i + 1] == 0) odd++;
        }
        if (odd >= 2 && odd > even * 4) return LocalGuideEncoding.Utf16Le;
        if (even >= 2 && even > odd * 4) return LocalGuideEncoding.Utf16Be;
        return null;
    }

    private static bool HasKanaRun(string? text)
    {
        int run = 0;
        foreach (char c in text ?? "")
        {
            run = c is >= '\u3041' and <= '\u3096' or >= '\u30A1' and <= '\u30FA' ? run + 1 : 0;
            if (run >= 2) return true;
        }
        return false;
    }

    private static string? TryDecode(ReadOnlySpan<byte> bytes, LocalGuideEncoding choice)
    {
        Encoding? encoding = choice switch
        {
            LocalGuideEncoding.Utf8 => new UTF8Encoding(false, true),
            LocalGuideEncoding.Utf16Le => new UnicodeEncoding(false, false, true),
            LocalGuideEncoding.Utf16Be => new UnicodeEncoding(true, false, true),
            LocalGuideEncoding.Utf32Le => new UTF32Encoding(false, false, true),
            LocalGuideEncoding.Utf32Be => new UTF32Encoding(true, false, true),
            LocalGuideEncoding.Gb18030 => CodePage(54936),
            LocalGuideEncoding.ShiftJis => CodePage(932),
            LocalGuideEncoding.Big5 => CodePage(950),
            LocalGuideEncoding.EucJp => CodePage(51932),
            LocalGuideEncoding.Windows1252 => CodePage(1252),
            LocalGuideEncoding.Dos437 => CodePage(437),
            _ => null,
        };
        try
        {
            string? text = encoding?.GetString(bytes).TrimEnd('\u001a');
            return text is not null && !text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n' or '\f'))
                ? text
                : null;
        }
        catch (DecoderFallbackException) { return null; }
    }

    private static Encoding? CodePage(int page) => CodePagesEncodingProvider.Instance.GetEncoding(
        page, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
}
