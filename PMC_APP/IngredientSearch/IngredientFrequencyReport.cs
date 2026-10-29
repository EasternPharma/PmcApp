using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using MongoDB.Bson;

namespace PMC_APP.IngredientSearch;

public sealed class KeywordFrequencyRow
{
    public string Keyword { get; init; } = string.Empty;
    public long[] Occurrences { get; } = new long[3];
    public long[] ArticleCounts { get; } = new long[3];
    public long AnyFieldArticleCount { get; set; }
}

/// <summary>Aggregates stored counts, so a resumed job produces a complete, nonduplicated report.</summary>
public sealed class IngredientFrequencyReport
{
    private readonly Dictionary<string, KeywordFrequencyRow> _rows;
    public IReadOnlyCollection<KeywordFrequencyRow> Rows => _rows.Values;
    public long Articles { get; private set; }
    public long[] ArticlesWithMatches { get; } = new long[3];
    public long ArticlesWithAnyMatch { get; private set; }
    private static readonly string[] CountFields =
        { "IngredientTitleCounts", "IngredientAbstractCounts", "IngredientSectionCounts" };

    public IngredientFrequencyReport(IEnumerable<string> keywords) =>
        _rows = keywords.ToDictionary(k => k, k => new KeywordFrequencyRow { Keyword = k }, StringComparer.Ordinal);

    public void AddArticle(BsonDocument article)
    {
        var any = new HashSet<string>(StringComparer.Ordinal);
        for (int field = 0; field < 3; field++)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in article[CountFields[field]].AsBsonArray)
            {
                string keyword = value["Keyword"].AsString;
                long count = value["Count"].ToInt64();
                if (count <= 0 || !seen.Add(keyword))
                    throw new InvalidDataException($"Invalid or repeated stored count for '{keyword}'.");
                if (!_rows.TryGetValue(keyword, out var row))
                    throw new InvalidDataException($"Unknown stored keyword '{keyword}' for this keyword version.");
                row.Occurrences[field] += count;
                row.ArticleCounts[field]++;
                any.Add(keyword);
            }
            if (seen.Count > 0) ArticlesWithMatches[field]++;
        }
        foreach (string keyword in any) _rows[keyword].AnyFieldArticleCount++;
        if (any.Count > 0) ArticlesWithAnyMatch++;
        Articles++;
    }

    /// <summary>Combines aggregates from disjoint sets of articles.</summary>
    public void Merge(IngredientFrequencyReport other)
    {
        if (_rows.Count != other._rows.Count || other._rows.Keys.Any(k => !_rows.ContainsKey(k)))
            throw new ArgumentException("Cannot merge reports for different keyword sets.", nameof(other));
        foreach (var (keyword, source) in other._rows)
        {
            var target = _rows[keyword];
            for (int i = 0; i < 3; i++)
            {
                target.Occurrences[i] += source.Occurrences[i];
                target.ArticleCounts[i] += source.ArticleCounts[i];
            }
            target.AnyFieldArticleCount += source.AnyFieldArticleCount;
        }
        for (int i = 0; i < 3; i++) ArticlesWithMatches[i] += other.ArticlesWithMatches[i];
        ArticlesWithAnyMatch += other.ArticlesWithAnyMatch;
        Articles += other.Articles;
    }

    public void Save(string path, IngredientFrequencyOptions options, string version, TimeSpan elapsed)
    {
        string created = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney")).ToString("yyyy-MM-dd HH:mm:ss zzz");
        var sorted = Rows.OrderByDescending(r => r.Occurrences.Sum()).ThenBy(r => r.Keyword, StringComparer.Ordinal).ToArray();
        var summary = new List<object?[]>
        {
            new object?[] { "Metric", "Value" },
            new object?[] { "Created (Australia/Sydney)", created },
            new object?[] { "Database / collection", options.Database + "." + options.Collection },
            new object?[] { "Keyword file", Path.GetFullPath(options.KeywordsPath) },
            new object?[] { "Keyword / matcher version", version },
            new object?[] { "Unique keywords (including zero matches)", Rows.Count },
            new object?[] { "Eligible articles labeled", Articles },
            new object?[] { "Articles with any ingredient match", ArticlesWithAnyMatch },
            new object?[] { "Articles with title matches", ArticlesWithMatches[0] },
            new object?[] { "Articles with abstract matches", ArticlesWithMatches[1] },
            new object?[] { "Articles with section matches", ArticlesWithMatches[2] },
            new object?[] { "Title occurrences", Rows.Sum(r => r.Occurrences[0]) },
            new object?[] { "Abstract occurrences", Rows.Sum(r => r.Occurrences[1]) },
            new object?[] { "Section occurrences", Rows.Sum(r => r.Occurrences[2]) },
            new object?[] { "Job elapsed seconds", Math.Round(elapsed.TotalSeconds, 2) },
            new object?[] { "Eligibility", "Nonblank AbstractText and at least one nonblank string in Sections; no human-study/source restriction." },
            new object?[] { "Sections scope", "Only section values (including stored tables); section keys/headings are not counted separately. Each value is scanned independently." },
            new object?[] { "Matching", "Original Aho-Corasick; invariant case-insensitive, Unicode whole word/phrase, literal internal whitespace/punctuation; overlapping keywords count independently." },
            new object?[] { "Occurrence count", "Every matched occurrence; section counts are summed across stored section values. Stored duplicate text is counted as stored." },
            new object?[] { "Article count", "Each keyword counts at most once per article per field. Any-field article counts are a union across the three fields." },
            new object?[] { "Article percentage denominator", "All eligible labeled articles, including articles with no matches." },
            new object?[] { "Input frequency column", "Ignored. All frequencies are recalculated from MongoDB article text." }
        };

        IEnumerable<object?[]> AllRows()
        {
            yield return new object?[] { "Keyword", "Title occurrences", "Title articles", "Title article %",
                "Abstract occurrences", "Abstract articles", "Abstract article %", "Sections occurrences",
                "Sections articles", "Sections article %", "Total occurrences", "Any-field articles", "Any-field article %" };
            foreach (var r in sorted)
                yield return new object?[] { r.Keyword, r.Occurrences[0], r.ArticleCounts[0], Percent(r.ArticleCounts[0]),
                    r.Occurrences[1], r.ArticleCounts[1], Percent(r.ArticleCounts[1]),
                    r.Occurrences[2], r.ArticleCounts[2], Percent(r.ArticleCounts[2]),
                    r.Occurrences.Sum(), r.AnyFieldArticleCount, Percent(r.AnyFieldArticleCount) };
        }
        IEnumerable<object?[]> FieldRows(int field)
        {
            yield return new object?[] { "Keyword", "Occurrences", "Articles", "Article %" };
            foreach (var r in Rows.OrderByDescending(r => r.Occurrences[field]).ThenBy(r => r.Keyword, StringComparer.Ordinal))
                yield return new object?[] { r.Keyword, r.Occurrences[field], r.ArticleCounts[field], Percent(r.ArticleCounts[field]) };
        }
        XlsxReportWriter.Write(path, new[]
        {
            new ReportSheet("Summary", summary, summary.Count, 2),
            new ReportSheet("Keyword frequencies", AllRows(), sorted.Length + 1, 13),
            new ReportSheet("Title", FieldRows(0), sorted.Length + 1, 4),
            new ReportSheet("Abstract", FieldRows(1), sorted.Length + 1, 4),
            new ReportSheet("Full text sections", FieldRows(2), sorted.Length + 1, 4)
        });
        File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new
        {
            createdAustraliaSydney = created, database = options.Database, collection = options.Collection,
            keywordVersion = version, keywordCount = Rows.Count, articles = Articles,
            articlesWithAnyMatch = ArticlesWithAnyMatch, articlesWithMatches = ArticlesWithMatches,
            occurrences = Enumerable.Range(0, 3).Select(i => Rows.Sum(r => r.Occurrences[i])).ToArray(),
            fields = new[] { "Title", "AbstractText", "Sections" },
            keywords = sorted.Select(r => new { r.Keyword, r.Occurrences, r.ArticleCounts, r.AnyFieldArticleCount })
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private double Percent(long value) => Articles == 0 ? 0 : Math.Round(value * 100d / Articles, 6);
}

internal sealed record ReportSheet(string Name, IEnumerable<object?[]> Rows, int RowCount, int ColumnCount);

/// <summary>A small standards-based XLSX writer; all text is stored as inline strings (never formulas).</summary>
internal static class XlsxReportWriter
{
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static void Write(string path, IReadOnlyList<ReportSheet> sheets)
    {
        string temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            Part(zip, "[Content_Types].xml", w =>
            {
                w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                Empty(w, "Default", ("Extension", "rels"), ("ContentType", "application/vnd.openxmlformats-package.relationships+xml"));
                Empty(w, "Default", ("Extension", "xml"), ("ContentType", "application/xml"));
                Empty(w, "Override", ("PartName", "/xl/workbook.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"));
                Empty(w, "Override", ("PartName", "/xl/styles.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"));
                for (int i = 1; i <= sheets.Count; i++)
                    Empty(w, "Override", ("PartName", $"/xl/worksheets/sheet{i}.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
                w.WriteEndElement();
            });
            Relationships(zip, "_rels/.rels", new[] { ("officeDocument", "xl/workbook.xml") });
            Relationships(zip, "xl/_rels/workbook.xml.rels", Enumerable.Range(1, sheets.Count)
                .Select(i => ("worksheet", $"worksheets/sheet{i}.xml")).Append(("styles", "styles.xml")).ToArray());
            Part(zip, "xl/workbook.xml", w =>
            {
                w.WriteStartElement("workbook", Ns);
                w.WriteAttributeString("xmlns", "r", null, RelNs);
                w.WriteStartElement("sheets");
                for (int i = 0; i < sheets.Count; i++)
                {
                    w.WriteStartElement("sheet");
                    w.WriteAttributeString("name", sheets[i].Name);
                    w.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("r", "id", RelNs, $"rId{i + 1}");
                    w.WriteEndElement();
                }
                w.WriteEndElement(); w.WriteEndElement();
            });
            Part(zip, "xl/styles.xml", w => XElement.Parse("<styleSheet xmlns=\"" + Ns + "\">"
                + "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><color rgb=\"FFFFFFFF\"/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>"
                + "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF17365D\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>"
                + "<borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
                + "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment wrapText=\"1\"/></xf></cellXfs>"
                + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>").WriteTo(w));
            for (int i = 0; i < sheets.Count; i++)
            {
                var sheet = sheets[i];
                Part(zip, $"xl/worksheets/sheet{i + 1}.xml", w => WriteSheet(w, sheet));
            }
        }
        File.Move(temporary, path, true);
    }

    private static void WriteSheet(XmlWriter w, ReportSheet sheet)
    {
        w.WriteStartElement("worksheet", Ns);
        string end = Column(sheet.ColumnCount) + sheet.RowCount;
        Empty(w, "dimension", ("ref", "A1:" + end));
        w.WriteStartElement("sheetViews"); w.WriteStartElement("sheetView"); w.WriteAttributeString("workbookViewId", "0");
        Empty(w, "pane", ("ySplit", "1"), ("topLeftCell", "A2"), ("activePane", "bottomLeft"), ("state", "frozen"));
        w.WriteEndElement(); w.WriteEndElement();
        w.WriteStartElement("cols");
        Empty(w, "col", ("min", "1"), ("max", "1"), ("width", "48"), ("customWidth", "1"));
        Empty(w, "col", ("min", "2"), ("max", sheet.ColumnCount.ToString(CultureInfo.InvariantCulture)),
            ("width", sheet.Name == "Summary" ? "110" : "23"), ("customWidth", "1"));
        w.WriteEndElement();
        w.WriteStartElement("sheetData");
        int row = 0;
        foreach (var values in sheet.Rows)
        {
            row++;
            w.WriteStartElement("row"); w.WriteAttributeString("r", row.ToString(CultureInfo.InvariantCulture));
            if (row == 1) { w.WriteAttributeString("ht", "32"); w.WriteAttributeString("customHeight", "1"); }
            for (int column = 0; column < values.Length; column++)
            {
                object? value = values[column];
                w.WriteStartElement("c"); w.WriteAttributeString("r", Column(column + 1) + row);
                if (row == 1) w.WriteAttributeString("s", "1");
                if (value is byte or int or long or double or decimal)
                    w.WriteElementString("v", Convert.ToString(value, CultureInfo.InvariantCulture));
                else
                {
                    w.WriteAttributeString("t", "inlineStr"); w.WriteStartElement("is");
                    w.WriteStartElement("t"); w.WriteAttributeString("xml", "space", null, "preserve");
                    w.WriteString(value?.ToString() ?? ""); w.WriteEndElement(); w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
        Empty(w, "autoFilter", ("ref", "A1:" + end));
        w.WriteEndElement();
    }

    private static void Relationships(ZipArchive zip, string path, IReadOnlyList<(string Type, string Target)> rels) =>
        Part(zip, path, w =>
        {
            w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            for (int i = 0; i < rels.Count; i++)
                Empty(w, "Relationship", ("Id", $"rId{i + 1}"), ("Type", RelNs + "/" + rels[i].Type), ("Target", rels[i].Target));
            w.WriteEndElement();
        });

    private static void Part(ZipArchive zip, string path, Action<XmlWriter> write)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Fastest).Open();
        using var w = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false) });
        w.WriteStartDocument(); write(w); w.WriteEndDocument();
    }

    private static void Empty(XmlWriter w, string name, params (string Name, string Value)[] attributes)
    {
        w.WriteStartElement(name);
        foreach (var a in attributes) w.WriteAttributeString(a.Name, a.Value);
        w.WriteEndElement();
    }

    private static string Column(int number)
    {
        string value = "";
        while (number > 0) { number--; value = (char)('A' + number % 26) + value; number /= 26; }
        return value;
    }
}
