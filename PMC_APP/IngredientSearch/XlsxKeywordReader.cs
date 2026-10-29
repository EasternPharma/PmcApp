using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace IngredientKeywordSearch;

/// <summary>
/// Reads the column named "keyword" from the first worksheet of an XLSX file.
/// Handles shared and inline strings, skips blanks, and ignores all other columns.
/// Uses only built-in .NET libraries; no Excel installation or NuGet package.
/// </summary>
public static class XlsxKeywordReader
{
    private static readonly XNamespace MainNs =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelationshipsNs =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationshipsNs =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    public static List<string> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using ZipArchive archive = ZipFile.OpenRead(path);

        List<string> sharedStrings = ReadSharedStrings(archive);
        string sheetPath = GetFirstSheetPath(archive);
        ZipArchiveEntry sheetEntry = archive.GetEntry(sheetPath)
            ?? throw new InvalidDataException($"Worksheet '{sheetPath}' was not found.");

        using Stream sheetStream = sheetEntry.Open();
        XDocument worksheet = XDocument.Load(sheetStream);
        var rows = worksheet.Descendants(MainNs + "sheetData").Elements(MainNs + "row");
        using IEnumerator<XElement> enumerator = rows.GetEnumerator();
        if (!enumerator.MoveNext())
            throw new InvalidDataException("The first worksheet is empty.");

        string? keywordColumn = null;
        foreach (XElement cell in enumerator.Current.Elements(MainNs + "c"))
        {
            if (string.Equals(ReadCell(cell, sharedStrings).Trim(), "keyword",
                    StringComparison.OrdinalIgnoreCase))
            {
                keywordColumn = GetColumn((string?)cell.Attribute("r"));
                break;
            }
        }

        if (string.IsNullOrEmpty(keywordColumn))
            throw new InvalidDataException("No 'keyword' header in the first worksheet.");

        var words = new List<string>();
        while (enumerator.MoveNext())
        {
            XElement? cell = enumerator.Current.Elements(MainNs + "c")
                .FirstOrDefault(c => string.Equals(GetColumn((string?)c.Attribute("r")),
                    keywordColumn, StringComparison.OrdinalIgnoreCase));
            if (cell is null)
                continue;

            string value = ReadCell(cell, sharedStrings).Trim();
            if (value.Length != 0)
                words.Add(value);
        }

        return words;
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        ZipArchiveEntry? entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
            return new List<string>();

        using Stream stream = entry.Open();
        XDocument document = XDocument.Load(stream);
        return document.Descendants(MainNs + "si")
            .Select(item => string.Concat(item.Descendants(MainNs + "t").Select(t => t.Value)))
            .ToList();
    }

    private static string ReadCell(XElement cell, IReadOnlyList<string> shared)
    {
        string? type = (string?)cell.Attribute("t");
        if (type == "inlineStr")
            return string.Concat(cell.Descendants(MainNs + "t").Select(t => t.Value));

        string? raw = (string?)cell.Element(MainNs + "v");
        if (string.IsNullOrEmpty(raw))
            return "";

        if (type != "s")
            return raw;

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            || index < 0 || index >= shared.Count)
            throw new InvalidDataException($"Invalid XLSX shared-string index '{raw}'.");

        return shared[index];
    }

    private static string GetColumn(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference))
            return "";

        int end = 0;
        while (end < cellReference.Length && char.IsLetter(cellReference[end]))
            end++;
        return cellReference[..end];
    }

    private static string GetFirstSheetPath(ZipArchive archive)
    {
        ZipArchiveEntry workbookEntry = archive.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException("Missing xl/workbook.xml.");
        ZipArchiveEntry relationsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels")
            ?? throw new InvalidDataException("Missing workbook relationships.");

        using Stream workbookStream = workbookEntry.Open();
        XDocument workbook = XDocument.Load(workbookStream);
        string? id = (string?)workbook.Descendants(MainNs + "sheet").FirstOrDefault()
            ?.Attribute(RelationshipsNs + "id");

        using Stream relationsStream = relationsEntry.Open();
        XDocument relations = XDocument.Load(relationsStream);
        string? target = (string?)relations.Descendants(PackageRelationshipsNs + "Relationship")
            .FirstOrDefault(r => (string?)r.Attribute("Id") == id)?.Attribute("Target");
        if (string.IsNullOrEmpty(target))
            throw new InvalidDataException("Could not resolve the first worksheet.");

        // Resolve a package-relative or package-absolute target to a ZIP entry path.
        return new Uri(new Uri("https://xlsx.invalid/xl/workbook.xml"), target)
            .AbsolutePath.TrimStart('/');
    }
}
