using StructuredRAG.Core.Models.Catalog;

namespace StructuredRAG.Core.Services;

/// <summary>
/// Derives prerequisite OR-groups from the flat prerequisite lists (issue #21).
///
/// Some courses exist as two language editions (e.g. "Statistik 1" de / "Statistics 1" en).
/// Requirement texts name the course once, but the compiler resolves it to one or both
/// variant codes — and a flat AND-list then either blocks students who completed the other
/// variant, or demands both. This pass turns every module's prerequisites into groups of
/// interchangeable codes: completing ANY member satisfies the group.
///
/// Equivalence is deterministic (no LLM): two modules are language variants of the same
/// course when they share the same English-identity title (titleEn where present, else
/// title; normalized), the same ECTS and level, and are taught in pairwise disjoint
/// languages. The disjoint-language and ECTS guards keep genuinely different courses with
/// similar names apart (e.g. "Datenbanken" 5 ECTS vs "Datenbanktechnologien" 6 ECTS, both
/// German, share the English title "Database Technology" but must never merge).
/// </summary>
public static class PrerequisiteGrouping
{
    /// <summary>
    /// Populates <see cref="CompiledModule.PrerequisiteGroups"/> for every module. Groups
    /// preserve the flat list's order with the originally referenced code first, followed
    /// by its equivalent variants (which also repairs lists that named only one variant).
    /// With <paramref name="force"/> the groups are recomputed even when already present —
    /// the compiler uses this so reused modules pick up catalog-wide equivalence changes;
    /// loaders leave existing groups untouched.
    /// </summary>
    public static void EnsureGroups(IReadOnlyList<CompiledModule> modules, bool force = false)
    {
        var classes = BuildEquivalenceClasses(modules);
        foreach (var module in modules)
        {
            if (!force && module.PrerequisiteGroups.Count > 0) continue;
            module.PrerequisiteGroups = DeriveGroups(module, classes);
        }
    }

    /// <summary>Maps each variant's code to the full ordered equivalence class it belongs to.
    /// Codes without variants are absent from the map.</summary>
    public static Dictionary<string, List<string>> BuildEquivalenceClasses(IReadOnlyList<CompiledModule> modules)
    {
        var classes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        var candidates = modules
            .GroupBy(m => (Title: NormalizeTitle(m.TitleEn ?? m.Title), m.Ects, Level: m.Level.Trim().ToLowerInvariant()))
            .Where(g => g.Key.Title.Length > 0 && g.Count() > 1);

        foreach (var group in candidates)
        {
            // Only pairwise disjoint, known languages qualify as variants: two same-titled
            // modules taught in the same language are different courses (or data errors),
            // and without language info the equivalence cannot be established safely.
            var members = group.ToList();
            var disjoint = members.All(m => m.Languages.Count > 0) &&
                           members.SelectMany(m => m.Languages.Select(l => l.ToLowerInvariant())).Distinct().Count() ==
                           members.Sum(m => m.Languages.Select(l => l.ToLowerInvariant()).Distinct().Count());
            if (!disjoint) continue;

            var codes = members.Select(m => m.Code).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var code in codes) classes[code] = codes;
        }

        return classes;
    }

    private static List<List<string>> DeriveGroups(CompiledModule module, Dictionary<string, List<string>> classes)
    {
        var groups = new List<List<string>>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var code in module.Prerequisites)
        {
            if (!covered.Add(code)) continue; // second variant of an already-emitted group

            var group = new List<string> { code };
            if (classes.TryGetValue(code, out var cls))
            {
                foreach (var variant in cls)
                {
                    if (variant.Equals(code, StringComparison.OrdinalIgnoreCase)) continue;
                    if (variant.Equals(module.Code, StringComparison.OrdinalIgnoreCase)) continue;
                    if (covered.Add(variant)) group.Add(variant);
                }
            }
            groups.Add(group);
        }

        return groups;
    }

    private static string NormalizeTitle(string? title) =>
        string.Join(' ', (title ?? string.Empty).Trim().ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
