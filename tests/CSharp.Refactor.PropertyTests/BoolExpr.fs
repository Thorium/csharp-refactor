/// A tiny language of boolean expressions over three integer variables:
/// the oracle for the semantic properties. A term is generated here,
/// printed as C#, run through the rules, and the fixed text is parsed back
/// and interpreted (Interpreter.fs); the interpreter's answer must equal
/// `eval` of the original term under every environment in `envs`.
///
/// The language is deliberately the fragment BooleanSimplify (CR0007,
/// CR0008) and the hint engine's negation, De Morgan and `== true` rules
/// rewrite, so that a generated term has a fair chance of firing several
/// rules in one method.
module CSharp.Refactor.PropertyTests.BoolExpr

open FsCheck
open FsCheck.FSharp

type IntExpr =
    /// `x0`, `x1`, `x2`
    | IVar of int
    /// 0 .. 9: never negative, so the printer never has to spell `- -1`
    | ILit of int
    | Add of a: IntExpr * b: IntExpr
    | Sub of a: IntExpr * b: IntExpr
    | Mul of a: IntExpr * b: IntExpr
    /// Parentheses the generator put there on purpose, needed or not.
    | IParen of IntExpr

type CmpOp =
    | Eq
    | Ne
    | Lt
    | Le
    | Gt
    | Ge

type BoolExpr =
    | Lit of bool
    | Cmp of CmpOp * IntExpr * IntExpr
    | And of a: BoolExpr * b: BoolExpr
    | Or of a: BoolExpr * b: BoolExpr
    | Not of BoolExpr
    | Paren of BoolExpr
    /// `e == true`, `e != false`: the IDE0100 shapes.
    | EqLit of BoolExpr * bool
    | NeLit of BoolExpr * bool

/// The variable names the printed method binds, in `IVar` order.
let variables = [| "x0"; "x1"; "x2" |]

/// Every assignment of a few interesting values to the three variables:
/// a fixed grid rather than a random environment, so one generated term
/// is checked at 64 points and a counterexample replays exactly.
let envs: int[] list =
    let values = [ -2; 0; 1; 3 ]

    [
        for a in values do
            for b in values do
                for c in values -> [| a; b; c |]
    ]

// ---- semantics ----

let rec evalInt (env: int[]) (e: IntExpr) : int =
    match e with
    | IVar i -> env.[i]
    | ILit n -> n
    // unchecked, like the C# operators the printer emits
    | Add(a, b) -> evalInt env a + evalInt env b
    | Sub(a, b) -> evalInt env a - evalInt env b
    | Mul(a, b) -> evalInt env a * evalInt env b
    | IParen a -> evalInt env a

let compareWith (op: CmpOp) (a: int) (b: int) =
    match op with
    | Eq -> a = b
    | Ne -> a <> b
    | Lt -> a < b
    | Le -> a <= b
    | Gt -> a > b
    | Ge -> a >= b

let rec eval (env: int[]) (e: BoolExpr) : bool =
    match e with
    | Lit b -> b
    | Cmp(op, a, b) -> compareWith op (evalInt env a) (evalInt env b)
    | And(a, b) -> eval env a && eval env b
    | Or(a, b) -> eval env a || eval env b
    | Not a -> not (eval env a)
    | Paren a -> eval env a
    | EqLit(a, b) -> eval env a = b
    | NeLit(a, b) -> eval env a <> b

// ---- printing as C# ----

/// Binding strength, C#'s: `||` < `&&` < `== !=` < relational < `+ -` < `*`.
let private intPrec =
    function
    | IVar _
    | ILit _
    | IParen _ -> 9
    | Mul _ -> 6
    | Add _
    | Sub _ -> 5

let private boolPrec =
    function
    | Lit _
    | Paren _
    | Not _ -> 9
    | Cmp((Eq | Ne), _, _)
    | EqLit _
    | NeLit _ -> 3
    | Cmp _ -> 4
    | And _ -> 2
    | Or _ -> 1

let private cmpText =
    function
    | Eq -> "=="
    | Ne -> "!="
    | Lt -> "<"
    | Le -> "<="
    | Gt -> ">"
    | Ge -> ">="

let private litText (b: bool) = if b then "true" else "false"

/// A child is wrapped when it binds looser than its parent, or equally
/// tight on the right of a left-associative operator (`a - (b - c)`).
let private wrap (needed: bool) (text: string) = if needed then $"({text})" else text

let rec printInt (e: IntExpr) : string =
    let child (parent: int) (right: bool) (c: IntExpr) =
        let p = intPrec c
        wrap (p < parent || (right && p = parent)) (printInt c)

    match e with
    | IVar i -> variables.[i]
    | ILit n -> string n
    | Add(a, b) -> $"{child 5 false a} + {child 5 true b}"
    | Sub(a, b) -> $"{child 5 false a} - {child 5 true b}"
    | Mul(a, b) -> $"{child 6 false a} * {child 6 true b}"
    | IParen a -> $"({printInt a})"

let rec printBool (e: BoolExpr) : string =
    let child (parent: int) (right: bool) (c: BoolExpr) =
        let p = boolPrec c
        wrap (p < parent || (right && p = parent)) (printBool c)

    match e with
    | Lit b -> litText b
    | Cmp(op, a, b) -> $"{printInt a} {cmpText op} {printInt b}"
    | And(a, b) -> $"{child 2 false a} && {child 2 true b}"
    | Or(a, b) -> $"{child 1 false a} || {child 1 true b}"
    // `!` binds tightest: anything but an atom goes in parentheses
    | Not a -> $"!{child 9 false a}"
    | Paren a -> $"({printBool a})"
    // `a == b == true` would compare a bool with a bool: the operand of
    // an equality is bracketed unless it binds tighter than one
    | EqLit(a, b) -> $"{child 4 false a} == {litText b}"
    | NeLit(a, b) -> $"{child 4 false a} != {litText b}"

// ---- generation ----

let private genVar = Gen.choose (0, 2) |> Gen.map IVar
let private genILit = Gen.choose (0, 9) |> Gen.map ILit

let rec genInt (size: int) : Gen<IntExpr> =
    if size <= 0 then
        Gen.frequency [ 3, genVar; 1, genILit ]
    else
        let sub = genInt (size / 2)

        let binary make =
            gen {
                let! a = sub
                let! b = sub
                return make (a, b)
            }

        Gen.frequency
            [
                3, genVar
                1, genILit
                2, binary Add
                2, binary Sub
                1, binary Mul
                1, Gen.map IParen sub
            ]

let private genLit = Gen.map Lit (Gen.elements [ true; false ])

let private genCmp (size: int) =
    gen {
        let! op = Gen.elements [ Eq; Ne; Lt; Le; Gt; Ge ]
        let! a = genInt (size / 3)
        let! b = genInt (size / 3)
        return Cmp(op, a, b)
    }

let rec genBool (size: int) : Gen<BoolExpr> =
    if size <= 0 then
        Gen.frequency [ 1, genLit; 4, genCmp 0 ]
    else
        let sub = genBool (size / 2)

        let binary make =
            gen {
                let! a = sub
                let! b = sub
                return make (a, b)
            }

        let withLit make =
            gen {
                let! a = sub
                let! b = Gen.elements [ true; false ]
                return make (a, b)
            }

        Gen.frequency
            [
                1, genLit
                4, genCmp size
                3, binary And
                3, binary Or
                2, Gen.map Not sub
                2, Gen.map Paren sub
                1, withLit EqLit
                1, withLit NeLit
                // the shapes the rules are FOR, at a rate a uniform tree
                // would rarely reach: an identity element and a duplicate
                1, Gen.map (fun a -> And(a, Lit true)) sub
                1, Gen.map (fun a -> Or(Lit false, a)) sub
                1, Gen.map (fun a -> And(a, a)) sub
                1, Gen.map (fun a -> Or(a, a)) sub
                // De Morgan, a negated comparison, a double negation
                1, binary (fun (a, b) -> And(Not a, Not b))
                1, Gen.map (Paren >> Not) (genCmp size)
                1, Gen.map (Not >> Paren >> Not) sub
            ]

// ---- shrinking: a smaller term is a child, or a leaf ----

let rec shrinkInt (e: IntExpr) : seq<IntExpr> =
    seq {
        match e with
        | IVar _ -> ()
        | ILit n when n > 0 -> yield ILit 0
        | ILit _ -> ()
        | Add(a, b)
        | Sub(a, b)
        | Mul(a, b) ->
            yield a
            yield b

            let rebuild x y =
                match e with
                | Add _ -> Add(x, y)
                | Sub _ -> Sub(x, y)
                | _ -> Mul(x, y)

            for a' in shrinkInt a -> rebuild a' b
            for b' in shrinkInt b -> rebuild a b'
        | IParen a ->
            yield a
            for a' in shrinkInt a -> IParen a'
    }

let rec shrinkBool (e: BoolExpr) : seq<BoolExpr> =
    seq {
        match e with
        | Lit true -> yield Lit false
        | Lit false -> ()
        | Cmp(op, a, b) ->
            yield Lit true
            yield Lit false
            for a' in shrinkInt a -> Cmp(op, a', b)
            for b' in shrinkInt b -> Cmp(op, a, b')
        | And(a, b)
        | Or(a, b) ->
            yield a
            yield b

            let rebuild x y =
                match e with
                | And _ -> And(x, y)
                | _ -> Or(x, y)

            for a' in shrinkBool a -> rebuild a' b
            for b' in shrinkBool b -> rebuild a b'
        | Not a ->
            yield a
            for a' in shrinkBool a -> Not a'
        | Paren a ->
            yield a
            for a' in shrinkBool a -> Paren a'
        | EqLit(a, b) ->
            yield a
            for a' in shrinkBool a -> EqLit(a', b)
        | NeLit(a, b) ->
            yield a
            for a' in shrinkBool a -> NeLit(a', b)
    }

let arbitrary: Arbitrary<BoolExpr> =
    Arb.fromGenShrink (Gen.sized genBool, shrinkBool)

let rec sizeOfInt (e: IntExpr) : int =
    match e with
    | IVar _
    | ILit _ -> 1
    | Add(a, b)
    | Sub(a, b)
    | Mul(a, b) -> 1 + sizeOfInt a + sizeOfInt b
    | IParen a -> 1 + sizeOfInt a

/// How many operators a term holds: the bound on how many rewrite rounds
/// a fixed point can be away.
let rec sizeOf (e: BoolExpr) : int =
    match e with
    | Lit _ -> 1
    | Cmp(_, a, b) -> 1 + sizeOfInt a + sizeOfInt b
    | And(a, b)
    | Or(a, b) -> 1 + sizeOf a + sizeOf b
    | Not a
    | Paren a
    | EqLit(a, _)
    | NeLit(a, _) -> 1 + sizeOf a
