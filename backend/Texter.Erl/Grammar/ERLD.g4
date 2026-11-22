/*
 * ERLD – Entity Relationship Language for Data Definition   (v0.3)
 *
 * Jezik za definisanje proširenog modela entiteta i relacija (PMER).
 * Prati podelu iz specifikacije PMER-a:
 *
 *   STRUKTURA      domeni, enumeracije, entiteti, atributi, veze,
 *                  agregacije, specijalizacije, kategorije
 *   OGRANIČENJA    šta mora da važi (deklarativno, račun entiteta)
 *   PRAVILA        šta se radi kad se ograničenje naruši
 *                  (događaj – uslov – akcija, sa porukom)
 *
 * Gde ima smisla, postoje DUGI i KRATKI zapis. Kratki zapis je skraćenica
 * koju semantička analiza razvija u dugi:
 *   - veza:        blok sa dva preslikavanja  |  jedan red sa <->
 *   - ograničenje: constraint + rule          |  check/predikat na atributu
 *   - str. pravilo: rule ... for delete ...   |  on delete cascade uz preslikavanje
 *
 * Strukturna ograničenja se NE pišu: izvode se iz donjih granica
 * kardinalnosti. Ako za njih nije navedeno pravilo, podrazumeva se restrict.
 *
 * v0.3 (TEXTER): anotacije (@source, @confidence, ...) ispred deklaracija,
 * atributa i preslikavanja – čuvaju atribuciju modela na tekst zahteva.
 */
grammar ERLD;

options { caseInsensitive = true; }

import ERLCommon;

// ======================================================================
//  STRUKTURA FAJLA
// ======================================================================

erldFile
    : modelDecl? declaration* EOF
    ;

modelDecl
    : MODEL name ';'
    ;

declaration
    : annotation* declarationBody
    ;

declarationBody
    : domainDecl
    | enumDecl
    | entityDecl
    | relationshipDecl
    | aggregationDecl
    | specializationDecl
    | categoryDecl
    | constraintDecl
    | ruleDecl
    ;

// ======================================================================
//  ANOTACIJE (TEXTER atribucija)
// ======================================================================

/*
 * Metapodaci koji ne menjaju semantiku modela. TEXTER koristi:
 *   @source(S2, S4)      rečenice zahteva iz kojih je element nastao
 *   @confidence(0.95)    sigurnost LLM-a (0..1)
 * Ostala imena su slobodna (@todo('proveriti kardinalnost'), @author(...)).
 */
annotation
    : '@' name ('(' annotationArg (',' annotationArg)* ')')?
    ;

annotationArg
    : literal
    | name
    ;

// ======================================================================
//  DOMENI I ENUMERACIJE (rečnik podataka)
// ======================================================================

/*
 *  domain DNaziv    = string(35) like '[A-Ž]%';
 *  domain DOcena    = int between 6 and 10;
 *  domain DSemestar = string(4) in ('I', 'II', 'III', 'IV');
 *  domain DIznos    = money check (value > 0 and value < 2500);
 */
domainDecl
    : DOMAIN name '=' dataType valueConstraint* ';'
    ;

// enum VrstaIsplate { PLATA, REGRES, BONUS }
enumDecl
    : ENUM name '{' enumValue (',' enumValue)* ','? '}'
    ;

enumValue
    : name ('=' literal)?
    ;

/*
 * Tip podatka. Ime tipa se proverava semantički. Ugrađeni tipovi (v0):
 *   tinyint, smallint, int, long, decimal(p,s), money, float, bool,
 *   char(n), string(n), text, date, time, datetime, uuid
 * plus imena domena i enumeracija. Sufiks [] = višeznačni atribut.
 */
dataType
    : name ('(' INT (',' INT)? ')')? MULTI?
    ;

/*
 * Vrednosno ograničenje nad jednom vrednošću (domena ili atributa).
 * Predikat bez subjekta odnosi se na samu vrednost:
 *   between 6 and 10  |  like 'PR%'  |  in (1, 2, 3)  |  > 0
 * Za složenije uslove: check (izraz), gde je vrednost "value".
 */
valueConstraint
    : NOT? BETWEEN expression AND expression        # betweenConstraint
    | NOT? LIKE STRING                              # likeConstraint
    | NOT? IN '(' expressionList ')'                # inConstraint
    | compOp expression                             # compareConstraint
    | CHECK '(' expression ')'                      # checkConstraint
    ;

// ======================================================================
//  ENTITETI I ATRIBUTI
// ======================================================================

/*
 *  entity Radnik { ... }
 *  entity RadnoMesto as "Radno mesto" { ... }
 *  weak entity Isplata { partial key iId : int; ... }   -- vlasnik se zadaje
 *                                                         identifying vezom
 * Entitet sadrži samo atribute, jedinstvenost i lokalna ograničenja.
 * Veze se definišu van entiteta.
 */
entityDecl
    : WEAK? ENTITY name displayName? '{' entityMember* '}'
    ;

displayName
    : AS STRING
    | AS QUOTED_ID
    ;

entityMember
    : annotation* entityMemberBody
    ;

entityMemberBody
    : attributeDecl
    | uniqueDecl
    | localConstraint
    ;

/*
 *  key rId : int auto(100, 2);
 *  partial key iId : int;
 *  ime : DNaziv;
 *  ocena : int between 6 and 10;                       -- kratki zapis ograničenja
 *  budzet : money > 0
 *           on update check (abs(new.budzet - old.budzet) <= 0.12 * old.budzet)
 *           with err 'Budžet se ne može promeniti za više od 12%.';
 *  telefon : string(20)[] optional;
 *  adresa { ulica : string(50); grad : string(30); } optional;
 *  derived prosek : decimal(4,2) = sum(ispiti.ocena) / card(ispiti);
 */
attributeDecl
    : attrModifier* name ':' dataType attrOption* ';'                    # simpleAttribute
    | attrModifier* name '{' attributeDecl+ '}' attrOption* ';'?         # compositeAttribute
    | DERIVED name ':' dataType '=' expression attrOption* ';'           # derivedAttribute
    ;

attrModifier
    : KEY
    | PARTIAL KEY
    | UNIQUE
    ;

attrOption
    : OPTIONAL                                                  # optionalOption
    | REQUIRED                                                  # requiredOption
    | valueConstraint withClause?                               # valueOption
    | ON UPDATE CHECK '(' expression ')' withClause?            # transitionOption
    | AUTO ('(' INT ',' INT ')')?                               # autoOption
    | DEFAULT expression                                        # defaultOption
    | COMMENT STRING                                            # commentOption
    ;

// unique (naziv, sluzba);
uniqueDecl
    : UNIQUE '(' name (',' name)* ')' ';'
    ;

/*
 * Lokalno ograničenje u entitetu (kratki zapis): odnosi se na svaku
 * instancu entiteta, atributi i uloge se pišu bez promenljive.
 *   check najviseTriDnevno:
 *       forall d in isplate.datum : count(isplate[datum = d]) <= 3
 *       with err 'Najviše tri isplate dnevno.';
 */
localConstraint
    : CHECK name? ':' expression withClause? ';'
    ;

// ======================================================================
//  VEZE
// ======================================================================

/*
 * DUGI ZAPIS – blok sa oba preslikavanja:
 *   relationship RadiNa as "radi na" {
 *       Radnik     -> RadnoMesto [1..1] as radnoMesto;
 *       RadnoMesto -> Radnik     [0..*] as radnici;
 *   }
 *
 * KRATKI ZAPIS – jedan red:
 *   relationship RadiNa: Radnik.radnoMesto [1..1] <-> RadnoMesto.radnici [0..*];
 *   (Radnik ima 1..1 radno mesto, radno mesto ima 0..* radnika)
 *
 * identifying relationship – veza slabog entiteta sa vlasnikom.
 * Veze su binarne; veza sa atributima je agregacija.
 */
relationshipDecl
    : IDENTIFYING? RELATIONSHIP name displayName?
      ( '{' mappingDecl mappingDecl '}'
      | ':' shortRelationship ';'
      )
    ;

/*
 * Preslikavanje: domen -> kodomen [donja..gornja] as uloga
 * Ime uloge je ime za navigaciju (r.radnoMesto). Ako se izostavi,
 * podrazumeva se ime kodomena malim slovom.
 * Opciona akcija je kratki zapis strukturnog pravila: kad se nad
 * domenom izvrši operacija, akcija se primenjuje na kodomen.
 */
mappingDecl
    : annotation* name ARROW name cardinality (AS name)? mappingAction* ';'
    ;

shortRelationship
    : relEnd BIARROW relEnd
    ;

relEnd
    : name ('.' name)? cardinality mappingAction*
    ;

cardinality
    : '[' bound DOTDOT bound ']'
    ;

bound
    : INT
    | '*'
    ;

mappingAction
    : ON structuralEvent structuralAction
    ;

// ======================================================================
//  AGREGACIJE
// ======================================================================

/*
 * Veza koja je istovremeno entitet: ima atribute, lokalna ograničenja
 * i može da učestvuje u drugim vezama.
 *
 * DUGI ZAPIS:
 *   aggregation Ucestvuje {
 *       Radnik   -> Projekat [0..*] as projekti;
 *       Projekat -> Radnik   [0..*] as ucesnici;
 *       od : date;
 *   }
 * KRATKI ZAPIS:
 *   aggregation Ucestvuje: Radnik.projekti [0..*] <-> Projekat.ucesnici [0..*] {
 *       od : date;
 *   }
 */
aggregationDecl
    : AGGREGATION name displayName?
      ( '{' mappingDecl mappingDecl entityMember* '}'
      | ':' shortRelationship ( '{' entityMember* '}' | ';' )
      )
    ;

// ======================================================================
//  SPECIJALIZACIJA I KATEGORIJE
// ======================================================================

/*
 * specialization VrsteIsplate of Isplata (disjoint, total) discriminator vrsta {
 *     Plata  = 'PLATA',
 *     Regres = 'REGRES'
 * }
 * Podtipovi se deklarišu kao entiteti bez ključa; nasleđuju ključ nadtipa.
 */
specializationDecl
    : SPECIALIZATION name? OF name '(' specOption (',' specOption)* ')'
      (DISCRIMINATOR name)? '{' subtype (',' subtype)* ','? '}'
    ;

specOption
    : DISJOINT
    | OVERLAPPING
    | TOTAL
    | PARTIAL
    ;

subtype
    : name ('=' literal)?
    ;

// category VlasnikKartice of Osoba, Firma (total) { key vkId : int auto; }
categoryDecl
    : CATEGORY name OF name (',' name)+ '(' (TOTAL | PARTIAL) ')'
      ( '{' entityMember* '}' | ';' )
    ;

// ======================================================================
//  OGRANIČENJA (dugi zapis) – šta mora da važi
// ======================================================================

/*
 * Imenovano ograničenje nad modelom, zapisano u računu entiteta:
 *   constraint StudentBrojIspita:
 *       for each (s in Student)
 *           if s.semestar in ('V', 'VI') then card(s.ispiti) > 10;
 *   constraint StudentPromenaOcene:
 *       for each (i in Ispit) new.ocena >= old.ocena;
 * Ograničenje ne kaže šta se radi kad je narušeno – to radi pravilo.
 */
constraintDecl
    : CONSTRAINT name ':' expression ';'
    ;

// ======================================================================
//  PRAVILA INTEGRITETA (dugi zapis) – šta se radi kad se naruši
// ======================================================================

/*
 * PI := < Događaj (entitet[.atribut], operacija), Ograničenje, Akcija, Poruka >
 *
 * Vrednosno pravilo:
 *   rule ProveraBrojaIspita on Student for insert, update
 *       where not StudentBrojIspita
 *       do reject
 *       with err 'Student petog semestra mora imati više od deset ispita.';
 *
 * Strukturno pravilo (uslov je ime veze; ograničenje je izvedeno iz kardinalnosti):
 *   rule BrisanjeRadnogMesta on RadnoMesto for delete
 *       where RadiNa
 *       do restrict
 *       with err 'Na radnom mestu ima zaposlenih.';
 *
 * Akcije: restrict | cascade | set null | set default | exec <procedura>   (strukturna)
 *         reject | recalculate                                           (vrednosna)
 * Poruke: info | warn | err | nonmsg
 */
ruleDecl
    : RULE name ON path FOR ruleEvent (',' ruleEvent)*
      (WHERE expression)?
      DO ruleAction (ON name)?
      withClause?
      ';'
    ;

ruleEvent
    : INSERT | UPDATE | DELETE | CONNECT | DISCONNECT | RECONNECT
    ;

ruleAction
    : structuralAction
    | REJECT
    | RECALCULATE
    ;

structuralEvent
    : INSERT | UPDATE | DELETE | CONNECT | DISCONNECT | RECONNECT
    ;

structuralAction
    : RESTRICT
    | CASCADE
    | SET NULL
    | SET DEFAULT
    | EXEC name
    ;

withClause
    : WITH messageKind (':'? STRING)?
    ;

messageKind
    : INFO | WARN | ERR | NONMSG
    ;

// ======================================================================
//  Ključne reči koje mogu biti i imena
// ======================================================================

softKeyword
    : DAY | DAYS | MONTH | MONTHS | YEAR | YEARS | HOUR | HOURS | MINUTE | MINUTES
    | DATE | TIME | DATETIME | EACH
    | KEY | PARTIAL | TOTAL | OPTIONAL | REQUIRED | AUTO | COMMENT
    | DISCRIMINATOR | RESTRICT | CASCADE | DISJOINT | OVERLAPPING
    | DERIVED | WEAK | IDENTIFYING | DO | WHERE | WITH | RULE
    | REJECT | RECALCULATE | EXEC | INFO | WARN | ERR | NONMSG
    | CONNECT | DISCONNECT | RECONNECT
    ;

// ======================================================================
//  LEKSER – ključne reči ERLD-a
// ======================================================================

MODEL          : 'model';
DOMAIN         : 'domain';
ENUM           : 'enum';
ENTITY         : 'entity';
WEAK           : 'weak';
AS             : 'as';
KEY            : 'key';
PARTIAL        : 'partial';
UNIQUE         : 'unique';
DERIVED        : 'derived';
OPTIONAL       : 'optional';
REQUIRED       : 'required';
DEFAULT        : 'default';
CHECK          : 'check';
AUTO           : 'auto';
COMMENT        : 'comment';
ON             : 'on';
INSERT         : 'insert';
UPDATE         : 'update';
DELETE         : 'delete';
CONNECT        : 'connect';
DISCONNECT     : 'disconnect';
RECONNECT      : 'reconnect';
RESTRICT       : 'restrict';
CASCADE        : 'cascade';
SET            : 'set';
EXEC           : 'exec';
REJECT         : 'reject';
RECALCULATE    : 'recalculate';
IDENTIFYING    : 'identifying';
RELATIONSHIP   : 'relationship';
AGGREGATION    : 'aggregation';
SPECIALIZATION : 'specialization';
OF             : 'of';
DISJOINT       : 'disjoint';
OVERLAPPING    : 'overlapping';
TOTAL          : 'total';
DISCRIMINATOR  : 'discriminator';
CATEGORY       : 'category';
CONSTRAINT     : 'constraint';
RULE           : 'rule';
WHERE          : 'where';
DO             : 'do';
WITH           : 'with';
INFO           : 'info';
WARN           : 'warn';
ERR            : 'err';
NONMSG         : 'nonmsg';
MULTI          : '[' [ \t]* ']';
