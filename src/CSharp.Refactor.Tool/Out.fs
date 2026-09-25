/// Console output with a purpose per colour: progress recedes, skips read
/// as deliberate, failures come forward. Colour is never the only carrier;
/// every line says in words what it is. Ported from fsharp-refactor.
module CSharp.Refactor.Tool.Out

open System

let mutable private forcePlain = false

/// --no-color, for a terminal that claims colour it cannot show.
let goPlain () = forcePlain <- true

let private allowed (redirected: bool) =
    not forcePlain
    && not redirected
    && String.IsNullOrEmpty(Environment.GetEnvironmentVariable "NO_COLOR")
    && Environment.GetEnvironmentVariable "TERM" <> "dumb"

/// Console colour is PROCESS-GLOBAL, and the sweep's heartbeat prints
/// from inside Async.Parallel. Two threads interleaving read-previous /
/// set / restore leave the terminal stuck in whichever colour lost the
/// race — surviving the run and colouring the user's next prompt. One
/// gate makes a line atomic; output is serialized for readability
/// anyway, so it costs nothing worth measuring.
let private gate = obj ()

/// Emit exactly once, coloured if we can. Reading or setting the colour
/// throws on a console that is not one; that costs the colour, never the
/// message. The previous colour is always put back, so a piece of a line
/// never leaves its colour hanging over whatever prints next.
let private coloured (stream: IO.TextWriter) (redirected: bool) (color: ConsoleColor) (emit: IO.TextWriter -> unit) =
    lock gate (fun () ->
        let restore =
            if allowed redirected then
                try
                    let previous = Console.ForegroundColor
                    Console.ForegroundColor <- color
                    Some previous
                with _ -> // not a colour-capable console; fsharpanalyzer: ignore-line FR0055
                    None
            else
                None

        try
            emit stream
        finally
            match restore with
            | Some previous ->
                try
                    Console.ForegroundColor <- previous
                with _ -> // a redirected console has no colour to restore; fsharpanalyzer: ignore-line FR0055
                    ()
            | None -> ())

let private line (stream: IO.TextWriter) redirected color (text: string) =
    coloured stream redirected color (fun s -> s.WriteLine text)

let private part (stream: IO.TextWriter) redirected color (text: string) =
    coloured stream redirected color (fun s -> s.Write text)

/// Progress and timing: true, and not what anyone is looking for.
let dim text =
    line Console.Out Console.IsOutputRedirected ConsoleColor.DarkGray text

/// A piece of a line that several writes assemble — a progress prefix
/// printed before the work, completed by the elapsed time after it.
let dimPart text =
    part Console.Out Console.IsOutputRedirected ConsoleColor.DarkGray text

/// The same on stderr, where the sweep's heartbeat lives so that piped
/// stdout stays machine-readable.
let dimPartErr text =
    part Console.Error Console.IsErrorRedirected ConsoleColor.DarkGray text

/// Work that landed.
let good text =
    line Console.Out Console.IsOutputRedirected ConsoleColor.Green text

/// A standing invitation about the RUN itself rather than about the
/// code — brighter than the default foreground, so it reads as
/// addressed to the operator without borrowing a finding's colour.
let white text =
    line Console.Out Console.IsOutputRedirected ConsoleColor.White text

/// Deliberately not done — a skip, a hold-back, a stand-down. Not a
/// failure, and worth being able to tell apart at a glance.
let skip text =
    line Console.Out Console.IsOutputRedirected ConsoleColor.DarkYellow text

/// Advice with no fix behind it. Deliberately NOT the skip colour: a
/// skip says we declined to act, a note asks the reader to — and for a
/// note-only rule it is the whole output, not an aside.
let note text =
    line Console.Out Console.IsOutputRedirected ConsoleColor.DarkCyan text

/// Failure. Stays on stderr, where it already was.
let bad text =
    line Console.Error Console.IsErrorRedirected ConsoleColor.Red text
