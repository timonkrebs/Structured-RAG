# Review: Vorschlagsqualität, Taxonomie-Graph, Kompilierung

Stand: 2026-07-26, Commit `9c084a8`, kompilierter Katalog vom 2026-07-18
(118 Module, 40 Tags, `compiled/`).

Arbeitsdokument — Befunde sind am aktuellen Katalog nachgemessen, nicht geschätzt.
Reihenfolge nach Wirkung auf die **Qualität der Modulvorschläge**, nicht nach Aufwand.

---

## 0. Kurzfassung

| # | Befund | Wirkung | Aufwand |
|---|--------|---------|---------|
| [A1](#a1) | Katalog enthält **nur 26HS** — jedes Modul ist `offeredIn: ["HS"]`. `plan_path` überspringt jedes Frühlingssemester und verdoppelt die Studiendauer; `plan_semester("FS")` liefert 0 Module | ★★★ | S (Guard) / M (Daten) |
| [A2](#a2) | Tags sind **binär und überinklusiv** — „Künstliche Intelligenz" hängt an 23/118 Modulen inkl. *Finanzmanagement* und *Kritisches Denken*; „Kommunikation Auftreten" an 46/118 (39 %) | ★★★ | M |
| [A3](#a3) | **28 von 118 Modulen sind de/en-Duplikate** desselben Kurses. Die Äquivalenzklassen werden bereits berechnet, aber nur für Prerequisites genutzt — Suche und Planer listen beide Varianten | ★★☆ | S |
| [A6](#a6) | `plan_semester` liefert **~286 KB / ~80 k Token**; davon 32 % nachweislich ungenutzt (`offerings`, `audience`, `url`) | ★★☆ | S |
| [A8](#a8) | **Keine Evaluationsschleife** — jeder Compile-Lauf verändert die Taxonomie, niemand weiss, ob die Vorschläge besser oder schlechter wurden | ★★★ | M |
| [C1](#c1) | Fehlgeschlagene LLM-Anreicherung wird mit `SourceHash` persistiert und danach **für immer wiederverwendet** | ★★☆ | S |
| [C5](#c5) | **Prompt-Änderungen invalidieren den Cache nicht** — nach einem Prompt-Rewrite bleiben alle alten Enrichments bestehen | ★★☆ | S |
| [B](#b) | Taxonomie ist ein flacher Bag aus 40 Strings ohne Kanten — der eigentliche Hebel gegen A2 | ★★★ | M–L |

---

## A. Qualität der Modulvorschläge

### <a id="a1"></a>A1 — Der Katalog kennt nur ein Semester (P0)

```
$ jq '[.[].offerings[].semesterId] | group_by(.) | map({(.[0]): length})' compiled/modules.json
[{"26HS": 118}]
```

Alle 118 Module haben `offeredIn: ["HS"]`. Das ist kein Aussage über den Rhythmus des
Moduls, sondern ein Artefakt davon, dass nur eine Semesterscheibe ingestiert wurde
(`appsettings.json` konfiguriert `26HS;27FS`, die 27FS-Daten waren zum Zeitpunkt des
Ingests offenbar noch nicht publiziert).

`plan_path` behandelt `OfferedIn` aber als harte Rhythmus-Angabe
(`ModuleCatalogTools.cs:407-426`, `FitSlot`/`SlotType`). Live nachgestellt:

```
plan_path(targetModule: 9491812 "Software Engineering", startSemester: 26HS)
  → earliestSemester: "29HS", semesterCount: 7
     26HS Grundlagen Programmierung
     27HS Datenbanken          ← 27FS übersprungen
     28HS Internettechnologien ← 28FS übersprungen
     29HS Software Engineering
```

Der Studierenden wird gesagt, sie brauche **3,5 Jahre** für eine Kette von drei
Modulen. Bei realem HS/FS-Angebot wären es 4 Semester. Das ist der schlimmste
mögliche Fehler für ein Beratungswerkzeug: er ist plausibel, konkret, quantitativ —
und falsch.

Ebenso: `plan_semester("FS")` und `("27FS")` liefern eine leere Liste
(`MatchesSemester`, `ModuleCatalogTools.cs:625-633`). Das Widget zeigt dann
„No eligible modules found for this semester."

**Massnahmen**

1. *Sofort, S:* Guard im Server. Wenn der Katalog nur einen Semestertyp kennt
   (`store.Modules.SelectMany(m => m.OfferedIn).Distinct().Count() == 1`), darf
   `plan_path` den HS/FS-Constraint nicht anwenden, sondern muss ihn als unbekannt
   behandeln und das in `notes` sagen: *„Der Katalog enthält nur HS-Angebote —
   der Pfad nimmt an, dass Module in beiden Semestern laufen; prüfe die offiziellen
   Semesterangaben."* Analog eine Warnung in `plan_semester`, wenn das angefragte
   Semester im Katalog gar nicht vorkommt (statt stiller Leermenge).
2. *M:* `Ingest:Semesters` auf mehrere reale Semester ausweiten und die Vollständigkeit
   im Ingest-Log prüfen (`Slice 27FS × … : 0 modules` sollte ein Fehler sein, kein Info-Log).
3. *M:* `OfferedIn` sauber von „im Katalog beobachtet" trennen — z. B.
   `OfferedIn` (beobachtet) vs. `Rhythm` (behauptet). Solange nur eine Scheibe
   ingestiert ist, ist `OfferedIn` keine belastbare Planungsgrundlage.

### <a id="a2"></a>A2 — Tags sind binär und überinklusiv (P0)

Verteilung im aktuellen Katalog (118 Module):

```
46  Kommunikation Auftreten     ← 39 % des Katalogs
36  Gesellschaft Kultur
33  Management Grundlagen
32  Praxislernen
30  Digitale Transformation
30  Internationales Geschäft
23  Künstliche Intelligenz
...
 5  Maschinelles Lernen
 3  Big Data
 3  Blockchain
```

Ein Tag, den 39 % des Katalogs tragen, hat praktisch keinen Informationsgehalt.
Konkret für „Künstliche Intelligenz" — Anzahl AI-bezogener Nennungen im
Beschreibungstext der 23 getaggten Module:

```
26  The Trust Factor: AI for Business Transformation
13  Generative AI for Business
12  AI-assisted Software Development
...
 1  Finanzmanagement
 1  IT Projektmanagement
 1  Kritisches Denken & Wissenschaftliches Schreiben
 1  Challenges in the Global Economic System
 1  Soziale Roboter
```

*Finanzmanagement* und *Machine Learning with Python* sind für den Client identisch
„KI-Module". Wer fragt „welche Module bringen mir KI bei?", bekommt
wissenschaftliches Schreiben vorgeschlagen. Die Server-Instructions kompensieren das
mit „RECALL OVER PRECISION" und „Tags sind approximativ" — das ist eine Entschuldigung
für ein Datenproblem, kein Fix, und es verlagert die Arbeit ins Kontextfenster des
Clients.

Ursache liegt im Compile-Prompt (`KnowledgeCompilationService.cs:215`):
`"tags": ["3 to 8 canonical German tag names"]` — eine Untergrenze von 3 zwingt
das Modell, auch bei einem eindeutigen Ein-Thema-Modul zwei Füll-Tags zu vergeben.
Und es gibt keine Möglichkeit auszudrücken, dass ein Tag *randständig* ist.

**Massnahmen**

1. **Tag-Rolle statt Tag-Menge** (der eigentliche Fix, M):
   `CompiledModule.Tags: List<string>` → `List<ModuleTag> { Name, Role }` mit
   `Role ∈ { core, related }`. Prompt: *„höchstens 3 `core`-Tags — das Modul handelt
   substanziell davon; beliebig viele `related` — wird gestreift"*. Untergrenze streichen.
   - `search_modules` bekommt `tagRole: 'core' | 'any'` (Default `any`, aber Ranking
     bevorzugt `core`-Treffer).
   - `plan_semester.interestMatches` zählt nur `core` als ★-Treffer.
   - `get_catalog_overview` schreibt `core` fett / `related` in Klammern.
   Rückwärtskompatibel serialisierbar, wenn `tags` als flache Liste bestehen bleibt
   und `tagRoles` additiv dazukommt.
2. **Compile-Zeit-Qualitätstor** (S): `ValidateCatalog`
   (`KnowledgeCompilationService.cs:349-364`) warnt heute nur bei *unbenutzten* Tags.
   Der umgekehrte Fall ist schlimmer. Ergänzen: Tag über ~20 % des Katalogs → `LogError`
   plus Eintrag im Manifest, damit der Refresh-PR das sichtbar macht.
3. **Kalibrierung in den Taxonomie-Prompt** (S): Die Regel *„no overly generic tags
   like Bildung"* (`:145`) ist nicht messbar. Ersetzen durch eine harte Zielvorgabe:
   *„kein Tag soll auf mehr als ~15 % des Katalogs passen; wenn doch, spalte ihn auf"* —
   und dem Modell die tatsächlichen Häufigkeiten aus einem Vorlauf mitgeben (→ [C2](#c2)).

### <a id="a3"></a>A3 — Sprachvarianten erscheinen doppelt in jeder Liste (P1)

14 Kurspaare, also 28 von 118 Modulen (24 %), sind de/en-Editionen desselben Kurses:

```
applied mathematics 1 · enterprise systems · critical thinking & academic writing
database technology · digital business · digital ethics · digital marketing
programming foundations · it project management · organisational behaviour
principles of management · statistics 1 · statistics 2 · web-based applications
```

`PrerequisiteGrouping.BuildEquivalenceClasses` (`PrerequisiteGrouping.cs:43-67`)
berechnet diese Klassen bereits deterministisch — aber sie werden ausschliesslich
für Prerequisite-Gruppen und für `ExpandWithVariants` (abgeschlossene Module)
genutzt. In `search_modules`, `plan_semester.eligible` und `get_catalog_overview`
tauchen beide Varianten als eigenständige Zeilen auf.

Folgen: die Ergebnisliste ist zu einem Viertel redundant; das Modell kann
versehentlich beide Varianten in denselben Plan legen; im Planer-Widget stehen
„Statistik 1" und „Statistics 1" nebeneinander, ohne dass erkennbar ist, dass es
derselbe Kurs ist.

**Massnahme (S):** Äquivalenz zur First-Class-Kante machen —
`CompiledModule.EquivalentTo: List<string>` beim Compile befüllen (die Berechnung
existiert) und ausspielen:
- `ModuleSummary` bekommt `variants: [{code, language}]`.
- `plan_semester` und `search_modules` kollabieren Varianten per Default zu einem
  Eintrag (Parameter `collapseVariants`, Default `true`), die Sprache wandert in ein
  Feld statt in eine eigene Zeile.
- Das Widget rendert eine Zeile mit de/en-Umschalter; die Wahl geht in `chosenClass`-
  Manier in den State.

Das reduziert die Ergebnismenge um ~24 % und beseitigt eine ganze Fehlerklasse.

### <a id="a4"></a>A4 — Es fehlt die Position im Studienverlauf (P1)

Nichts im Katalog sagt, *wann* ein Modul gedacht ist. Nur 23 von 118 Modulen haben
überhaupt Prerequisites, der Graph ist also viel zu dünn, um eine Reihenfolge zu
implizieren. Ein Erstsemestriger kann „Big, Semi- and Unstructured Data" vorgeschlagen
bekommen, ein Sechstsemestriger „Statistik 1". Die Server-Instructions sagen dem Modell
„mandatory/foundational modules first" — aber im Datenmodell gibt es kein
„foundational".

**Massnahmen**

1. *S, deterministisch:* `CompiledModule.PrerequisiteDepth` = längste Kette bis zu
   diesem Modul im Prerequisite-DAG. Kostet nichts, hilft sofort beim Sortieren.
2. *M, LLM:* `CurriculumStage ∈ { foundation, core, advanced, capstone }` beim
   Enrichment mitkompilieren. Das Modell hat Beschreibung, ECTS, Typ und
   Voraussetzungstext vorliegen — das reicht für eine brauchbare Einstufung.
3. *L:* Den echten Studienplan (Regelsemester pro Modul) als zweite Quelle anbinden.
   Steht nicht in der bariapi, wäre aber der eigentlich richtige Weg.

### <a id="a5"></a>A5 — `moduleType` ist programmabhängig, wird aber als ein Wert geführt (P2)

`SourceModuleMapper.cs:58`: `ModuleType = d.ModuleTypes?.FirstOrDefault()`.
Das DTO führt `ModuleTypes` als flache Liste ohne Zuordnung zu `StudyPrograms` —
dasselbe Modul kann in Wirtschaftsinformatik Pflicht und in Betriebsökonomie Wahl sein.
Wir behalten willkürlich das erste Element. *(Zu verifizieren: ob die API die Paarung
überhaupt hergibt — aus dem Netz dieser Session ist bariapi nicht erreichbar.)*

Zusätzlich haben **18 von 118 Modulen gar keinen Typ** — durchweg
programmübergreifende Wahlangebote (Sprachkurse, *Taiwan Study Tour*,
*Generative AI for Business*, *Blockchain*, …). Konsequenzen:
`search_modules(moduleType: "Wahlmodul")` findet sie nie (`"Wahlmodul".Equals(null)`
ist `false`, `ModuleCatalogTools.cs:102`), und im Planer-Widget landen sie unter
„Sonstige" — obwohl es genau die frei wählbaren Module sind, nach denen Studierende
am häufigsten fragen.

**Massnahme (M):** `ModuleTypeByProgram: Dictionary<string,string>` statt Skalar,
plus ein `studyProgram`-Parameter auf `plan_semester`, der die Kategorisierung
auflöst. Für die 18 typlosen: aus `StudyPrograms.Count >= 4` + fehlendem Typ lässt
sich „programmübergreifendes Wahlangebot" deterministisch ableiten — besser als `null`.

### <a id="a6"></a>A6 — `plan_semester` kostet ~80 k Token pro Aufruf (P1)

Nachgerechnet auf dem aktuellen Katalog:

```
26HS: 118 Module → 286 097 Zeichen JSON  (~82 000 Token)
  davon offerings + audience + url:  94 068 Zeichen  (32 %)
```

`ModuleSummary` (`ModuleCatalogTools.cs:697-744`) enthält:
- `offerings` — die **komplette** Angebotsliste inklusive aller `lessons`, während
  `lessons` daneben nochmal die Slots des passenden Semesters führt. Im
  Ein-Semester-Katalog sind die beiden Felder byteweise identisch. Kein Widget liest
  `offerings` im Semesterplaner (`grep` bestätigt: nur `module-comparer` und
  `path-planner` nutzen es).
- `audience` — 38 k Zeichen, von **keinem** Widget gelesen und für die
  Planungsentscheidung nicht nötig (das Modell hat `summary` + `tags`).
- `url` — 9 k Zeichen, deterministisch aus `planSemesterModulId` ableitbar.

Das ist nicht nur Kosten und Latenz: 80 k Token belegen bei jedem Planungsaufruf den
grössten Teil des Kontextfensters, in dem der Client danach *reasonen* soll. Die
Vorschlagsqualität hängt direkt daran. Die Instructions empfehlen ausserdem, vorher
`get_catalog_overview` (~7 k Token) zu laden — der Katalog wird also faktisch zweimal
übertragen.

**Massnahme (S):** Ein `detail`-Parameter auf `plan_semester`
(`planning` (Default) | `full`). Im `planning`-Modus: `offerings` weglassen,
`audience` weglassen, `url` weglassen, `summary` auf ~200 Zeichen kürzen. Grobe
Schätzung: 80 k → ~20 k Token bei unverändertem Widget-Verhalten (das Widget braucht
`summary`/`summaryEn`, weil es die Sprache clientseitig wählt — `_host.js:170-171`).

Ergänzend: eine `catalog://index`-Resource statt eines Tool-Calls für die Übersicht
lässt Hosts, die Resources cachen, den Katalog nur einmal laden.

### <a id="a7"></a>A7 — Freitextsuche ist reines Substring-Matching (P2)

`CatalogStore.ScoreModule` (`CatalogStore.cs:133-151`) macht `Contains` auf
lowercased Text mit festen Gewichten (Titel 5, Tags 3, Body 1). Kein Stemming, keine
Umlaut-Faltung, keine Komposita-Zerlegung, kein IDF. Praktisch heisst das:
„Datenbanken" findet „Datenbank" nicht, „Programmieren" findet „Programmierung"
nicht, „fur" findet „für" nicht. Die Instructions bewerben die Freitextsuche
ausdrücklich als Gegenprobe, wenn ein Tag-Sweep dünn aussieht — die Gegenprobe ist
schwächer als beworben.

**Massnahme (S–M):** Passend zur „structured RAG"-These die Arbeit in den Compile
verschieben: ein Feld `Keywords: List<string>` pro Modul (Synonyme, Abkürzungen,
umgangssprachliche Formulierungen, Tool-/Sprachnamen), beim Enrichment mitgeneriert,
und im Scoring mit Gewicht 4 zwischen Titel und Tags einhängen. Dazu Umlaut-Faltung
und ein simpler deutscher Suffix-Stripper (`-en`, `-ung`, `-e`, `-s`) — 20 Zeilen.
Kein Embedding nötig.

### <a id="a8"></a>A8 — Es gibt keine Messung der Vorschlagsqualität (P0-Enabler)

Das ist der wichtigste strukturelle Befund: Es existiert **kein Goldstandard und kein
Eval-Lauf**. Die Taxonomie wird bei jedem Compile neu vom LLM entworfen (mit der
vorherigen als Basis, aber änderbar), Tags werden neu vergeben — und niemand kann
sagen, ob die Vorschläge danach besser oder schlechter sind. Jede der Massnahmen oben
ist ohne diese Schleife nicht verifizierbar.

**Massnahme (M):**
1. `eval/queries.jsonl` — ~30 realistische Studierendenfragen mit erwarteten
   Modulcodes, von Hand einmal erstellt:
   ```jsonl
   {"q": "Ich will Machine Learning lernen", "expect": ["9491769","9212342","9491771"], "reject": ["9827881"]}
   {"q": "Welche Module helfen mir beim wissenschaftlichen Schreiben?", "expect": ["9774570"]}
   ```
2. `dotnet run --project StructuredRAG.Compiler -- eval` — fährt `search` und
   `search_modules` gegen den kompilierten Katalog und rechnet Recall@10, MRR und
   *false-positive rate* über die `reject`-Listen. Rein deterministisch, kein LLM,
   Sekunden Laufzeit.
3. Den Report als Kommentar in den Refresh-PR (`.github/workflows/refresh-catalog.yml`)
   schreiben. Damit wird jede Katalog-Neukompilierung überprüfbar statt geglaubt.

Die `reject`-Liste ist dabei mindestens so wichtig wie `expect` — sie misst genau das
Überinklusiv-Problem aus [A2](#a2).

---

## <a id="b"></a>B. Taxonomie Richtung Graph

Heute ist `TagDefinition` (`CompiledCatalog.cs:28-35`) ein flacher Bag:
`Name, NameEn, Description, DescriptionEn, ModuleCount`. Module zeigen auf Tags,
Tags zeigen auf nichts. Der einzige echte Graph im System ist die Prerequisite-Kante —
und die existiert auf 23 von 118 Modulen.

Wichtig: **B1 ist der eigentliche Fix für [A2](#a2).** Der Compiler hängt heute den
breiten Tag an alles, weil das die einzige Art ist, Breite zu erzeugen. Mit Hierarchie
kann jedes Modul präzise seine *spezifischsten* Tags bekommen, und die Breite entsteht
beim Traversieren.

### <a id="b1"></a>B1 — Tag→Tag-Kanten (SKOS-Modell), M

`TagDefinition` erweitern:

```csharp
public List<string> Broader  { get; set; } = new();  // "Maschinelles Lernen" → "Künstliche Intelligenz"
public List<string> Narrower { get; set; } = new();  // abgeleitet, nicht vom LLM
public List<string> Related  { get; set; } = new();  // assoziativ, symmetrisch
public string Facet { get; set; } = "";              // subject | method | intent | context
```

Das `Facet`-Feld räumt ein bestehendes Problem auf: die 40 Tags mischen heute
Fachgebiete („Datenbanken"), Absichten („Karriereentwicklung") und Lehrformate
(„Praxislernen") in einem Namensraum. `allOfTags` über verschiedene Facetten hinweg
verhält sich dadurch unvorhersehbar — `Praxislernen AND Datenbanken` meint etwas
grundsätzlich anderes als `Statistik AND Datenanalyse`.

Serving:
- `search_modules(expandNarrower: true)` — „Künstliche Intelligenz" zieht
  automatisch „Maschinelles Lernen" und „Verantwortungsvolle KI" mit.
- `list_tags` / `catalog://taxonomy` geben den Baum aus statt einer flachen Liste;
  das Client-Modell sieht sofort, welcher Tag der spezifische und welcher der
  Oberbegriff ist.
- Die Facette macht `search_modules` erklärbar: „Fachgebiet = KI, Format = Praxislernen".

Die Kanten kommen aus einem eigenen, kleinen LLM-Schritt am Ende der Phase 1 (die
Taxonomie ist da schon fertig — 40 Tags, ein Prompt, ein Aufruf pro Compile).
`Narrower` wird deterministisch aus `Broader` invertiert; Zyklen beim Compile prüfen.

### <a id="b2"></a>B2 — Modul→Modul-Kanten jenseits von Prerequisites, M

| Kante | Herkunft | Wofür |
|---|---|---|
| `equivalentTo` | deterministisch, existiert bereits ([A3](#a3)) | Varianten kollabieren |
| `buildsOn` | LLM, weich | „Statistik 2 baut auf Statistik 1 auf", auch ohne harte FHNW-Voraussetzung |
| `similarTo` | Tag-Jaccard + Titel/Beschreibungs-Overlap, LLM bestätigt | „Was ist der Unterschied zwischen X und Y?", „X ist voll — was stattdessen?" |
| `complements` | LLM | „passt gut ins selbe Semester" |
| `overlapsWith` | LLM | „nimm nicht beide, der Inhalt überschneidet sich" |

Genau diese Kanten sind der Unterschied zwischen „hier sind 12 Module, die zu deinen
Tags passen" und „hier ist ein kohärentes Semester". `similarTo` und `overlapsWith`
sind zudem die Grundlage für Erklärungen — das, was ein Beratungsgespräch von einer
Filterliste unterscheidet.

Kandidatengenerierung deterministisch (Jaccard über Tags + Termüberlappung), LLM nur
zum Bestätigen und Labeln der Top-Kandidaten. Das hält die Kosten bei O(Module) statt
O(Module²).

### <a id="b3"></a>B3 — Den Graphen auch als Graph ausliefern, S

- Neues Artefakt `compiled/edges.json` als typisierte Kantenliste:
  `{from, to, type, weight, source: "llm"|"derived"|"official"}`.
  Getrennt von `modules.json` zu halten ist wichtig: die Kantenliste ist im
  Refresh-PR diffbar, und Kanten aus verschiedenen Quellen bleiben unterscheidbar.
- Neue Resource `catalog://graph`.
- Neues Tool `get_module_neighborhood(code, depth, edgeTypes)` — liefert in einem
  Aufruf Modul + Voraussetzungen + Folgemodule + Äquivalente + Ähnliche +
  Ergänzende. Das ist das deutlich bessere „erklär mir dieses Modul im Kontext"-
  Primitiv als `fetch`, und es bleibt zero inference.
- Für die Widgets: ein kleiner Voraussetzungs-/Folgegraph neben dem Modul ist damit
  ohne zusätzlichen Serverstate renderbar.

### B4 — Studiengänge als Knoten, L

`StudyPrograms` sind heute Strings auf Modulen. Als Knoten mit ECTS-Anforderungen pro
Kategorie und programmspezifischem `moduleType` ([A5](#a5)) wird aus „plan my
semester" ein Constraint-Problem, das der Client tatsächlich lösen kann: *„dir fehlen
noch 12 Wahlpflicht-ECTS in der Vertiefung X"*. Das ist die Frage, die Studierende
wirklich haben.

### B5 — Speicherung: keine Graphdatenbank

Bei JSON-Artefakten bleiben. Eine typisierte Kantenliste neben `modules.json` und
`taxonomy.json` ist diffbar, versionierbar und später in beliebige Werkzeuge ladbar.
Was allerdings jetzt fällig ist: `CatalogManifest.SchemaVersion` ist auf `1`
hartkodiert (`CompiledCatalog.cs:16`) und wird beim Laden **nirgends geprüft**
(`CatalogStore.Reload`). Sobald das Schema wächst, lädt ein alter Server neue
Artefakte still falsch. Version beim Laden validieren, bevor das Schema sich ändert.

---

## C. Kompilierung

### <a id="c1"></a>C1 — Fehlgeschlagene Anreicherung wird dauerhaft eingefroren (Bug, S)

`CompileModuleAsync` (`KnowledgeCompilationService.cs:227-302`): Wenn `ExtractJson`
`null` liefert, wird geloggt und mit Fallback weitergemacht — `Summary` =
abgeschnittene Beschreibung, `Audience` = `""`, `Tags` = leer. Am Ende wird trotzdem
`SourceHash = sourceHash` gesetzt (`:301`).

Beim nächsten Compile greift damit der Reuse-Pfad (`:71-78`), `RefreshPassThrough`
trägt die leere Anreicherung weiter — **für immer**, bis sich der Quelltext ändert.
Ein einzelner Parse-Fehler vergiftet ein Modul dauerhaft, und `ValidateCatalog` warnt
nur (`:354-358`), bricht nicht ab.

**Fix:** `SourceHash` bei fehlgeschlagener Anreicherung auf `null` lassen — der
Reuse-Filter (`:58`, `Where(m => m.SourceHash != null)`) schliesst das Modul dann
automatisch vom Reuse aus und der nächste Lauf versucht es erneut. Zusätzlich:
ein Retry mit Repair-Prompt, und einen Abbruch, wenn mehr als N Module degradiert sind.

### <a id="c2"></a>C2 — Taxonomie-Design ist ein Schuss auf abgeschnittenen Text (M)

`DesignTaxonomyAsync` (`:110-169`) baut einen einzigen Prompt aus
`Truncate(description, 180)` pro Modul. Bei 118 Modulen sind das ~21 KB Übersicht;
bei 500 Modulen wird das unbrauchbar, und 180 Zeichen sind sehr wenig, um daraus ein
Vokabular zu entwerfen. Vor allem aber sieht das Modell keine Häufigkeiten — es kann
die Granularität nicht an der Verteilung kalibrieren, und genau daher kommt
„Kommunikation Auftreten: 46 Module".

**Vorschlag — drei Stufen statt einer:**
1. Pro Modul freie Keyphrase-Extraktion (parallelisierbar, billig, volle Beschreibung).
2. Keyphrases deterministisch clustern und zählen.
3. Taxonomie aus den Clustern *mit echten Häufigkeiten* entwerfen — dann kann der
   Prompt „kein Tag über 15 % des Katalogs" auch tatsächlich befolgt werden.

Nebeneffekt: Die Keyphrases aus Stufe 1 sind direkt das `Keywords`-Feld aus [A7](#a7).

### <a id="c3"></a>C3 — Der Compile läuft strikt sequenziell (S)

`foreach (var module in modules) { … await CompileModuleAsync(…) }` (`:63-82`) —
118 LLM-Aufrufe nacheinander. Der einzige geteilte Zustand ist die (read-only)
Taxonomie und die Ergebnisliste. Mit `Parallel.ForEachAsync` und
`MaxDegreeOfParallelism` ~4–8 fällt ein Vollcompile von Stunden auf Minuten.
Zu prüfen: ob der Codex-CLI-Transport (`CodexCliService`) nebenläufige Prozesse
verträgt — sonst pro Provider ein konfigurierbares Limit.

Das ist nicht nur Bequemlichkeit: solange ein Vollcompile teuer ist, traut sich
niemand, den Prompt zu ändern — und das blockiert alle Verbesserungen aus [A2](#a2)
und [C2](#c2).

### <a id="c4"></a>C4 — Prerequisite-Extraktion ohne Belege und ohne Verifikation (M)

Prerequisites blockieren Module im Planer. Eine falsche Extraktion ist teuer. Heute
ist es ein einzelner LLM-Schuss, validiert nur gegen „existiert der Code".

Symptomatisch dafür ist `FindRecommendationOnlyMentions`
(`KnowledgeCompilationService.cs:430-496`): ~70 Zeilen handgeschriebene
Wortlauf-Heuristik mit 60-%-Schwellen und Exakttitel-Tiebreak, die im Nachhinein
repariert, was der LLM-Pass nicht sauber geliefert hat. Diese Komplexität ist ein
Symptom, keine Lösung.

**Vorschlag:** Die Extraktion belegpflichtig machen —
`{code, kind: required|recommended, evidence: "<wörtliches Zitat>"}` — und beim
Compile prüfen, ob das Zitat wirklich im Quelltext vorkommt. Das ist genauer, es macht
`FindRecommendationOnlyMentions` grösstenteils überflüssig (`kind` kommt direkt vom
Modell, belegt), es ist auditierbar — und das Widget kann dem Studierenden *zeigen*,
warum ein Modul blockiert ist, statt nur einen Code zu nennen.

Verwandt, im Live-Output sichtbar: `prerequisiteNotes` soll laut Prompt (`:219`)
unauflösbare Anforderungen „verbatim-ish" enthalten. Tatsächlich kommt heraus:

```
"9491812: Java (Programming 2): Programming 2 ist im verfügbaren Modulkatalog
 nicht eindeutig zuordenbar."
```

Das ist ein Meta-Kommentar des Compilers über den Katalog, der bis in eine
studierendensichtbare Notiz durchschlägt. Der Prompt muss explizit verbieten, über
den Katalog zu sprechen.

### <a id="c5"></a>C5 — Prompt-Änderungen invalidieren den Cache nicht (Bug, S)

`ComputeSourceHash` (`:378-397`) hasht ausschliesslich Quellfelder. Wer den
Enrichment-Prompt verbessert und neu kompiliert, bekommt für **jedes unveränderte
Modul** die alte Anreicherung zurück — die Verbesserung greift nur bei Modulen, deren
Text sich zufällig auch geändert hat. Das ist genau dann tückisch, wenn man an
[A2](#a2) arbeitet.

**Fix:** Eine `PromptVersion`-Konstante in den Hash aufnehmen (und beim Ändern des
Prompts hochzählen). Zusätzlich ins Manifest: Modellname, Provider und
`PromptVersion` — heute lässt sich einem kompilierten Katalog nicht ansehen, womit er
erzeugt wurde, und zwei Läufe über dieselbe Quelle liefern verschiedene Taxonomien.

---

## D. Sonstiges

- **Keine Tests für den C#-Teil** (Issue #13). Die Widget-Playwright-Suite existiert
  (4 Specs), aber `PrerequisiteGrouping`, das `plan_path`-Scheduling,
  `NormalizeNoPrereqSentinel` und `FindRecommendationOnlyMentions` sind reine
  Funktionen mit subtilen Regeln und null Tests. Genau dort werden Regressionen
  entstehen. Ein `StructuredRAG.Core.Tests`-Projekt mit ~30 Fällen ist ein Nachmittag
  und der beste Schutz für alles oben.
- `CatalogStore.EnsureFresh()` macht bei **jedem** Property-Zugriff ein
  `File.GetLastWriteTimeUtc` — und `Modules` wird in Schleifen angefasst. Ein Syscall
  pro Zugriff; mit einer 1-Sekunden-TTL erledigt.
- `Program.cs:28-97`: Die `ServerInstructions` werden einmal via `PostConfigure`
  gebaut und nach einem Katalog-Hot-Reload nie aktualisiert (im Kommentar korrekt
  dokumentiert). Das Tag-Vokabular in den Instructions kann also vom tatsächlichen
  Katalog abweichen. Da der Refresh-Workflow einen PR öffnet (und damit ein Redeploy),
  heute praktisch folgenlos — aber eine Falle, sobald irgendwann live nachkompiliert wird.
- `search` hat ein hartkodiertes `limit: 10` (`ModuleCatalogTools.cs:30`) ohne Paging
  und ohne `total`. Bei 118 Modulen unkritisch, bei mehreren Studiengängen
  ([Issue #26](https://github.com/timonkrebs/structured-rag/issues/26)) nicht mehr.
- Server ohne Authentifizierung (bekannt, im Backlog).

---

## Vorgeschlagene Reihenfolge

**Erst messbar machen, dann verbessern** — sonst ist keine der Tag-Änderungen verifizierbar.

1. [A8](#a8) Eval-Harness + Goldstandard · [C1](#c1) + [C5](#c5) Compile-Bugs ·
   [A1](#a1) Semester-Guard
   → *danach ist bekannt, wo man steht, und Änderungen greifen überhaupt*
2. [C3](#c3) paralleler Compile · [A6](#a6) Payload-Diät · [A3](#a3) Varianten kollabieren
   → *billige, sofort spürbare Gewinne; macht Prompt-Iteration bezahlbar*
3. [A2](#a2) Tag-Rollen + Qualitätstor · [C2](#c2) dreistufiges Taxonomie-Design
   → *der eigentliche Angriff auf die Vorschlagsqualität, jetzt messbar*
4. [B1](#b1) Tag-Hierarchie + Facetten · [A4](#a4) Curriculum-Stufe
   → *Präzision und Breite gleichzeitig, statt gegeneinander*
5. [B2](#b2) + [B3](#b3) Modulkanten und Graph-Serving · [A5](#a5) programmabhängige Typen
   → *von „passende Module" zu „kohärentes Semester"*
