/// A method body around a boolean term, in the statement shapes the
/// control-flow rules rewrite: `if (c) return true; return false;`
/// (CR0001), the nested `if` (CR0005), the assigned flag (CR0001). Each
/// body is a function of the three variables with a meaning `eval` gives,
/// and the rules' rewrite must keep it.
module CSharp.Refactor.PropertyTests.Bodies

open FsCheck
open FsCheck.FSharp
open CSharp.Refactor.PropertyTests.BoolExpr

type Body =
    /// `return e;`
    | Return of BoolExpr
    /// `if (e) return lit; return !lit;`
    | IfReturn of BoolExpr * bool
    /// `if (e) { return lit; } else { return !lit; }`
    | IfElseReturn of BoolExpr * bool
    /// `if (a) { if (b) { return true; } } return false;`
    | NestedIfReturn of a: BoolExpr * b: BoolExpr
    /// `bool r; if (e) r = lit; else r = !lit; return r;`
    | AssignReturn of BoolExpr * bool
    /// `return e ? true : false;`
    | Ternary of BoolExpr

let eval (body: Body) (env: int[]) : bool =
    match body with
    | Return e
    | Ternary e -> BoolExpr.eval env e
    | IfReturn(e, lit)
    | IfElseReturn(e, lit)
    | AssignReturn(e, lit) -> if BoolExpr.eval env e then lit else not lit
    | NestedIfReturn(a, b) -> BoolExpr.eval env a && BoolExpr.eval env b

let private litText (b: bool) = if b then "true" else "false"

/// The body's statements, unindented.
let print (body: Body) : string =
    match body with
    | Return e -> $"return {printBool e};"
    | IfReturn(e, lit) -> $"if ({printBool e}) return {litText lit};\nreturn {litText (not lit)};"
    | IfElseReturn(e, lit) ->
        $"if ({printBool e})\n{{\n    return {litText lit};\n}}\nelse\n{{\n    return {litText (not lit)};\n}}"
    | NestedIfReturn(a, b) ->
        $"if ({printBool a})\n{{\n    if ({printBool b})\n    {{\n        return true;\n    }}\n}}\n\nreturn false;"
    | AssignReturn(e, lit) -> $"bool r;\nif ({printBool e}) r = {litText lit}; else r = {litText (not lit)};\nreturn r;"
    | Ternary e -> $"return {printBool e} ? true : false;"

/// The whole program: one class, one method of the three variables.
let program (body: Body) : string =
    let parameters = variables |> Array.map (fun v -> $"int {v}") |> String.concat ", "

    let statements =
        print body
        |> _.Split('\n')
        |> Array.map (fun l -> if l = "" then "" else "        " + l)
        |> String.concat "\n"

    $"class C\n{{\n    static bool F({parameters})\n    {{\n{statements}\n    }}\n}}\n"

let genBody (size: int) : Gen<Body> =
    let term = genBool size

    let withLit make =
        gen {
            let! e = term
            let! lit = Gen.elements [ true; false ]
            return make (e, lit)
        }

    Gen.frequency
        [
            4, Gen.map Return term
            2, withLit IfReturn
            2, withLit IfElseReturn
            2, withLit AssignReturn
            1, Gen.map Ternary term
            2,
            gen {
                let! a = genBool (size / 2)
                let! b = genBool (size / 2)
                return NestedIfReturn(a, b)
            }
        ]

let shrinkBody (body: Body) : seq<Body> =
    seq {
        match body with
        | Return e -> for e' in shrinkBool e -> Return e'
        | Ternary e ->
            yield Return e
            for e' in shrinkBool e -> Ternary e'
        | IfReturn(e, lit) ->
            yield Return e
            for e' in shrinkBool e -> IfReturn(e', lit)
        | IfElseReturn(e, lit) ->
            yield IfReturn(e, lit)
            for e' in shrinkBool e -> IfElseReturn(e', lit)
        | AssignReturn(e, lit) ->
            yield IfReturn(e, lit)
            for e' in shrinkBool e -> AssignReturn(e', lit)
        | NestedIfReturn(a, b) ->
            yield Return a
            yield Return b
            for a' in shrinkBool a -> NestedIfReturn(a', b)
            for b' in shrinkBool b -> NestedIfReturn(a, b')
    }

let arbitrary: Arbitrary<Body> = Arb.fromGenShrink (Gen.sized genBody, shrinkBody)

let sizeOf (body: Body) : int =
    match body with
    | Return e
    | Ternary e
    | IfReturn(e, _)
    | IfElseReturn(e, _)
    | AssignReturn(e, _) -> 2 + BoolExpr.sizeOf e
    | NestedIfReturn(a, b) -> 2 + BoolExpr.sizeOf a + BoolExpr.sizeOf b
