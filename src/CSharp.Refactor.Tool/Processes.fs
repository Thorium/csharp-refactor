/// Child processes — the verification builds — that can never hang the
/// tool. Ported from fsharp-refactor.
module CSharp.Refactor.Tool.Processes

open System
open System.Diagnostics
open System.IO

/// The stderr line runProcessIn writes for a child stopped at its time cap
/// begins with this, so that a classifier tests for the cap itself and not
/// for the prose after it.
[<Literal>]
let TimeCapMark = "[stopped at the time cap]"

let runProcessIn (workingDirectory: string option) (timeout: TimeSpan) (fileName: string) (arguments: string) =
    let psi =
        ProcessStartInfo(
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        )

    workingDirectory |> Option.iter (fun dir -> psi.WorkingDirectory <- dir)

    // MSBuild keeps its worker nodes alive after the build for the next
    // one to reuse. A node inherits the pipes below, so the child's exit
    // does not close them, and the flush wait after it (see there) has no
    // end. Nodes that end with their build hold nothing of ours.
    psi.Environment.["MSBUILDDISABLENODEREUSE"] <- "1"

    // MSBuildLocator points THIS process at the .NET SDK's MSBuild through the
    // environment; a Visual Studio MSBuild.exe launched with those inherited
    // would load the SDK's net472 task assemblies and fail (MSB4062). A child
    // finds its own.
    for name in
        [
            "MSBUILD_EXE_PATH"
            "MSBuildExtensionsPath"
            "MSBuildSDKsPath"
            "DOTNET_HOST_PATH"
        ] do
        psi.Environment.Remove name |> ignore

    // A child that cannot START is the fourth way: a blocked or missing
    // executable (a paket bootstrapper under application control, `mono`
    // absent, `dotnet` not on the PATH) throws out of Process.Start, and
    // that unwound the whole sweep from one checkout's restore. Reported
    // the way a failed exit is, so the caller skips that target and the
    // run goes on.
    let started =
        try
            Ok(Process.Start psi)
        with
        | :? System.ComponentModel.Win32Exception
        | :? IOException
        | :? UnauthorizedAccessException as e -> Error e.Message

    match started with
    | Error message -> -1, "", $"'{fileName}' could not be started: {message}"
    | Ok p ->

        use p = p

        // a prompt now reads end-of-input and gives up, instead of waiting
        p.StandardInput.Close()

        // Both pipes drain on their own callbacks. .NET raises each stream's
        // event in order, so a builder is never written from two threads at
        // once, and nothing here blocks on a task the child has to finish
        // first — which is what made reading one pipe then the other deadlock.
        let outText = Text.StringBuilder()
        let errText = Text.StringBuilder()

        p.OutputDataReceived.Add(fun e ->
            if not (isNull e.Data) then
                outText.AppendLine e.Data |> ignore)

        p.ErrorDataReceived.Add(fun e ->
            if not (isNull e.Data) then
                errText.AppendLine e.Data |> ignore)

        p.BeginOutputReadLine()
        p.BeginErrorReadLine()

        if p.WaitForExit(int timeout.TotalMilliseconds) then
            // the timed overload can return before the output callbacks have
            // flushed; the argument-less one waits for them - and for both
            // pipes to close, which a grandchild that inherited them holds
            // up for as long as it lives (an MSBuild node kept for reuse:
            // fifteen minutes, or forever under a test host). The output
            // is in the builders within milliseconds of the exit, so the
            // flush gets seconds, never the run.
            let flushed =
                System.Threading.Tasks.Task.Run(fun () ->
                    try
                        p.WaitForExit()
                    with
                    | :? InvalidOperationException
                    | :? ObjectDisposedException -> ())

            flushed.Wait(TimeSpan.FromSeconds 10.0) |> ignore
            p.ExitCode, outText.ToString(), errText.ToString()
        else
            try
                // the whole tree: MSBuild leaves worker nodes behind
                p.Kill true
            with
            | :? InvalidOperationException
            | :? NotSupportedException
            | :? System.ComponentModel.Win32Exception -> ()

            let minutes = timeout.TotalMinutes

            -1,
            "",
            $"{TimeCapMark} '{fileName} {arguments}' had not finished after {minutes} minutes, so it was stopped."

/// Long enough for a real build of a large project, short enough that a
/// stuck one is reported rather than waited on forever. FSREF_BUILD_MINUTES
/// raises it for a project whose compile alone takes longer: FSharpPlus's
/// SRTP-heavy test project needs ~23 minutes, and was skipped as "does not
/// Long enough for a real build of a large project, short enough that a
/// stuck one is reported rather than waited on forever. CSREF_BUILD_MINUTES
/// raises it for a project whose compile alone takes longer.
let processTimeout =
    match Environment.GetEnvironmentVariable "CSREF_BUILD_MINUTES" with
    | null
    | "" -> TimeSpan.FromMinutes 15.0
    | v ->
        match Double.TryParse(v, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
        | true, minutes when minutes > 0.0 -> TimeSpan.FromMinutes minutes
        | _ -> TimeSpan.FromMinutes 15.0

let runProcess (timeout: TimeSpan) (fileName: string) (arguments: string) =
    runProcessIn None timeout fileName arguments
