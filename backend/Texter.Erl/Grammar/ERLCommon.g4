/*
 * ERLCommon – zajednički deo ERL jezika (ERLD, ERLQ): imena, literali, izrazi
 * računa entiteta, navigacija po DER-u i leksika.
 */
grammar ERLCommon;

// ======================================================================
//  IMENA, PUTANJE, LITERALI
// ======================================================================

name
    : ID
    | QUOTED_ID
    | softKeyword
    ;

// Radnik | Radnik.ime | rk.radnik.radnoMesto
path
    : name ('.' name)*
    ;

literal
    : INT
    | DECIMAL
    | STRING
    | TRUE
    | FALSE
    | NULL
    ;

expressionList
    : expression (',' expression)*
    ;

compOp
    : '=' | '<>' | '!=' | '<' | '<=' | '>' | '>='
    ;

durationUnit
    : DAY | DAYS | MONTH | MONTHS | YEAR | YEARS | HOUR | HOURS | MINUTE | MINUTES
    ;

// ======================================================================
//  IZRAZI (od najvišeg ka najnižem prioritetu)
// ======================================================================

expression
    : literal                                                       # literalExpr
    | INT durationUnit                                              # durationExpr
    | name '(' expressionList? ')'                                  # callExpr
    | name                                                          # nameExpr
    | '(' expressionList ')'                                        # parenExpr
    | expression '.' name                                           # memberExpr
    | expression '[' expression ']'                                 # filterExpr
    | '-' expression                                                # negateExpr
    | expression op=('*' | '/' | '%') expression                    # mulExpr
    | expression op=('+' | '-') expression                          # addExpr
    | expression compOp expression                                  # compareExpr
    | expression IS NOT? NULL                                       # isNullExpr
    | expression NOT? IN expression                                 # inExpr
    | expression NOT? BETWEEN expression AND expression             # betweenExpr
    | expression NOT? LIKE expression                               # likeExpr
    | NOT expression                                                # notExpr
    | expression AND expression                                     # andExpr
    | expression OR expression                                      # orExpr
    | expression IMPLIES expression                                 # impliesExpr
    | IF expression THEN expression (ELSE expression)?              # ifExpr
    | (FORALL | EXISTS) name IN expression ':' expression           # quantifiedExpr
    | FOR EACH '(' name IN expression ')' expression                # forEachExpr
    ;

// ======================================================================
//  LEKSER
// ======================================================================

AND      : 'and';
OR       : 'or';
NOT      : 'not';
IMPLIES  : 'implies';
IN       : 'in';
IS       : 'is';
NULL     : 'null';
TRUE     : 'true';
FALSE    : 'false';
BETWEEN  : 'between';
LIKE     : 'like';
IF       : 'if';
THEN     : 'then';
ELSE     : 'else';
FORALL   : 'forall';
EXISTS   : 'exists';
FOR      : 'for';
EACH     : 'each';

DAY      : 'day';
DAYS     : 'days';
MONTH    : 'month';
MONTHS   : 'months';
YEAR     : 'year';
YEARS    : 'years';
HOUR     : 'hour';
HOURS    : 'hours';
MINUTE   : 'minute';
MINUTES  : 'minutes';
DATE     : 'date';
TIME     : 'time';
DATETIME : 'datetime';

ARROW    : '->';
BIARROW  : '<->';
DOTDOT   : '..';

DECIMAL  : [0-9]+ '.' [0-9]+;
INT      : [0-9]+;
STRING   : '\'' ( '\'\'' | ~'\'' )* '\'';
QUOTED_ID: '"' ~'"'* '"';

// Slova srpske latinice; # je dozvoljen posle prvog znaka (B#, P#).
ID       : [a-zčćžšđ_] [a-zčćžšđ_0-9#]*;

LINE_COMMENT  : '//' ~[\r\n]* -> skip;
BLOCK_COMMENT : '/*' .*? '*/' -> skip;
WS            : [ \t\r\n]+ -> skip;
