# TEXTER

Alat koji implementira TEXTER pristup: tekstualni opis domena → PMER konceptualni model sa
atribucijom (svaki element vezan za tekst iz kog je nastao) i procenom sigurnosti LLM-a → ERLD → DDL (SQL Server, MongoDB) → ERLQ upiti.

| Korak | Status |
|---|---|
| 1. Tekst → PMER dijagram sa atribucijom | ✅ |
| 2. PMER → ERLD (generisanje, izmena, provera sintakse) | ✅ |
| 3. ERLD → DDL (SQL Server, MongoDB) | ⏳ |
| 4. ERLQ upiti nad fizičkim skladištem | ⏳ |

## Struktura

```
backend/                     .NET 10
  Texter.Model/
    Conceptual/              PMER model: Entity (normal/weak), Attribute (#identifikator, tip),
                             Relationship (dva preslikavanja sa (DG,GG); sa atributima = agregacija), Generalization (IS-A).
                             Svaki ModelElement nosi atribuciju: SentenceIds, Evidence, Spans, Confidence, Rationale.
    Traceability/Sentence    rečenice S1, S2, ... sa pozicijom u tekstu
    GoJs/                    DTO za GoJS
  Texter.Logic/
    Llm/                     ILlmClient, OpenAiClient (Structured Outputs), FakeLlmClient
    Extraction/              LlmConceptualModelExtractor - jedan poziv LLM-u
      Resources/             system prompt (.md) i JSON šema odgovora
    Traceability/            SentenceSplitter, EvidenceLocator (citat → pozicija), ConfidenceClassifier
    Diagram/                 ConvertToDiagram (PMER DER notacija)
    Erld/                    ErldGenerator (PMER model → ERLD kod)
  Texter.Erl/                ANTLR gramatike ERL jezika (Grammar/ERLD.g4, ERLCommon.g4) i parser
  Texter.Api/                POST /api/conceptualmodel/extract → { model, diagram, sentences, notes }
                             POST /api/erld/generate, /api/erld/validate → { code, errors }
  Texter.Tests/              xUnit
frontend/                    Vue 3 + Vite + GoJS
```

## Korak 1: tekst → PMER model sa atribucijom

1. Tekst se deterministički deli na rečenice S1, S2, ... (pozicije se pamte).
2. **Jedan poziv** LLM-u (`gpt-5.4-mini`, stroga JSON šema) vraća PMER model. Uz svaki entitet, atribut,
   relaciju i generalizaciju ide **atribucija**: rečenice, doslovni citati, sigurnost 0–1 i obrazloženje.
   Za svako preslikavanje LLM kaže i da li su granice (DG,GG) navedene u tekstu (`boundsStated`).
3. `EvidenceLocator` pronalazi citate u tekstu (tačno → normalizovano → najbliža rečenica);
   ako citat ne može da se nađe, označavaju se cele citirane rečenice.
4. `ConfidenceClassifier`: **High** ≥ 0.8 (zeleno), **Medium** ≥ 0.5 (žuto), **Low** (crveno), uz pravila:
   element bez ikakve atribucije u tekstu → najviše Low; relacija sa pretpostavljenim granicama → najviše Medium.
   Pragovi su u `appsettings.json` (`Confidence`).
5. Na dijagramu: prelaz mišem označava tekst i rečenice elementa; pretpostavljene granice su crvene sa `?`.

## Korak 2: PMER → ERLD

- `ErldGenerator` piše ERLD: kratki zapis gde može, dugi zapis za identifikujuće veze i za veze čije je neko
  preslikavanje pretpostavljeno (`@assumed`), agregacije za veze sa atributima, `specialization` za IS-A,
  `on delete cascade` od vlasnika ka slabom entitetu. Imena koja su ključne reči idu pod navodnike.
- Atribucija se čuva u jeziku: `@source(S2, S4) @confidence(0.95)` ispred deklaracija, atributa i preslikavanja
  (gramatika v0.3). U editoru, prelaz preko reda označava rečenice zahteva iz kojih je element nastao.
- Svaki generisani ili ručno izmenjeni ERLD proverava se parserom generisanim iz `ERLD.g4`.
- `ERLCommon.g4` (izrazi, imena, leksika) je rekonstruisan na osnovu ERLD gramatike i primera
  `Samples/preduzece.erld`, koji se parsira bez grešaka – zameniti originalom ako se razlikuje.

## Pokretanje

Build backenda generiše C# parser iz gramatike (Antlr4BuildTasks), pa je potrebna **Java** (11+).


Backend (OpenAI ključ preko user-secrets, ne u appsettings):

```bash
cd backend/Texter.Api
dotnet user-secrets set "Llm:ApiKey" "sk-..."
dotnet run
```

Bez ključa, sa unapred snimljenim odgovorom (`Samples/fake/pmer_model.json`, tekst u `Samples/library-requirements.txt`):

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
- Promena prompta/šeme: `Texter.Logic/Extraction/Resources/` (šema i `LlmPmerModel` moraju da se slažu).
- Sledeći koraci (ERLD, DDL, ERLQ) dobijaju svoje foldere u `Texter.Logic` i polaze od `ConceptualModel`
  koji API već vraća uz dijagram.
