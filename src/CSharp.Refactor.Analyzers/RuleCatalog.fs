/// Every rule, by code: its category, default, and whether it is a priority
/// (warning-severity) rule. The tests keep Rules.md in step with this module.
///
/// The four categories, as on the F# side:
///
///   correctness — a defect: the code does something other than what it
///                 looks like it does.
///   performance — measurably wasteful, but correct.
///   idiom       — the same behaviour written the way clear C# writes it.
///   cosmetic    — punctuation and spelling of code.
module CSharp.Refactor.RuleCatalog

open System

[<RequireQualifiedAccess>]
type Category =
    | Correctness
    | Performance
    | Idiom
    | Cosmetic

let name (category: Category) =
    match category with
    | Category.Correctness -> "correctness"
    | Category.Performance -> "performance"
    | Category.Idiom -> "idiom"
    | Category.Cosmetic -> "cosmetic"

let all =
    [
        Category.Correctness
        Category.Performance
        Category.Idiom
        Category.Cosmetic
    ]

let parse (text: string) =
    let wanted = text.Trim()

    all
    |> List.tryFind (fun c -> String.Equals(name c, wanted, StringComparison.OrdinalIgnoreCase))

/// The categories a stranger's repository is worth a pull request over.
let substantive = set [ Category.Correctness; Category.Performance ]

type Rule =
    {
        Code: string
        Category: Category
        Title: string
        /// Enabled by default. Off means `.editorconfig` or `--codes` wakes it.
        Default: bool
        /// A likely defect too costly to hold back: warning severity, printed
        /// without `--notes`.
        Priority: bool
        /// Microsoft rule ids this rule shadows: when any of them is on in the
        /// file's effective config, this rule stands down for its shapes.
        YieldsTo: string list
    }

let private rule code category title =
    {
        Code = code
        Category = category
        Title = title
        Default = true
        Priority = false
        YieldsTo = []
    }

let private off (r: Rule) = { r with Default = false }
let private priority (r: Rule) = { r with Priority = true }
let private yields (ids: string list) (r: Rule) = { r with YieldsTo = ids }

/// The v1 catalog. Codes are assigned in order of introduction and never
/// reused. Only the rules an analyzer module implements are listed; DESIGN.md
/// carries the full planned set.
let rules: Rule list =
    [
        rule "CR0001" Category.Idiom "if/else spelling out true and false returns the condition"
        rule "CR0002" Category.Idiom "an if chain comparing one value against constants is a switch"
        rule "CR0003" Category.Idiom "a chain of type tests with casts is a switch over type patterns"
        rule "CR0004" Category.Idiom "HasValue then .Value is a pattern match"
        rule "CR0005" Category.Idiom "nested ifs with the same else merge into one condition"
        rule "CR0006" Category.Idiom "a long happy path under a short exiting else is a guard clause"
        |> off
        rule "CR0007" Category.Idiom "&& true and || false contribute nothing"
        rule "CR0008" Category.Idiom "a || a and a && a are a"
        rule "CR0009" Category.Idiom "adjacent switch arms with one body fold"
        rule "CR0010" Category.Idiom "a guard that only tests the binder against a constant is the constant pattern"
        rule "CR0011" Category.Idiom "term-rewriting hints: negated comparisons, bool literals, CompareTo, LINQ shapes"
        rule "CR0012" Category.Correctness "an arm that says it is unfinished should throw"
        rule "CR0013" Category.Correctness "a default arm standing in for one or two enum members"
        rule "CR0014" Category.Correctness "an exiting enum switch with no default and members unhandled"
        rule "CR0015" Category.Idiom "an index that only reads xs[i] is a foreach"
        rule "CR0016" Category.Idiom "a flag-steered loop keeps running after the decision"
        |> off
        rule "CR0017" Category.Correctness "a closure capturing the for variable outlives the iteration"
        rule "CR0020" Category.Performance "an eager copy that a lazy stage or a consumer follows moves or goes"
        rule "CR0021" Category.Idiom "a running sum, count or string is the aggregate"
        rule "CR0022" Category.Idiom "a flag set by a loop is Any or All" |> off
        rule "CR0023" Category.Performance "a startup-built list probed by Contains per element is a set"
        rule "CR0024" Category.Performance "adding every element one by one is AddRange"
        rule "CR0025" Category.Performance "growing by one inside a loop copies everything each time"
        rule "CR0026" Category.Performance "Count/ElementAt/Last on a bare IEnumerable inside a loop walks it again"
        |> yields [ "CA1826"; "CA1829" ]
        rule "CR0027" Category.Correctness "a deferred query as a statement runs nothing"
        rule "CR0028" Category.Idiom "a list filled by one loop and then only read is the pipeline"
        |> off
        rule "CR0029" Category.Idiom "two Selects in a row are one, an identity Select is none"
        rule "CR0030" Category.Correctness "an IEnumerable parameter enumerated twice on one path"
        |> yields [ "CA1851" ]
        rule "CR0031" Category.Performance "new Random() per call is Random.Shared"
        rule "CR0032" Category.Performance "foreach over Keys with a lookup per key enumerates the pairs"
        rule "CR0033" Category.Performance "Append of a concatenation appends the pieces"
        rule "CR0034" Category.Correctness "a query per element of an outer loop is the N+1"
        |> priority
        rule "CR0035" Category.Performance "an iterator re-yielding its own recursion nests an enumerator per level"
        rule "CR0040" Category.Correctness "a blocking drain inside an async body is an await; outside, the boundary"
        |> yields [ "CA1849" ]
        rule "CR0041" Category.Correctness "a sync method draining a task whose every caller is async becomes async"
        rule "CR0042" Category.Correctness "a sync call with an Async twin inside an async body awaits the twin"
        |> yields [ "CA1849" ]
        rule "CR0043" Category.Correctness "async void that is not an event handler returns a Task"
        |> yields [ "VSTHRD100" ]
        rule "CR0044" Category.Correctness "a task started and dropped: its failure is silently lost"
        rule "CR0045" Category.Performance "a test blocking on a task returns a Task and awaits"
        rule "CR0046" Category.Performance "a method that only awaits and returns an async call returns the task"
        rule "CR0047" Category.Correctness "a lock on this, a string, a Type or a boxed value"
        |> priority
        |> yields [ "CA2002" ]
        rule "CR0048" Category.Correctness "Monitor.Enter with try/finally Exit is the lock statement"
        rule "CR0049" Category.Correctness "a check-then-store on a ConcurrentDictionary races: GetOrAdd"
        rule "CR0050" Category.Correctness "GetOrAdd with a Task or Lazy value caches a failure for good"
        rule "CR0051" Category.Correctness "a using disposes before the returned task completes"
        rule "CR0052" Category.Correctness "a handler capturing this on a process-wide publisher, never removed"
        rule "CR0053" Category.Correctness "an async lambda handed to a void delegate is async void in disguise"
        |> yields [ "VSTHRD101" ]
        rule "CR0054" Category.Performance "WhenAll or WaitAll of one task combines nothing"
        |> yields [ "CA1842"; "CA1843" ]
        rule "CR0055" Category.Correctness "CancellationToken.None while a token is in scope"
        |> yields [ "CA2016" ]
        rule "CR0060" Category.Correctness "a disposable local never disposed becomes a using declaration"
        |> yields [ "CA2000" ]
        rule "CR0061" Category.Correctness "a type constructing a disposable field without IDisposable"
        |> priority
        |> yields [ "CA1001" ]
        rule "CR0062" Category.Correctness "a Dispose that never releases an owned disposable field"
        |> priority
        |> yields [ "CA2213" ]
        rule "CR0063" Category.Correctness "a public Dispose on a type not implementing IDisposable"
        rule "CR0064" Category.Correctness "a catch-all that swallows"
        |> yields [ "CA1031" ]
        rule "CR0065" Category.Idiom "a guard that rethrows is an exception filter"
        rule "CR0066" Category.Correctness "a throw inside finally"
        |> priority
        |> yields [ "CA2219" ]
        rule
            "CR0067"
            Category.Correctness
            "a throw inside Equals, GetHashCode, ToString, Dispose or a static constructor"
        |> yields [ "CA1065" ]
        rule "CR0068" Category.Correctness "throwing the runtime's own exception types"
        |> yields [ "CA2201" ]
        rule "CR0069" Category.Idiom "a constant exception message gains the caller's arguments"
        rule "CR0070" Category.Correctness "an exception whose informative member goes unread"
        rule "CR0080" Category.Idiom "an immutable class is a record"
        rule "CR0081" Category.Performance "a record of small value fields is a readonly record struct"
        rule "CR0082" Category.Performance "a reference Tuple is a value tuple"
        rule "CR0083" Category.Idiom "a setter only used while constructing is init"
        rule "CR0084" Category.Correctness "a public mutable static written from several sites"
        |> yields [ "CA2211" ]
        rule "CR0085" Category.Correctness "a type test by name or by exact Type"
        rule "CR0086" Category.Correctness "a virtual member called while constructing"
        |> priority
        |> yields [ "CA2214" ]
        rule "CR0087" Category.Performance "an enum compared by its text"
        rule "CR0089" Category.Idiom "a private type's DateTime clock slot is a DateTimeOffset"
        |> off
        rule "CR0090" Category.Correctness "new Guid() is Guid.Empty"
        rule "CR0100" Category.Idiom "a concatenation of literals and values is an interpolated string"
        rule "CR0101" Category.Idiom "string.Format with a literal template is an interpolated string"
        rule "CR0102" Category.Performance "ToString() inside a hole or under string.Join is a copy"
        rule "CR0103" Category.Cosmetic "hole-free interpolated string"
        rule "CR0104" Category.Idiom "a spelled-out null-or-empty test is string.IsNullOrEmpty"
        rule "CR0105" Category.Correctness "Parse without a culture"
        |> yields [ "CA1305" ]
        rule "CR0106" Category.Correctness "DateTime.Now is a local clock"
        rule "CR0107" Category.Correctness "a regex pattern the engine rejects"
        |> priority
        rule "CR0108" Category.Performance "a regex over plain text is a string operation"
        rule "CR0109" Category.Performance "a regex built on every call is hoisted"
        |> yields [ "SYSLIB1045" ]
        rule "CR0110" Category.Performance "an HttpClient per call, a factory per iteration"
        |> yields [ "CA1869"; "CA1870" ]
        rule "CR0111" Category.Idiom "a path joined by hand"
        rule "CR0112" Category.Correctness "an invisible or direction-changing character in source"
        rule "CR0113" Category.Correctness "arithmetic near the type's ceiling"
        rule "CR0114" Category.Correctness "a log template that does not fit its arguments"
        |> yields [ "CA2017"; "CA2254" ]
        rule "CR0115" Category.Correctness "a caught exception left off the log line"
        rule "CR0120" Category.Correctness "SQL text built from values"
        |> priority
        |> yields [ "CA2100"; "CA3001" ]
        rule "CR0121" Category.Correctness "a SQL command with no parameter at all"
        rule "CR0122" Category.Correctness "a command line built from a value"
        |> priority
        rule "CR0123" Category.Correctness "a literal in a provider's key format"
        |> priority
        rule "CR0124" Category.Correctness "a credential in a constant connection string"
        rule "CR0125" Category.Correctness "a broken hash, cipher, certificate check or protocol"
        |> priority
        |> yields [ "CA5350"; "CA5351"; "CA5359"; "CA5364"; "CA5386"; "CA5397" ]
        rule "CR0126" Category.Idiom "an obsolete crypto constructor is the factory"
        |> yields [ "SYSLIB0021" ]
        rule "CR0140" Category.Cosmetic "Attribute suffix is redundant"
        rule "CR0141" Category.Cosmetic "empty attribute argument list"
        rule "CR0142" Category.Cosmetic "attribute lists on one line merge into one bracket pair"
        |> off
        rule "CR0143" Category.Cosmetic "@ on a non-keyword identifier"
        rule "CR0144" Category.Cosmetic "else holding only an if is else if"
        rule "CR0145" Category.Idiom "a namespace spelled out at every use is a using"
        rule "CR0146" Category.Idiom "a trailing note on a public declaration is its summary"
        rule "CR0147" Category.Idiom "a chain of length tests is a switch over list patterns"
        rule "CR0148" Category.Performance "the bytes of a constant are a u8 literal"
        rule "CR0149" Category.Idiom "a property every construction sets is required"
        |> off
        rule "CR0150" Category.Performance "a static dictionary or set filled once is frozen"
        rule "CR0151" Category.Performance "a params array the body only reads is a params ReadOnlySpan"
        rule "CR0152" Category.Performance "a gate object used only by lock is a Lock"
        rule "CR0153" Category.Idiom "a backing field referenced only by its property is the field keyword"
        rule "CR0154" Category.Idiom "a null-guarded assignment is a null-conditional assignment"
        |> yields [ "IDE0031" ]
        rule "CR0155" Category.Idiom "a static class of extension methods is an extension block"
        |> off
        rule "CR0156" Category.Idiom "a memberless abstract record with sealed record cases is a union"
        rule "CR0157" Category.Idiom "an unreachable discard arm throws UnreachableException"
        rule "CR0158" Category.Idiom "a discard arm over a union hides a missing case"
        rule "CR0160" Category.Correctness "a closure created in a loop reads a variable the loop changes"
        |> priority
        rule "CR0161" Category.Correctness "a mutating call on a struct copy changes the copy"
        |> priority
        rule "CR0162" Category.Correctness "a timer nothing references is collected"
        |> priority
        rule "CR0163" Category.Correctness "an acquired semaphore or lock is released in a finally"
        |> priority
        rule "CR0164" Category.Correctness "a check-then-assign static cache is LazyInitializer.EnsureInitialized"
        rule "CR0165" Category.Correctness "a wrapping throw keeps the caught exception as the inner"
        |> priority
        |> yields [ "CA2200" ]
        rule "CR0166" Category.Performance "a parse driven by its exception is a TryParse"
        rule "CR0167" Category.Correctness "floating-point equality compares representations"
        rule "CR0168" Category.Correctness "an integer division lands in a floating-point target"
        rule "CR0169" Category.Correctness "a local time and a UTC time are compared"
        |> priority
        rule "CR0170" Category.Correctness "a loop in a method taking a CancellationToken observes it"
        rule "CR0171" Category.Correctness "a collection mutated under its own foreach"
        |> priority
        rule "CR0172" Category.Idiom "a local initialised with a constant and never written is const"
        rule "CR0173" Category.Idiom "a return or assignment every branch performs is one of a conditional"
        |> yields [ "IDE0046"; "IDE0045" ]
        rule "CR0174" Category.Performance "a Substring handed to a span-reading consumer is AsSpan"
        |> yields [ "CA1846" ]
        rule "CR0175" Category.Performance "a prefix or suffix cut out to be compared is StartsWith or EndsWith"
        rule "CR0176" Category.Performance "a ToCharArray a foreach reads once is the string itself"
        rule
            "CR0177"
            Category.Performance
            "a local computed inside a loop from nothing the loop changes is computed once, above it"
        rule "CR0178" Category.Performance "a query copied before Where or Select runs them in the query, copying after"
    ]

let private byCode = rules |> List.map (fun r -> r.Code, r) |> Map.ofList

let tryFind (code: string) = byCode |> Map.tryFind code

let categoryOf (code: string) =
    byCode
    |> Map.tryFind code
    |> Option.map (fun r -> r.Category)
    |> Option.defaultValue Category.Idiom

let isPriority (code: string) =
    byCode
    |> Map.tryFind code
    |> Option.map (fun r -> r.Priority)
    |> Option.defaultValue false

let isDefaultOn (code: string) =
    byCode
    |> Map.tryFind code
    |> Option.map (fun r -> r.Default)
    |> Option.defaultValue true

let known = byCode |> Map.toSeq |> Seq.map fst |> Set.ofSeq

/// The catalog's one-line description of a rule.
let describe (code: string) =
    byCode
    |> Map.tryFind code
    |> Option.map (fun r -> r.Title)
    |> Option.defaultValue code

/// Every code of the wanted categories.
let codesIn (wanted: Set<Category>) =
    rules
    |> List.filter (fun r -> wanted.Contains r.Category)
    |> List.map (fun r -> r.Code)
    |> Set.ofList

/// (code, category) for every rule, in code order.
let allRules = rules |> List.map (fun r -> r.Code, r.Category)

/// The GitHub slug of a rule's `### CRnnnn — category` heading in Rules.md:
/// lowercased, the em dash between two spaces leaving a double hyphen.
let anchor (code: string) =
    code.ToLowerInvariant() + "--" + name (categoryOf code)

/// The rule's section in Rules.md.
let helpUri (code: string) =
    "https://github.com/Thorium/csharp-refactor/blob/main/Rules.md#" + anchor code
