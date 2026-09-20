/// An interpreter for the C# the rules leave behind: the boolean and
/// integer operators of BoolExpr, `!`, parentheses, literals, the three
/// parameters, a conditional, and the statements of Bodies — blocks,
/// `if`, `return`, a local declared or assigned. Anything else is a loud
/// failure: a rewrite into a shape this cannot read is worth a look, not a
/// silent pass.
module CSharp.Refactor.PropertyTests.Interpreter

open System.Linq
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax

type Value =
    | Int of int
    | Bool of bool

let private asInt (v: Value) =
    match v with
    | Int n -> n
    | Bool b -> failwithf "expected an int, got %b" b

let private asBool (v: Value) =
    match v with
    | Bool b -> b
    | Int n -> failwithf "expected a bool, got %d" n

let rec evalExpr (env: Map<string, Value>) (e: ExpressionSyntax) : Value =
    match e with
    | :? ParenthesizedExpressionSyntax as p -> evalExpr env p.Expression
    | :? LiteralExpressionSyntax as l ->
        match l.Kind() with
        | SyntaxKind.TrueLiteralExpression -> Bool true
        | SyntaxKind.FalseLiteralExpression -> Bool false
        | SyntaxKind.NumericLiteralExpression -> Int(System.Convert.ToInt32 l.Token.Value)
        | kind -> failwithf "not a literal this interpreter reads: %A %s" kind (l.ToString())
    | :? IdentifierNameSyntax as id ->
        match Map.tryFind id.Identifier.Text env with
        | Some v -> v
        | None -> failwithf "unbound name %s" id.Identifier.Text
    | :? PrefixUnaryExpressionSyntax as u ->
        match u.Kind() with
        | SyntaxKind.LogicalNotExpression -> Bool(not (asBool (evalExpr env u.Operand)))
        | SyntaxKind.UnaryMinusExpression -> Int(-(asInt (evalExpr env u.Operand)))
        | kind -> failwithf "not a unary this interpreter reads: %A %s" kind (u.ToString())
    | :? BinaryExpressionSyntax as b ->
        let ints (op: int -> int -> int) =
            Int(op (asInt (evalExpr env b.Left)) (asInt (evalExpr env b.Right)))

        let compare (op: int -> int -> bool) =
            Bool(op (asInt (evalExpr env b.Left)) (asInt (evalExpr env b.Right)))

        match b.Kind() with
        | SyntaxKind.LogicalAndExpression -> Bool(asBool (evalExpr env b.Left) && asBool (evalExpr env b.Right))
        | SyntaxKind.LogicalOrExpression -> Bool(asBool (evalExpr env b.Left) || asBool (evalExpr env b.Right))
        // equality is over ints or over bools: whatever the sides are
        | SyntaxKind.EqualsExpression -> Bool(evalExpr env b.Left = evalExpr env b.Right)
        | SyntaxKind.NotEqualsExpression -> Bool(evalExpr env b.Left <> evalExpr env b.Right)
        | SyntaxKind.LessThanExpression -> compare (<)
        | SyntaxKind.LessThanOrEqualExpression -> compare (<=)
        | SyntaxKind.GreaterThanExpression -> compare (>)
        | SyntaxKind.GreaterThanOrEqualExpression -> compare (>=)
        | SyntaxKind.AddExpression -> ints (+)
        | SyntaxKind.SubtractExpression -> ints (-)
        | SyntaxKind.MultiplyExpression -> ints (*)
        | kind -> failwithf "not a binary this interpreter reads: %A %s" kind (b.ToString())
    | :? ConditionalExpressionSyntax as c ->
        if asBool (evalExpr env c.Condition) then
            evalExpr env c.WhenTrue
        else
            evalExpr env c.WhenFalse
    | other -> failwithf "not an expression this interpreter reads: %s %s" (other.GetType().Name) (other.ToString())

/// Run a statement: the environment after it, and the value it returned
/// if it did.
let rec exec (env: Map<string, Value>) (s: StatementSyntax) : Map<string, Value> * Value option =
    match s with
    | :? BlockSyntax as b ->
        let mutable env = env
        let mutable returned = None

        for statement in b.Statements do
            if returned.IsNone then
                let env', r = exec env statement
                env <- env'
                returned <- r

        env, returned
    | :? ReturnStatementSyntax as r -> env, Some(evalExpr env r.Expression)
    | :? IfStatementSyntax as i ->
        if asBool (evalExpr env i.Condition) then
            exec env i.Statement
        elif isNull i.Else then
            env, None
        else
            exec env i.Else.Statement
    | :? LocalDeclarationStatementSyntax as d ->
        let mutable env = env

        for v in d.Declaration.Variables do
            if not (isNull v.Initializer) then
                env <- Map.add v.Identifier.Text (evalExpr env v.Initializer.Value) env

        env, None
    | :? ExpressionStatementSyntax as es ->
        match es.Expression with
        | :? AssignmentExpressionSyntax as a when a.IsKind SyntaxKind.SimpleAssignmentExpression ->
            match a.Left with
            | :? IdentifierNameSyntax as id -> Map.add id.Identifier.Text (evalExpr env a.Right) env, None
            | other -> failwithf "not an assignment target this interpreter reads: %s" (other.ToString())
        | other -> failwithf "not a statement expression this interpreter reads: %s" (other.ToString())
    | other -> failwithf "not a statement this interpreter reads: %s %s" (other.GetType().Name) (other.ToString())

/// Run the program's method `F` at one environment, through the tree.
let run (tree: SyntaxTree) (env: int[]) : bool =
    let method =
        tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
        |> Seq.tryFind (fun m -> m.Identifier.Text = "F")
        |> Option.defaultWith (fun () -> failwith "no method F in the program")

    let parameters =
        BoolExpr.variables
        |> Array.mapi (fun i name -> name, Int env.[i])
        |> Map.ofArray

    if not (isNull method.ExpressionBody) then
        asBool (evalExpr parameters method.ExpressionBody.Expression)
    else
        match exec parameters method.Body with
        | _, Some v -> asBool v
        | _, None -> failwith "the method fell off its end without returning"
