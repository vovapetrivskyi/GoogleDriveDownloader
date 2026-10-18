namespace GoogleDriveDownloader.Application.Input;

public sealed class DriveLinkParser
{
    public DriveLink Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DriveLinkException("Посилання порожнє.");

        var text = value.Trim();
        if (text.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character == '\\') ||
            !HasValidEscapes(text) || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            (uri.Host != "drive.google.com" && uri.Host != "docs.google.com"))
        {
            throw new DriveLinkException("Потрібне коректне HTTPS-посилання на drive.google.com або docs.google.com без облікових даних і нестандартного порту.");
        }

        // Uri нормалізує «..» у шляху. Розбираємо вихідний шлях, щоб не приймати іншу структуру URL.
        var authorityStart = text.IndexOf("://", StringComparison.Ordinal) + 3;
        var pathStart = text.IndexOfAny(['/', '?', '#'], authorityStart);
        if (text[authorityStart..(pathStart < 0 ? text.Length : pathStart)].Contains('@'))
            throw new DriveLinkException("Посилання не повинно містити облікові дані.");
        var pathEnd = pathStart < 0 ? -1 : text.IndexOfAny(['?', '#'], pathStart);
        var path = pathStart < 0 || text[pathStart] != '/' ? "" : text[pathStart..(pathEnd < 0 ? text.Length : pathEnd)];
        if (path.EndsWith('/')) path = path[..^1];
        var parts = path.Split('/');
        string? pathId = null;
        var queryLink = uri.Host == "drive.google.com" && path is "/open" or "/uc";
        if (!queryLink)
        {
            var kindAllowed = parts.Length >= 4 && (uri.Host == "drive.google.com"
                ? parts[1] == "file"
                : parts[1] is "document" or "spreadsheets" or "presentation");
            var actionAllowed = parts.Length == 4 || (parts.Length == 5 && parts[4] is "view" or "edit" or "preview" or "copy");
            if (!kindAllowed || !actionAllowed || parts[0] != "" || parts[2] != "d")
                throw new DriveLinkException("Непідтримувана структура посилання Google Drive. Використайте посилання на окремий файл або документ.");
            pathId = Decode(parts[3]);
        }

        string? queryId = null;
        string? resourceKey = null;
        var hasId = false;
        var hasKey = false;
        foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = parameter.Split('=', 2);
            var name = Decode(pair[0]);
            var parameterValue = pair.Length == 2 ? Decode(pair[1]) : "";
            if (name.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                if (hasId) throw new DriveLinkException("Посилання містить декілька параметрів File ID.");
                hasId = true;
                queryId = parameterValue;
            }
            if (name.Equals("resourcekey", StringComparison.OrdinalIgnoreCase))
            {
                if (hasKey) throw new DriveLinkException("Посилання містить декілька параметрів resourcekey.");
                hasKey = true;
                resourceKey = parameterValue;
            }
        }

        var fileId = pathId ?? queryId;
        if (!IsFileId(fileId) || (hasId && !IsFileId(queryId)))
            throw new DriveLinkException("File ID відсутній або містить недопустимі символи.");
        if (pathId is not null && hasId && pathId != queryId)
            throw new DriveLinkException("File ID у шляху й параметрах посилання відрізняються.");
        if (hasKey && (string.IsNullOrEmpty(resourceKey) || resourceKey.Any(character =>
                character is < '!' or > '~' or '/' or ',' or '\\')))
            throw new DriveLinkException("Параметр resourcekey порожній або містить недопустимі символи.");

        return new DriveLink(fileId!, resourceKey);
    }

    private static bool IsFileId(string? value) => !string.IsNullOrEmpty(value) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static bool HasValidEscapes(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            if (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2])) return false;
            index += 2;
        }
        return true;
    }
}
