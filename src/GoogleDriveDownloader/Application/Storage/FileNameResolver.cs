using System.Globalization;
using System.Text;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Drive;

namespace GoogleDriveDownloader.Application.Storage;

public sealed class FileNameResolver
{
    public const int MaximumFileNameLength = 255;
    private const string InvalidCharacters = "<>:\"/\\|?*";
    private static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "rtf", "txt", "csv", "tsv",
        "json", "xml", "html", "htm", "md", "log", "ini", "yaml", "yml", "zip", "7z", "rar", "tar", "gz", "bz2", "xz",
        "jpg", "jpeg", "png", "gif", "webp", "bmp", "tif", "tiff", "svg", "ico", "heic", "mp3", "wav", "ogg", "flac",
        "mp4", "avi", "mov", "mkv", "webm", "exe", "dll", "msi", "iso", "epub"
    };
    private static readonly Dictionary<string, string> MimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = ".pdf", [ExportSettings.Docx] = ".docx", [ExportSettings.Xlsx] = ".xlsx", [ExportSettings.Pptx] = ".pptx",
        ["application/msword"] = ".doc", ["application/vnd.ms-excel"] = ".xls", ["application/vnd.ms-powerpoint"] = ".ppt",
        ["application/zip"] = ".zip", ["application/x-7z-compressed"] = ".7z", ["application/vnd.rar"] = ".rar",
        ["application/gzip"] = ".gz", ["application/x-tar"] = ".tar", ["application/json"] = ".json",
        ["application/xml"] = ".xml", ["text/xml"] = ".xml", ["text/csv"] = ".csv", ["text/html"] = ".html",
        ["image/jpeg"] = ".jpg", ["image/png"] = ".png", ["image/gif"] = ".gif", ["image/webp"] = ".webp",
        ["image/tiff"] = ".tif", ["image/bmp"] = ".bmp", ["image/svg+xml"] = ".svg",
        ["audio/mpeg"] = ".mp3", ["audio/wav"] = ".wav", ["video/mp4"] = ".mp4", ["video/webm"] = ".webm"
    };

    public string Resolve(string? requestedName, DriveFileMetadata metadata, DriveTransferFormat format,
        int maxLength = MaximumFileNameLength)
    {
        var extension = GetExtension(metadata, format);
        var name = Sanitize(requestedName ?? "");
        var removedCorrectExtension = false;
        if (extension.Length != 0)
        {
            // Повторений правильний суфікс теж прибираємо: «звіт.pdf.pdf» стає «звіт.pdf».
            while (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                removedCorrectExtension = true;
                name = name[..^extension.Length].TrimEnd(' ', '.');
            }
        }
        var oldExtension = Path.GetExtension(name);
        if (!removedCorrectExtension && oldExtension.Length > 1 && (KnownExtensions.Contains(oldExtension[1..]) ||
            oldExtension.Equals(NormalizeExtension(metadata.FileExtension), StringComparison.OrdinalIgnoreCase)))
        {
            name = name[..^oldExtension.Length];
        }
        name = ProtectBase(name);
        return Compose(name, extension, "", maxLength);
    }

    public string GetCandidate(string fileName, int sequence = 1, int maxLength = MaximumFileNameLength)
    {
        ValidateFileName(fileName);
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        var extension = Path.GetExtension(fileName);
        var stem = extension.Length == 0 ? fileName : fileName[..^extension.Length];
        var suffix = sequence == 1 ? "" : " (" + sequence.ToString(CultureInfo.InvariantCulture) + ")";
        return Compose(stem, extension, suffix, maxLength);
    }

    public static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." ||
            fileName.EndsWith(' ') || fileName.EndsWith('.') || fileName.Any(IsInvalid) || IsReserved(fileName) ||
            !HasValidSurrogates(fileName))
            throw new FileStorageException(FileStorageErrorCode.InvalidName, "Потрібне безпечне ім'я файла без шляху, заборонених символів і зарезервованих назв Windows.");
    }

    private static string GetExtension(DriveFileMetadata metadata, DriveTransferFormat format)
    {
        if (format.IsExport)
        {
            var expected = format.MimeType switch
            {
                ExportSettings.Docx => ".docx", ExportSettings.Xlsx => ".xlsx", ExportSettings.Pptx => ".pptx", _ => null
            };
            if (expected is null || !expected.Equals(NormalizeExtension(format.ExportExtension), StringComparison.OrdinalIgnoreCase))
                throw new FileStorageException(FileStorageErrorCode.InvalidName, "Непідтримуваний формат або розширення експорту.");
            return expected;
        }
        var fromMetadata = NormalizeExtension(metadata.FileExtension);
        var mime = metadata.MimeType.Split(';')[0].Trim();
        if (MimeExtensions.TryGetValue(mime, out var known))
        {
            // Обидва варіанти є коректними розширеннями одного MIME, тому не змінюємо метадані без потреби.
            if ((known == ".jpg" && fromMetadata == ".jpeg") || (known == ".tif" && fromMetadata == ".tiff"))
                return fromMetadata;
            return known;
        }
        if (fromMetadata.Length != 0) return fromMetadata;
        var remoteExtension = NormalizeExtension(Path.GetExtension(Sanitize(metadata.Name)));
        return remoteExtension.Length != 0 ? remoteExtension : mime.Equals("text/plain", StringComparison.OrdinalIgnoreCase) ? ".txt" : "";
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return "";
        var value = extension.Trim().TrimStart('.');
        if (value.Length == 0 || value.EndsWith('.') || value.Length > 253 || value.Contains("..", StringComparison.Ordinal) ||
            value.Any(IsInvalid) || !HasValidSurrogates(value)) return "";
        return "." + value.ToLowerInvariant();
    }

    private static string Sanitize(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                result.Append(character).Append(value[++index]);
            }
            else result.Append(IsInvalid(character) || char.IsSurrogate(character) ? '_' : character);
        }
        return result.ToString().Trim().Trim('.').TrimEnd();
    }

    private static bool IsInvalid(char character) => char.IsControl(character) || InvalidCharacters.Contains(character);

    private static bool HasValidSurrogates(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index]) || index + 1 == value.Length || !char.IsLowSurrogate(value[++index])) return false;
        }
        return true;
    }

    private static bool IsReserved(string name)
    {
        var first = name.Split('.')[0].TrimEnd(' ');
        if (first.Equals("CON", StringComparison.OrdinalIgnoreCase) || first.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("AUX", StringComparison.OrdinalIgnoreCase) || first.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || first.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)) return true;
        return first.Length == 4 && (first.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || first.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            (first[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }

    private static string ProtectBase(string value)
    {
        var result = value.Trim(' ', '.');
        if (result.Length == 0) result = "Документ";
        return IsReserved(result) ? "_" + result : result;
    }

    private static string Compose(string stem, string extension, string suffix, int maxLength)
    {
        if (maxLength is < 1 or > MaximumFileNameLength || maxLength <= extension.Length + suffix.Length)
            throw new FileStorageException(FileStorageErrorCode.PathTooLong, "Шлях або розширення надто довгі. Оберіть коротшу директорію збереження.");
        var budget = maxLength - extension.Length - suffix.Length;
        var shortened = Truncate(stem, budget).TrimEnd(' ', '.');
        if (shortened.Length == 0) shortened = Truncate("Документ", budget);
        if (IsReserved(shortened)) shortened = "_" + Truncate(shortened, budget - 1);
        var result = shortened + suffix + extension;
        ValidateFileName(result);
        return result;
    }

    private static string Truncate(string value, int length)
    {
        if (value.Length <= length) return value;
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }
}
