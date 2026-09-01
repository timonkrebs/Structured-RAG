# Review: Vorschlagsqualität, Taxonomie-Graph, Kompilierung

Stand 2026-07-26, Commit `9c084a8`, Katalog vom 2026-07-18 (118 Module, 40 Tags).
Alle Zahlen am Katalog nachgemessen. Reihenfolge nach Wirkung, nicht Aufwand.

## Befunde

| # | Befund | Fix | Aufwand |
|---|--------|-----|---------|
| 1 | **Katalog enthält nur 26HS.** Jedes Modul ist `offeredIn: ["HS"]`; `plan_path` überspringt jedes FS-Semester — 3er-Kette → „7 Semester" statt 4 (live: `plan_path(9491812)` → 29HS). `plan_semester("FS")` liefert 0 Module. | Guard: nur ein Semestertyp im Katalog → HS/FS-Constraint nicht anwenden, in `notes` sagen. Ingest auf mehrere Semester; 0-Module-Slice = Fehler. | S / M |
| 2 | **Tags binär und überinklusiv.** „Kommunikation Auftreten" 46/118 (39 %), „Künstliche Intelligenz" 23 — inkl. *Finanzmanagement*, *Kritisches Denken* (je 1 AI-Nennung). Prompt erzwingt „3 to 8 tags". | Tag-Rolle `core`/`related` statt Menge, Untergrenze weg. `search_modules(tagRole)`, ★ nur für `core`. Qualitätstor: Tag > 20 % des Katalogs → Fehler. | M |
| 3 | **28/118 Module sind de/en-Duplikate.** Äquivalenzklassen existieren (`PrerequisiteGrouping`), werden aber nur für Prerequisites genutzt. Suche und Planer listen beide. | `EquivalentTo` beim Compile befüllen; `plan_semester`/`search_modules` kollabieren per Default, Widget zeigt de/en-Umschalter. | S |
| 4 | **`plan_semester` = ~286 KB / ~80 k Token.** 32 % davon liest kein Widget: `offerings` (dupliziert `lessons`), `audience`, `url`. | `detail: planning\|full`; im Default die drei Felder weglassen, `summary` kürzen. ~80 k → ~20 k. | S |
| 5 | **Keine Messung.** Kein Goldstandard, kein Eval — jede Taxonomie-Änderung ist Glaubenssache. | `eval/queries.jsonl` (~30 Fragen mit `expect` **und** `reject`), `compiler eval` → Recall@10/MRR, Report in den Refresh-PR. | M |
| 6 | **Fehlgeschlagene Anreicherung wird eingefroren.** `SourceHash` wird auch bei `enrichment == null` gesetzt (`KnowledgeCompilationService.cs:301`) → leere Tags für immer wiederverwendet. | `SourceHash = null` bei Fehlschlag; Retry; Abbruch ab N degradierten Modulen. | S |
| 7 | **Prompt-Änderung invalidiert Cache nicht.** `ComputeSourceHash` hasht nur Quellfelder. | `PromptVersion` in den Hash und ins Manifest (plus Modell/Provider). | S |
| 8 | **Compile sequenziell.** 118 LLM-Calls nacheinander (`:63-82`). | `Parallel.ForEachAsync`, Grad 4–8. | S |
| 9 | **Keine Studienverlaufs-Position.** Nur 23/118 haben Prerequisites; „foundational" existiert im Datenmodell nicht. | `PrerequisiteDepth` (deterministisch), `CurriculumStage` (LLM). | S / M |
| 10 | **`moduleType` programmabhängig, aber Skalar.** `ModuleTypes.FirstOrDefault()` (`SourceModuleMapper.cs:58`); 18/118 sind `null` → unsichtbar für `moduleType`-Filter, „Sonstige" im Widget. | `ModuleTypeByProgram`, `studyProgram`-Parameter auf `plan_semester`. | M |
| 11 | **Freitextsuche = Substring.** Kein Stemming, keine Umlaut-Faltung: „Datenbanken" findet „Datenbank" nicht. | `Keywords[]` beim Compile + Umlaut-Faltung + Suffix-Stripper. | S |
| 12 | **Prerequisite-Extraktion ohne Belege.** 70 Zeilen Regex-Heuristik (`FindRecommendationOnlyMentions`) reparieren nach. `prerequisiteNotes` enthält Compiler-Meta-Kommentare („… im Modulkatalog nicht eindeutig zuordenbar"). | Output `{code, kind, evidence}`, Zitat gegen Quelltext prüfen. Prompt: nie über den Katalog sprechen. | M |

## Taxonomie → Graph

Heute: `TagDefinition` = flacher Bag aus 40 Strings, einzige Kante ist `prerequisite` (23 Module).
**Hierarchie ist der eigentliche Fix für #2** — der Compiler hängt breite Tags an alles, weil das die einzige Art ist, Breite zu erzeugen.

1. **Tag→Tag (SKOS):** `Broader`, `Narrower` (abgeleitet), `Related`, `Facet ∈ subject|method|intent|context`. `search_modules(expandNarrower)`. Ein kleiner LLM-Schritt nach Phase 1.
2. **Modul→Modul:** `equivalentTo` (existiert), `buildsOn`, `similarTo`, `overlapsWith`, `complements`. Kandidaten deterministisch (Tag-Jaccard), LLM bestätigt nur.
3. **Ausliefern:** `compiled/edges.json` `{from, to, type, weight, source}`, Resource `catalog://graph`, Tool `get_module_neighborhood(code, depth, edgeTypes)`.
4. **Später:** Studiengänge als Knoten mit ECTS-Anforderungen → „dir fehlen 12 Wahlpflicht-ECTS".

Keine Graph-DB. Aber `SchemaVersion` wird beim Laden nirgends geprüft — vor der Schema-Erweiterung nachrüsten.

## Kompilierung, sonstiges

- Taxonomie-Design ist ein Prompt über `Truncate(description, 180)` × 118 ohne Häufigkeiten → kann Granularität nicht kalibrieren. Dreistufig: Keyphrases pro Modul → clustern/zählen → Taxonomie mit echten Zahlen und Regel „kein Tag > 15 %".
- Keine C#-Tests (#13). `PrerequisiteGrouping`, `plan_path`-Scheduling, Sentinel-Normalisierung: pure Funktionen, null Tests.
- `EnsureFresh()` stat-Syscall bei jedem Property-Zugriff. `ServerInstructions` refreshen nach Hot-Reload nicht. `search` hat `limit: 10` ohne Paging.

## Reihenfolge

1. #5 Eval · #6 #7 Compile-Bugs · #1 Guard — *erst messbar, dann Änderungen die greifen*
2. #8 parallel · #4 Payload · #3 Varianten — *billig, sofort spürbar*
3. #2 Tag-Rollen · dreistufige Taxonomie — *der eigentliche Angriff, jetzt messbar*
4. Graph 1 + #9 — *Präzision und Breite gleichzeitig*
5. Graph 2–3 + #10 — *von „passende Module" zu „kohärentes Semester"*
