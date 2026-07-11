using StructuredRAG.Core.Models.Catalog;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StructuredRAG.Fhnw;

/// <summary>
/// Maps bariapi module details to <see cref="SourceModule"/>. Strips HTML, summarizes
/// assessment records, and deliberately drops personal contact data (emails, teacher
/// lists) — compiled artifacts flow through third-party AI clients.
/// </summary>
public static class SourceModuleMapper
{
    public static SourceModule Map(ModuleDetailDto d)
    {
        var semesterId = d.SemesterId ?? SemesterIdFromPlanId(d.PlanSemesterModulId);

        // English-taught modules often have content only in the *EN fields — fall back
        // so Description is never empty when the catalog has any content at all.
        var descriptionDe = JoinSections(StripHtml(d.KeyIdea), StripHtml(d.CourseContent));
        var descriptionEn = JoinSections(StripHtml(d.KeyIdeaEN), StripHtml(d.CourseContentEN));
        var requirementsDe = StripHtml(d.Requirements);
        var requirementsEn = StripHtml(d.RequirementsEN);

        return new SourceModule
        {
            Code = d.ModuleId > 0 ? d.ModuleId.ToString() : d.PlanSemesterModulId,
            ModuleId = d.ModuleId > 0 ? d.ModuleId.ToString() : null,
            Title = d.Title?.Trim() ?? string.Empty,
            TitleEn = NullIfSameOrEmpty(d.TitleEN?.Trim(), d.Title?.Trim()),
            Description = string.IsNullOrWhiteSpace(descriptionDe) ? descriptionEn : descriptionDe,
            DescriptionEn = string.IsNullOrWhiteSpace(descriptionDe) ? null : NullIfEmpty(descriptionEn),
            RequirementsText = string.IsNullOrWhiteSpace(requirementsDe) ? NullIfEmpty(requirementsEn) : requirementsDe,
            RequirementsTextEn = string.IsNullOrWhiteSpace(requirementsDe) ? null : NullIfEmpty(requirementsEn),
            Ects = d.Ects,
            Level = d.StudyLevel ?? string.Empty,
            OfferedIn = SemesterTypeOf(semesterId) is { } type ? new List<string> { type } : new(),
            Offerings = new List<ModuleOffering>
            {
                new() { SemesterId = semesterId, PlanSemesterModulId = d.PlanSemesterModulId }
            },
            Languages = ExtractLanguages(d),
            Weekdays = ExtractWeekdays(d),
            StudyPrograms = d.StudyPrograms ?? new List<string>(),
            ModuleType = d.ModuleTypes?.FirstOrDefault(),
            Locations = d.Locations ?? new List<string>(),
            ResponsibleName = d.ModuleResponsibles?.FirstOrDefault()?.Name,
            Assessment = SummarizeAssessment(d.PerformanceRecords),
            Url = BariApiClient.GetModuleDetailPageUrl(d.PlanSemesterModulId)
        };
    }

    /// <summary>
    /// Merges another offering of the same ModuleId into an existing source module.
    /// Content fields are taken from the newer offering; offerings are accumulated.
    /// </summary>
    public static SourceModule Merge(SourceModule a, SourceModule b)
    {
        var (older, newer) = CompareSemesters(NewestSemester(a), NewestSemester(b)) >= 0 ? (b, a) : (a, b);

        newer.Offerings = newer.Offerings
            .Concat(older.Offerings)
            .GroupBy(o => o.PlanSemesterModulId)
            .Select(g => g.First())
            .OrderByDescending(o => o.SemesterId, SemesterComparer)
            .ToList();
        newer.OfferedIn = newer.Offerings
            .Select(o => SemesterTypeOf(o.SemesterId))
            .Where(t => t != null)
            .Select(t => t!)
            .Distinct()
            .ToList();
        newer.Languages = newer.Languages.Union(older.Languages).ToList();
        newer.Weekdays = newer.Weekdays.Union(older.Weekdays, StringComparer.OrdinalIgnoreCase).ToList();
        return newer;
    }

    // --- helpers ---

    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex ListItemRegex = new(@"<li[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BlockBreakRegex = new(@"</(p|div|ul|ol|h\d)>|<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MultiSpaceRegex = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex MultiNewlineRegex = new(@"\n{3,}", RegexOptions.Compiled);

    /// <summary>Converts an HTML fragment to readable plain text (list items become "- " lines).</summary>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var text = ListItemRegex.Replace(html, "\n- ");
        text = BlockBreakRegex.Replace(text, "\n");
        text = TagRegex.Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = MultiSpaceRegex.Replace(text, " ");
        text = string.Join('\n', text.Split('\n').Select(l => l.Trim()));
        text = MultiNewlineRegex.Replace(text, "\n\n");
        return text.Trim();
    }

    private static string JoinSections(params string[] sections) =>
        string.Join("\n\n", sections.Where(s => !string.IsNullOrWhiteSpace(s)));

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? NullIfSameOrEmpty(string? s, string? other) =>
        string.IsNullOrWhiteSpace(s) || s == other ? null : s;

    private static string SemesterIdFromPlanId(string planSemesterModulId)
    {
        var idx = planSemesterModulId.IndexOf('_');
        return idx > 0 ? planSemesterModulId[..idx] : string.Empty;
    }

    /// <summary>"26HS" → "HS"; unknown formats → null.</summary>
    public static string? SemesterTypeOf(string semesterId) =>
        semesterId.Length == 4 && char.IsDigit(semesterId[0]) && char.IsDigit(semesterId[1])
            ? semesterId[2..].ToUpperInvariant()
            : null;

    private static string NewestSemester(SourceModule m) =>
        m.Offerings.Select(o => o.SemesterId).OrderByDescending(s => s, SemesterComparer).FirstOrDefault() ?? "";

    /// <summary>Orders semester ids chronologically: "25FS" &lt; "25HS" &lt; "26FS".</summary>
    public static readonly IComparer<string> SemesterComparer = Comparer<string>.Create(CompareSemesters);

    public static int CompareSemesters(string a, string b)
    {
        static (int year, int term) Key(string s) =>
            s.Length == 4 && int.TryParse(s[..2], out var y)
                ? (y, s[2..].Equals("HS", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                : (-1, -1);
        return Key(a).CompareTo(Key(b));
    }

    /// <summary>Language lives per course instance; the top-level field is usually null.</summary>
    private static List<string> ExtractLanguages(ModuleDetailDto d)
    {
        var texts = new List<string?> { d.Language };
        if (d.ModuleInstances != null) texts.AddRange(d.ModuleInstances.Select(i => i.Language));
        return texts.SelectMany(ParseLanguages).Distinct().ToList();
    }

    private static readonly string[] DayOrder =
        { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

    private static List<string> ExtractWeekdays(ModuleDetailDto d) =>
        (d.ModuleInstances ?? new List<ModuleInstanceDto>())
            .Select(i => i.Day)
            .Where(day => !string.IsNullOrWhiteSpace(day))
            .Select(day => day!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(day => Array.FindIndex(DayOrder, x => x.Equals(day, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    private static List<string> ParseLanguages(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return new List<string>();
        var result = new List<string>();
        void AddIf(string needle, string code)
        {
            if (language.Contains(needle, StringComparison.OrdinalIgnoreCase) && !result.Contains(code))
                result.Add(code);
        }
        AddIf("deutsch", "de");
        AddIf("englisch", "en");
        AddIf("english", "en");
        AddIf("französisch", "fr");
        AddIf("italienisch", "it");
        AddIf("spanisch", "es");
        return result;
    }

    /// <summary>Compacts the performanceRecords JSON (German PascalCase keys) into one line per record.
    /// The API delivers this field as a JSON string containing an array.</summary>
    private static string SummarizeAssessment(JsonElement? records)
    {
        if (records is { ValueKind: JsonValueKind.String } str)
        {
            try
            {
                using var doc = JsonDocument.Parse(str.GetString() ?? "[]");
                return SummarizeAssessment(doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        if (records is not { ValueKind: JsonValueKind.Array } arr) return string.Empty;

        var parts = new List<string>();
        foreach (var rec in arr.EnumerateArray())
        {
            if (rec.ValueKind != JsonValueKind.Object) continue;
            var sb = new StringBuilder();
            sb.Append(GetString(rec, "LeistungsnachweisArt") ?? "Leistungsnachweis");
            var detail = new[] { GetString(rec, "Pruefungsart"), GetString(rec, "Dauer"), GetString(rec, "Zeitpunkt") }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            var detailText = string.Join(", ", detail);
            if (detailText.Length > 0) sb.Append($" ({detailText})");
            parts.Add(sb.ToString());
        }
        return string.Join("; ", parts);
    }

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
