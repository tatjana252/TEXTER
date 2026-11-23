/*
 * ERLQ – Entity Relationship Language for Queries   (v0.1)
 *
 * Upiti nad modelom zadatim u ERLD-u. Upit se piše nad entitetima, atributima i
 * ulogama (navigacija po vezama), ne nad tabelama: TEXTER ga prevodi u SQL nad
 * fizičkim skladištem iz koraka 3. Izrazi su zajednički sa ERLD-om (ERLCommon):
 * navigacija r.sluzba.naziv, filteri r.isplate[iznos > 100], forall / exists,
 * agregati card, count, sum, avg, min, max, if-then-else.
 *
 * DUGI ZAPIS:
 *   from r in Radnik, i in r.isplate
 *   where r.sluzba.naziv = 'IT' and i.iznos > 100
 *   select r.ime, i.datum, i.iznos
 *   order by i.datum desc
 *   limit 10;
 *
 *   from r in Radnik
 *   group by r.sluzba.naziv
 *   select r.sluzba.naziv as sluzba, count(r), avg(r.isplate.iznos);   -- agregat po grupi
 *
 * KRATKI ZAPIS (putanja) – razvija se u dugi:
 *   Radnik[sluzba.naziv = 'IT'].ime        ==  from x in Radnik where x.sluzba.naziv = 'IT' select x.ime
 *   Projekat[budzet > 1000].ucesnici       ==  from p in Projekat, u in p.ucesnici where p.budzet > 1000 select u
 *   Radnik                                 ==  from x in Radnik select x
 * U filteru [...] imena bez promenljive su atributi i uloge elementa koji se filtrira.
 *
 * v0.1: samo upiti (čitanje). Naredbe za izmenu (insert, update, delete,
 * connect, disconnect) dolaze kasnije.
 */
grammar ERLQ;

options { caseInsensitive = true; }

import ERLCommon;

erlqFile
    : query ';'? EOF
    ;

query
    : longQuery         # longForm
    | expression        # shortForm
    ;

longQuery
    : FROM rangeDecl (',' rangeDecl)*
      (WHERE expression)?
      (GROUP BY expressionList (HAVING expression)?)?
      SELECT DISTINCT? selectItem (',' selectItem)*
      (ORDER BY orderItem (',' orderItem)*)?
      (LIMIT INT)?
    ;

// r in Radnik  |  i in r.isplate  |  p in Projekat[budzet > 1000]
rangeDecl
    : name IN expression
    ;

selectItem
    : expression (AS name)?
    ;

orderItem
    : expression (ASC | DESC)?
    ;

// Ključne reči koje mogu biti i imena (entitet Order, atribut desc, ...).
softKeyword
    : DAY | DAYS | MONTH | MONTHS | YEAR | YEARS | HOUR | HOURS | MINUTE | MINUTES
    | DATE | TIME | DATETIME | EACH
    | GROUP | BY | HAVING | ORDER | ASC | DESC | LIMIT
    ;

FROM     : 'from';
WHERE    : 'where';
GROUP    : 'group';
BY       : 'by';
HAVING   : 'having';
SELECT   : 'select';
DISTINCT : 'distinct';
ORDER    : 'order';
ASC      : 'asc';
DESC     : 'desc';
LIMIT    : 'limit';
AS       : 'as';
