# TEXTER

Alat koji implementira TEXTER pristup: tekstualni opis domena → PMER konceptualni model sa
atribucijom (svaki element vezan za tekst iz kog je nastao) i procenom sigurnosti LLM-a → ERLD → DDL (SQL Server, MongoDB) → ERLQ upiti.

| Korak | Status |
|---|---|
| 1. Tekst → PMER dijagram sa atribucijom | ✅ |
| 2. PMER → ERLD (generisanje, izmena, provera sintakse) | ✅ |
| 3. ERLD → DDL: SQL Server | ✅ |
| 3. ERLD → DDL: MongoDB | ⏳ |
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
    Traceability/            SentenceSplitter, ConfidenceClassifier
    Diagram/                 ConvertToDiagram (PMER DER notacija)
    Erld/                    ErldGenerator (PMER model → ERLD kod)
  Texter.Erl/                ANTLR gramatike ERL jezika (Grammar/), generisani parser (Generated/), ErldSyntax
    Semantics/               ErldModelBuilder: ERLD → semantički model (razrešena imena i tipovi, kratki → dugi zapis,
                             greške i upozorenja sa redom i kolonom)
    Ddl/                     SqlServerDdlGenerator, SqlExpression (izrazi računa entiteta → T-SQL)
  Texter.Api/                POST /api/conceptualmodel/extract → { model, diagram, sentences, notes }
                             POST /api/erld/generate, /api/erld/validate → { code, errors }
                             POST /api/ddl/sqlserver { code } → { sql, errors, warnings }
  Texter.Tests/              xUnit
frontend/                    Vue 3 + Vite + GoJS
```

## Korak 1: tekst → PMER model sa atribucijom

Prompt prati G3 (fact-mediated) iz IEEE Access rada, uz dodatke za ERLD (tipovi i dužine, višeznačni i opcioni
atributi, uloge, `boundsStated`, disjoint/total za IS-A, napomene).

1. Tekst se deterministički deli na rečenice S1, S2, ... (pozicije se pamte).
2. **Jedan poziv** LLM-u (`gpt-5.4-mini`, stroga JSON šema), u dva koraka unutar istog odgovora:
   - **činjenice** F1, F2, …: atomske tvrdnje u rečima teksta, bez pojmova modelovanja, svaka sa rečenicama iz kojih je pročitana;
   - **model** građen samo iz tih činjenica: svaki entitet, atribut, veza i generalizacija citira činjenice (`evidence`,
     bez ograničenja broja, prazno ako ga ništa ne nosi). Slab entitet i agregacija posebno citiraju činjenice
     te odluke (`weakEvidence`, `aggregationEvidence`).
   - Sigurnost je kategorija: `stated` (činjenica ga imenuje), `inferred` (odluka modelovanja), `assumed`
     (konvencija, bez činjenica). Obrazloženje sa šifrom heuristike (HE/HA/HR/HK/HN) ide samo uz odluke koje bi
     čitalac preispitao. Katalog heuristika je deo prompta.
3. Citirane činjenice vode do rečenica, a te rečenice se označavaju u tekstu.
4. `ConfidenceClassifier`: stated → **High** (zeleno), inferred → **Medium** (žuto), assumed → **Low** (crveno), uz pravila:
   element bez činjenica je uvek assumed; element koji ne vodi ni do jedne rečenice → najviše Low; relacija sa
   pretpostavljenim granicama → najviše Medium; slab entitet / agregacija bez činjenica za tu odluku → najviše Low.
   Ograničenja se mogu menjati u `appsettings.json` (sekcija `Confidence`, podrazumevano u `ConfidenceOptions`).
5. Na dijagramu: prelaz mišem označava rečenice elementa, a ispod dijagrama su kategorija, obrazloženje i citirane
   činjenice. Pretpostavljene granice su crvene, sa `?`.

## Korak 2: PMER → ERLD

- `ErldGenerator` piše ERLD: kratki zapis gde može, dugi zapis za identifikujuće veze i za veze čije je neko
  preslikavanje pretpostavljeno (`@assumed`), agregacije za veze sa atributima, `specialization` za IS-A,
  `on delete cascade` od vlasnika ka slabom entitetu. Imena koja su ključne reči idu pod navodnike.
- Atribucija se čuva u jeziku: `@source(S2, S4) @confidence(stated)` ispred deklaracija, atributa i preslikavanja
  (gramatika v0.3). U editoru, prelaz preko reda označava rečenice zahteva iz kojih je element nastao.
- Svaki generisani ili ručno izmenjeni ERLD proverava se parserom generisanim iz `ERLD.g4`.
- `ERLCommon.g4` (izrazi, imena, leksika) je rekonstruisan na osnovu ERLD gramatike i primera
  `Samples/preduzece.erld`, koji se parsira bez grešaka – zameniti originalom ako se razlikuje.

## Korak 3: ERLD → DDL (SQL Server)

DDL se pravi iz ERLD koda (sa ručnim izmenama iz koraka 2): parser → `ErldModelBuilder` (semantička analiza) →
relaciona šema → SQL. Pravila preslikavanja:

| ERLD | SQL Server |
|---|---|
| entitet, agregacija, kategorija | tabela; `key` → PRIMARY KEY (bez ključa: surogat `IDENTITY`), `auto(s, i)` → `IDENTITY(s, i)` |
| slab entitet | PK = ključ vlasnika (FK) + parcijalni ključ |
| veza sa gornjom granicom 1 na jednoj strani | FK na toj strani, `NOT NULL` kad je donja granica 1; 1:1 → jedinstven FK |
| veza M:N bez atributa | spojna tabela, PK = oba FK |
| agregacija | tabela sa oba FK; bez sopstvenog ključa PK = FK-ovi (ili FK strane sa gornjom granicom 1), sa ključem UNIQUE par; može učestvovati u drugim vezama |
| IS-A | **jedna tabela za celu hijerarhiju**: diskriminator (disjoint, `total` → NOT NULL) ili `bit` kolona po podtipu (overlapping); CHECK vezuje kolone podtipa za diskriminator |
| višeznačni atribut | tabela (ključ vlasnika, vrednost), ON DELETE CASCADE |
| složeni atribut | kolona po delu |
| domen, enum, between/like/in/poređenje/check | tip + CHECK |
| `default`, `derived`, lokalni `check` | DEFAULT, izračunata kolona, CHECK – kad ih red može sam da izračuna |
| `on delete/update ...` uz preslikavanje, strukturno pravilo | referencijalna akcija FK; podrazumevano restrict (NO ACTION) |

SQL Server odbija cikluse i više putanja kaskade; takva akcija se vraća na NO ACTION uz upozorenje. Ono što tabela
sama ne može da obezbedi (donja granica na strani "više", gornja granica > 1, provere prelaza `on update check`,
navigacija i agregati, imenovana ograničenja, pravila `reject`) navedeno je na kraju skripta kao posao za trigere.
Atribucija (`@source`, `@confidence`) prelazi u SQL komentare, pa prelaz preko tabele ili kolone u koraku 3 označava
rečenice zahteva. Testovi izvršavaju generisani DDL na LocalDB-u (ako ga nema, samo ga generišu).

## Pokretanje

Za build i pokretanje dovoljan je .NET 10 SDK i Node.js.

Posle izmene gramatike (`Texter.Erl/Grammar/*.g4`) parser se ponovo generiše skriptom
`Texter.Erl/generate-parser.ps1` (ili `.sh`). Samo za to treba Java 11+, jer je ANTLR alat napisan u Javi;
generisani C# kod (`Texter.Erl/Generated/`) je u repozitorijumu.


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
