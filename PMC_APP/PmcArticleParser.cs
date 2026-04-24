using PMC_APP.DTOs;
using System.Text;
using System.Xml;
namespace PMC_APP;
public class PmcArticleParser : IDisposable
{
    private bool disposedValue;
    private static readonly object _consoleLock = new object();

    public int PmcId { get; set; }
    private ArticleDTO article { get; set; }
    public PmcArticleParser()
    {
        article = new ArticleDTO();
    }

    private static void Log(string message)
    {
        lock (_consoleLock)
        {
            Console.WriteLine(message);
        }
    }
    private string? GetSingleValue(XmlDocument xmlDoc, string xpath)
    {
        if (xmlDoc == null)
            throw new ArgumentNullException(nameof(xmlDoc));

        var node = xmlDoc.SelectSingleNode(xpath);
        return node?.InnerText ?? null;
    }
    private string? GetJournal(XmlDocument xmlDoc)
    {
        if (xmlDoc == null)
            throw new ArgumentNullException(nameof(xmlDoc));

        // 1. Primary: journal-title
        var node = xmlDoc.SelectSingleNode("//journal-title-group/journal-title");

        // 2. Fallback chain: journal-id types by priority
        if (node == null)
        {
            node =
                xmlDoc.SelectSingleNode("//journal-id[@journal-id-type='nlm-ta']") ??
                xmlDoc.SelectSingleNode("//journal-id[@journal-id-type='iso-abbrev']") ??
                xmlDoc.SelectSingleNode("//journal-id[@journal-id-type='publisher-id']");
        }

        return node?.InnerText ?? null;
    }

    private List<string>? GetAuthors(XmlDocument xml_doc)
    {
        try
        {
            var author_nodes = xml_doc.SelectNodes("//contrib-group/contrib[@contrib-type='author']");
            if (author_nodes != null && author_nodes.Count > 0)
            {
                List<string> Authors = new List<string>();

                foreach (XmlNode author in author_nodes)
                {
                    string surname = author.SelectSingleNode("name/surname")?.InnerText.Trim() ?? string.Empty;
                    string givenNames = author.SelectSingleNode("name/given-names")?.InnerText.Trim() ?? string.Empty;
                    string author_name = $"{surname} {givenNames}".Trim();
                    Authors.Add(author_name);
                }
                return Authors;
            }
            else
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            Log($"[PMC{this.PmcId}] Error extracting authors: {ex.Message}");
            return null;
        }

    }
    private DateTime? GetPublishDate(XmlDocument xml_doc)
    {
        List<DateTime> possibleDates = new List<DateTime>();
        // 1. Try pub-date: epub → collection
        foreach (string pubType in new[] { "epub", "collection" })
        {
            int day = 0;
            int month = 0;
            int year = 0;
            string inner_text = "";
            try
            {
                var pubNode = xml_doc.SelectSingleNode($"//pub-date[@pub-type='{pubType}']");
                if (pubNode != null)
                {
                    inner_text = pubNode.InnerText.Trim();
                    day = int.TryParse(pubNode.SelectSingleNode("day")?.InnerText.Trim(), out int d) ? d : 1;
                    month = int.TryParse(pubNode.SelectSingleNode("month")?.InnerText.Trim(), out int m) ? m : 1;
                    year = int.TryParse(pubNode.SelectSingleNode("year")?.InnerText.Trim(), out int y) ? y : 1;
                    if (year == 1)
                        continue;
                    try
                    {
                        possibleDates.Add(new DateTime(year, month, day));
                    }
                    catch
                    {
                        if (month > 0 && month < 13 && day >= 31)
                            possibleDates.Add(new DateTime(year, month, day - 1));
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[PMC{this.PmcId}] Error parsing pub-date ({pubType}): day={day} month={month} year={year} | {inner_text}");
            }
        }

        // 2. If still empty, try history: accepted → rev-recd → received
        foreach (string dateType in new[] { "accepted", "rev-recd", "received" })
        {
            int day = 0;
            int month = 0;
            int year = 0;
            string inner_text = "";
            try
            {
                var histNode = xml_doc.SelectSingleNode($"//history/date[@date-type='{dateType}']");

                if (histNode != null)
                {
                    inner_text = histNode.InnerText.Trim();
                    day = int.TryParse(histNode.SelectSingleNode("day")?.InnerText.Trim(), out int d) ? d : 1;
                    month = int.TryParse(histNode.SelectSingleNode("month")?.InnerText.Trim(), out int m) ? m : 1;
                    year = int.TryParse(histNode.SelectSingleNode("year")?.InnerText.Trim(), out int y) ? y : 1;
                    if (year == 1)
                        continue;
                    try
                    {
                        possibleDates.Add(new DateTime(year, month, day));
                    }
                    catch
                    {
                        if (month > 0 && month < 13 && day >= 31)
                            possibleDates.Add(new DateTime(year, month, day - 1));
                    }

                }
            }
            catch (Exception ex)
            {
                Log($"[PMC{this.PmcId}] Error parsing history date ({dateType}): day={day} month={month} year={year} | {inner_text}");
            }

        }
        if (possibleDates.Count > 0)
            return possibleDates.Min();
        return null;
    }
    private string? GetAbstract(XmlDocument xml_doc)
    {
        try
        {
            string? abstractText = null;
            var abstractNode = xml_doc.SelectSingleNode("//abstract");
            if (abstractNode != null)
            {
                var pNodes = abstractNode.SelectNodes("p");
                var sectionNodes = abstractNode.SelectNodes("sec");

                if (pNodes.Count == 0 && sectionNodes.Count == 0)
                {
                    // Just plain text inside <abstract>
                    abstractText = abstractNode.InnerText.Trim();
                }
                else if (pNodes.Count > 0)
                {
                    // One or multiple <p> nodes
                    var parts = new List<string>();
                    foreach (XmlNode p in pNodes)
                        parts.Add(p.InnerText.Trim());

                    abstractText = string.Join("\n", parts);
                }
                else if (sectionNodes.Count > 0)
                {
                    // Structured sections with headers
                    var parts = new List<string>();
                    foreach (XmlNode sec in sectionNodes)
                    {
                        string header = sec.SelectSingleNode("title")?.InnerText.Trim() ?? string.Empty;
                        string body = sec.SelectSingleNode("p")?.InnerText.Trim() ?? string.Empty;

                        if (!string.IsNullOrEmpty(header))
                            parts.Add(header);
                        if (!string.IsNullOrEmpty(body))
                            parts.Add(body);
                    }

                    abstractText = string.Join("\n", parts);
                }

                abstractText = abstractText.Trim();
            }
            return abstractText;
        }
        catch (Exception ex)
        {
            Log($"[PMC{this.PmcId}] Error extracting abstract: {ex.Message}");
            return null;
        }

    }
    private List<string>? GetKeywords(XmlDocument xml_doc)
    {
        var kwdNodes = xml_doc.SelectNodes("//kwd-group/kwd");
        if (kwdNodes != null && kwdNodes.Count > 0)
        {
            var keywords = new List<string>();
            foreach (XmlNode kwd in kwdNodes)
            {
                string keyword = kwd.InnerText.Trim();
                if (!string.IsNullOrEmpty(keyword))
                    keywords.Add(keyword);
            }
            return keywords;
        }
        return null;
    }

    private Dictionary<string, string> GetSections_depth(XmlNodeList nodes, int depth = 0)
    {
        Dictionary<string, string> sections = new Dictionary<string, string>();
        StringBuilder header = new StringBuilder();
        StringBuilder paragraph = new StringBuilder();
        short sectionCount = 1;

        void Flush()
        {
            if (paragraph.Length == 0) return;
            if (header.Length == 0) header.Append("section_" + sectionCount++);
            string key = header.ToString().Trim();
            if (sections.ContainsKey(key)) key = $"{key}_{sectionCount++}";
            string value = System.Text.RegularExpressions.Regex.Replace(paragraph.ToString().Trim(), @"\.([A-Z])", ". $1");
            sections[key] = value;
            header.Clear();
            paragraph.Clear();
        }

        foreach (XmlNode node in nodes)
        {
            if (node.Name.ToLower() == "title" || node.Name.ToLower() == "b" || node.Name.ToLower().StartsWith("h") || node.Name.ToLower() == "strong" || node.Name.ToLower() == "bold")
            {
                if (paragraph.Length > 0)
                    Flush();
                header.AppendLine(node.InnerText.Trim());
            }
            else if (!node.Name.ToLower().StartsWith("sec"))
            {
                paragraph.AppendLine(node.InnerText.Trim());
            }
            else if (node.Name == "sec" && depth < 1)
            {
                Flush();
                var subSections = GetSections_depth(node.ChildNodes, 1);
                if (subSections != null)
                {
                    foreach (var kvp in subSections)
                        sections[kvp.Key] = kvp.Value;
                }
            }
            else if (node.Name == "sec" && depth >= 1)
            {
                Flush();
                var sub_nodes = node.ChildNodes;
                for (var pp = 0; pp < sub_nodes.Count; pp++)
                {
                    var sub_node = sub_nodes[pp];
                    if (sub_node != null && sub_node.InnerText != null)
                        paragraph.AppendLine(sub_node.InnerText.Trim());
                }
                Flush();
            }
        }

        Flush();
        return sections;
    }

    private Dictionary<string, string>? GetSections(XmlDocument xml_doc)
    {
        var bodyNode = xml_doc.SelectSingleNode("//body");
        if (bodyNode != null)
        {
            Dictionary<string, string> sections = new Dictionary<string, string>();
            XmlNodeList child_nodes = bodyNode.ChildNodes;


            // Case 1: Plain text or single <p>
            if (child_nodes.Count == 0)
            {
                sections["full_text"] = bodyNode.InnerText.Trim();
            }
            var sectionsFromDepth = GetSections_depth(child_nodes);
            if (sectionsFromDepth != null)
            {
                foreach (var kvp in sectionsFromDepth)
                {
                    sections[kvp.Key] = kvp.Value;
                }
            }
            return sections;
        }
        return null;
    }
    private Dictionary<string, string>? GetTables(XmlDocument xml_doc)
    {
        // helper
        string Normalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            return System.Text.RegularExpressions.Regex
                .Replace(input, @"\s+", " ")
                .Trim();
        }
        var tableNodes = xml_doc.SelectNodes("//floats-group//table-wrap");

        if (tableNodes != null && tableNodes.Count > 0)
        {
            Dictionary<string, string> tables = new Dictionary<string, string>();
            for (int t = 0; t < tableNodes.Count; t++)
            {
                XmlNode tableWrap = tableNodes[t];

                // Table name
                string table_name = tableWrap.SelectSingleNode("label")?.InnerText.Trim() ?? "";
                string table_caption = tableWrap.SelectSingleNode("caption/p")?.InnerText.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(table_name))
                    table_name = !string.IsNullOrWhiteSpace(table_caption)
                        ? table_caption
                        : $"table_{t + 1}";

                // Columns
                var thNodes = tableWrap.SelectNodes("table/thead/tr/th");
                var columns = new List<string>();

                foreach (XmlNode th in thNodes)
                {
                    string col = th.InnerText.Trim();

                    if (string.IsNullOrWhiteSpace(col))
                        col = $"Column_{columns.Count + 1}";

                    columns.Add(Normalize(col));
                }

                // Rows
                var trNodes = tableWrap.SelectNodes("table/tbody/tr");
                var rows = new List<Dictionary<string, string>>();

                foreach (XmlNode tr in trNodes)
                {
                    var tdNodes = tr.SelectNodes("td");
                    var row = new Dictionary<string, string>();

                    for (int i = 0; i < tdNodes.Count; i++)
                    {
                        string value = Normalize(tdNodes[i].InnerText);
                        string colName = i < columns.Count ? columns[i] : $"Extra_{i}";

                        row[colName] = value;
                    }

                    if (row.Count > 0)
                        rows.Add(row);
                }

                // Object to serialize
                var tableObj = new
                {
                    table_name = table_name,
                    caption = Normalize(table_caption),
                    columns = columns,
                    row_count = rows.Count,
                    rows = rows
                };

                string json = System.Text.Json.JsonSerializer.Serialize(
                    tableObj,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = false
                    });

                string key = table_name;
                if (tables.ContainsKey(key))
                    key = $"{table_name}_{t + 1}";
                if (tables.ContainsKey(key))
                    key = $"{table_name}_{t}_{tables.Count}";

                tables[key] = json;
            }
            return tables;
        }
        return null;
    }
    private string? GetPossiable(XmlDocument xml_doc, string[] possibleXpaths)
    {
        foreach (string xpath in possibleXpaths)
        {
            var doiNode = xml_doc.SelectSingleNode(xpath);
            if (doiNode != null)
                return doiNode.InnerText.Trim();
        }
        return null;
    }
    private int? GetPubmedId(XmlDocument xml_doc, string[] possibleXpaths)
    {
        int? pmid = null;
        string? pmidText = GetPossiable(xml_doc, possibleXpaths);
        if (pmidText != null && int.TryParse(pmidText, out int parsedPmid))
            pmid = parsedPmid;
        return pmid;
    }

    private void ExtractData(XmlDocument xml_doc)
    {
        this.article.PmcId = this.PmcId;
        this.article.Title = GetSingleValue(xml_doc, "//article-title");
        this.article.Journal = GetJournal(xml_doc);
        this.article.Category = GetSingleValue(xml_doc, "//article-categories/subj-group[@subj-group-type='heading']/subject");
        this.article.Volume = GetSingleValue(xml_doc, "//volume");
        this.article.Issue = GetSingleValue(xml_doc, "//issue");
        this.article.Doi = GetPossiable(xml_doc, new[] { "//pub-id[@pub-id-type='doi']", "//article-id[@pub-id-type='doi']" });
        this.article.PmId = GetPubmedId(xml_doc, new[] { "//pub-id[@pub-id-type='pmid']", "//article-id[@pub-id-type='pmid']" });
        this.article.Publisher = GetSingleValue(xml_doc, "//publisher-name");
        this.article.ISSN = GetSingleValue(xml_doc, "//issn");
        this.article.FPage = GetSingleValue(xml_doc, "//fpage");
        this.article.LPage = GetSingleValue(xml_doc, "//lpage");
        this.article.Authors = GetAuthors(xml_doc);
        this.article.PublishDate = GetPublishDate(xml_doc);
        this.article.AbstractText = GetAbstract(xml_doc);
        this.article.Keywords = GetKeywords(xml_doc);
        this.article.Sections = GetSections(xml_doc);
        Dictionary<string, string>? tables = GetTables(xml_doc);
        if (tables != null)
        {
            this.article.Sections ??= new Dictionary<string, string>();
            foreach (var kvp in tables)
            {
                this.article.Sections[kvp.Key] = kvp.Value;
            }
        }
    }
    public ArticleDTO? GetArticle(string pmcId, string source)
    {
        int? pmcid = int.TryParse(pmcId.ToLower().Replace("pmc", "").Replace(".xml", ""), out int result) ? result : (int?)null;
        if (pmcid == null)
        {
            throw new ArgumentException("Invalid PMC ID format.", nameof(pmcId));
        }
        return GetArticle(pmcid.Value, source);
    }
    public ArticleDTO? GetArticle(int pmcId, string source)
    {
        this.PmcId = pmcId;
        article.PmcId = pmcId;
        var xml_doc = new XmlDocument();
        xml_doc.LoadXml(source);
        ExtractData(xml_doc);
        return article;
    }

    public ArticleDTO? GetArticle(string pmcId, byte[] bytes)
    {
        try
        {
            int? pmcid = int.TryParse(pmcId.ToLower().Replace("pmc", "").Replace(".xml", ""), out int result) ? result : (int?)null;
            if (pmcid == null)
                throw new ArgumentException("Invalid PMC ID format.", nameof(pmcId));

            this.PmcId = pmcid.Value;
            article.PmcId = pmcid.Value;
            var xml_doc = new XmlDocument();
            using var ms = new System.IO.MemoryStream(bytes, writable: false);
            xml_doc.Load(ms);
            ExtractData(xml_doc);
            return article;
        }
        catch (Exception)
        {
            Console.WriteLine($"\n__________________________\n[PMC{pmcId}] Error parsing XML content.\n");
            return null;
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            disposedValue = true;
        }
    }
    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}