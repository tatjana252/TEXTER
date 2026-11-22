# TEXTER

Alat koji implementira TEXTER pristup: tekstualni opis domena → EER konceptualni model sa
sledljivošću (traceability) i procenom sigurnosti LLM-a → ERLD → DDL (SQL Server, MongoDB) → ERLQ upiti.

| Korak | Status |
|---|---|
| 1. Tekst → EER dijagram (traceability + confidence) | ✅ |
| 2. EER → ERLD | ⏳ |
| 3. ERLD → DDL (SQL Server, MongoDB) | ⏳ |
| 4. ERLQ upiti nad fizičkim skladištem | ⏳ |

## Struktura

```
backend/                     .NET 10
  Texter.Model/              domenski model
    Conceptual/              ConceptualModel, Entity, Attribute, Relationship, Generalization
                             (svaki ModelElement nosi Evidence, Spans, Confidence, ConfidenceLevel, Rationale)
    GoJs/                    DTO za GoJS (Node, Link, NodeCategory, LinkCategory)
  Texter.Logic/
    Llm/                     ILlmClient, OpenAiClient (Structured Outputs), FakeLlmClient
    Extraction/              IConceptualModelExtractor, LlmConceptualModelExtractor
      Resources/             system prompt (.md) i JSON schema odgovora
    Traceability/            EvidenceLocator (citat → pozicija u tekstu), ConfidenceClassifier
    Diagram/                 ConvertToDiagram (Chen notacija)
  Texter.Api/                ASP.NET Core, POST /api/conceptualmodel/extract
  Texter.Tests/              xUnit
frontend/                    Vue 3 + Vite + GoJS
  src/components/            RequirementsText, ErDiagram, ElementDetails, ConfidenceLegend
  src/diagram/               GoJS šabloni (Chen), boje po sigurnosti
  src/services/texterApi.js
```

## Kako radi korak 1

1. LLM (`gpt-5.4-mini`) dobija tekst i vraća EER model po strogoj JSON šemi. Za svaki element
   (entitet, atribut, poveznik, specijalizaciju) vraća **doslovne citate** iz teksta (`evidence`),
   **sigurnost** 0–1 i kratko obrazloženje.
2. `EvidenceLocator` pronalazi citate u originalnom tekstu u tri koraka: tačno poklapanje →
   normalizovano (velika/mala slova, razmaci, navodnici) → rečenica sa najvećim preklapanjem reči.
3. `ConfidenceClassifier` mapira sigurnost u nivo: **High** ≥ 0.8 (zeleno), **Medium** ≥ 0.5 (žuto),
   **Low** (crveno). Element za koji nijedan citat nije pronađen u tekstu nema opravdanje, pa se
   ograničava na Low. Pragovi su u `appsettings.json` (`Confidence`).
4. Prelaskom mišem preko elementa dijagrama označava se tekst koji je razlog tog elementa.

## Pokretanje

Backend (OpenAI ključ preko user-secrets, ne u appsettings):

```bash
cd backend/Texter.Api
dotnet user-secrets set "Llm:ApiKey" "sk-..."
dotnet run
```

Bez ključa, sa unapred snimljenim odgovorom (`Samples/library-response.json`):

```bash
dotnet run --launch-profile fake-llm
```

Frontend (proxy `/api` → `http://localhost:5264`):

```bash
cd frontend
npm install
npm run dev
```

Testovi: `cd backend && dotnet test`

## Nadogradnja

- Novi LLM provajder: implementirati `ILlmClient` i registrovati u `TexterServiceCollectionExtensions`.
- Promena prompta/šeme: `Texter.Logic/Extraction/Resources/` (šema i `LlmEerModel` moraju da se slažu).
- Sledeći koraci (ERLD, DDL, ERLQ) dobijaju svoje foldere u `Texter.Logic` i polaze od `ConceptualModel`
  koji API već vraća uz dijagram.
