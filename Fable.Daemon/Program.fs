﻿open System
open System.IO
open System.Threading.Tasks
open System.Text.Json
open System.Text.Json.Serialization
open StreamJsonRpc
open Fable
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.SourceCodeServices
open Fable.Compiler.ProjectCracker
open Fable.Compiler.Util
open Fable.Daemon

type Msg =
    | ProjectChanged of payload : ProjectChangedPayload * AsyncReplyChannel<ProjectChangedResult>
    | CompileFile of fileName : string * AsyncReplyChannel<FileChangedResult>
    | Disconnect

type Model =
    {
        CliArgs : CliArgs
        Checker : InteractiveChecker
        CrackerResponse : CrackerResponse
        SourceReader : SourceReader
        PathResolver : PathResolver
    }

type PongResponse = { Message : string }

type CompiledProjectData =
    {
        ProjectOptions : FSharpProjectOptions
        CompiledFSharpFiles : Map<string, string>
        CliArgs : CliArgs
        Checker : InteractiveChecker
        CrackerResponse : CrackerResponse
        SourceReader : SourceReader
        PathResolver : PathResolver
    }

let tryCompileProject (payload : ProjectChangedPayload) : Async<Result<CompiledProjectData, string>> =
    async {
        try
            let cliArgs : CliArgs =
                {
                    ProjectFile = payload.Project
                    RootDir = Path.GetDirectoryName payload.Project
                    OutDir = None
                    IsWatch = false
                    Precompile = false
                    PrecompiledLib = None
                    PrintAst = false
                    FableLibraryPath = Some payload.FableLibrary
                    Configuration = "Release"
                    NoRestore = true
                    NoCache = true
                    NoParallelTypeCheck = false
                    SourceMaps = false
                    SourceMapsRoot = None
                    Exclude = []
                    Replace = Map.empty
                    CompilerOptions =
                        {
                            TypedArrays = false
                            ClampByteArrays = false
                            Language = Language.JavaScript
                            Define = [ "FABLE_COMPILER" ; "FABLE_COMPILER_4" ; "FABLE_COMPILER_JAVASCRIPT" ]
                            DebugMode = false
                            OptimizeFSharpAst = false
                            Verbosity = Verbosity.Verbose
                            // We keep using `.fs` for the compiled FSharp file, even though the contents will be JavaScript.
                            FileExtension = ".fs"
                            TriggeredByDependency = false
                            NoReflection = false
                        }
                    RunProcess = None
                    Verbosity = Verbosity.Normal
                }

            let crackerOptions = CrackerOptions (cliArgs, false)

            let crackerResponse =
                getFullProjectOpts CoolCatCracking.coolCatResolver crackerOptions

            let checker = InteractiveChecker.Create crackerResponse.ProjectOptions

            let sourceReader =
                Fable.Compiler.File.MakeSourceReader (
                    Array.map Fable.Compiler.File crackerResponse.ProjectOptions.SourceFiles
                )
                |> snd

            let dummyPathResolver =
                { new PathResolver with
                    member _.TryPrecompiledOutPath (_sourceDir, _relativePath) = None
                    member _.GetOrAddDeduplicateTargetDir (importDir, addTargetDir) = importDir
                }

            let! initialCompiledFiles =
                Fable.Compiler.CodeServices.compileProjectToJavaScript
                    sourceReader
                    checker
                    dummyPathResolver
                    cliArgs
                    crackerResponse

            return
                Ok
                    {
                        ProjectOptions = crackerResponse.ProjectOptions
                        CompiledFSharpFiles = initialCompiledFiles
                        CliArgs = cliArgs
                        Checker = checker
                        CrackerResponse = crackerResponse
                        SourceReader = sourceReader
                        PathResolver = dummyPathResolver
                    }
        with ex ->
            return Error ex.Message
    }

let tryCompileFile (model : Model) (fileName : string) : Async<Result<Map<string, string>, string>> =
    async {
        try
            let fileName = Path.normalizePath fileName

            let sourceReader =
                Fable.Compiler.File.MakeSourceReader (
                    Array.map Fable.Compiler.File model.CrackerResponse.ProjectOptions.SourceFiles
                )
                |> snd

            let! compiledFiles =
                Fable.Compiler.CodeServices.compileFileToJavaScript
                    sourceReader
                    model.Checker
                    model.PathResolver
                    model.CliArgs
                    model.CrackerResponse
                    fileName

            return Ok compiledFiles
        with ex ->
            return Error ex.Message
    }

type FableServer(sender : Stream, reader : Stream) as this =
    let jsonMessageFormatter = new SystemTextJsonFormatter ()

    do
        jsonMessageFormatter.JsonSerializerOptions <-
            let options =
                JsonSerializerOptions (PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

            let jsonFSharpOptions =
                JsonFSharpOptions
                    .Default()
                    .WithUnionTagName("case")
                    .WithUnionFieldsName ("fields")

            options.Converters.Add (JsonUnionConverter (jsonFSharpOptions))
            options

    let handler =
        new HeaderDelimitedMessageHandler (sender, reader, jsonMessageFormatter)

    let rpc : JsonRpc = new JsonRpc (handler, this)
    do rpc.StartListening ()

    let mailbox =
        MailboxProcessor.Start (fun inbox ->
            let rec loop (model : Model) =
                async {
                    let! msg = inbox.Receive ()

                    match msg with
                    | ProjectChanged (payload, replyChannel) ->
                        let! result = tryCompileProject payload

                        match result with
                        | Error error ->
                            replyChannel.Reply (ProjectChangedResult.Error error)
                            return! loop model
                        | Ok result ->
                            replyChannel.Reply (
                                ProjectChangedResult.Success (result.ProjectOptions, result.CompiledFSharpFiles)
                            )

                            return!
                                loop
                                    {
                                        CliArgs = result.CliArgs
                                        Checker = result.Checker
                                        CrackerResponse = result.CrackerResponse
                                        SourceReader = result.SourceReader
                                        PathResolver = result.PathResolver
                                    }

                    // TODO: this probably means the file was changed as well.
                    | CompileFile (fileName, replyChannel) ->
                        let! result = tryCompileFile model fileName

                        match result with
                        | Error error -> replyChannel.Reply (FileChangedResult.Error error)
                        | Ok compiledFiles -> replyChannel.Reply (FileChangedResult.Success compiledFiles)

                        return! loop model
                    | Disconnect -> return ()
                }

            loop Unchecked.defaultof<Model>
        )

    // log or something.
    let subscription = mailbox.Error.Subscribe (fun evt -> ())

    interface IDisposable with
        member _.Dispose () =
            if not (isNull subscription) then
                subscription.Dispose ()

            ()

    /// returns a hot task that resolves when the stream has terminated
    member this.WaitForClose = rpc.Completion

    [<JsonRpcMethod("fable/ping", UseSingleObjectParameterDeserialization = true)>]
    member _.Ping (_p : PingPayload) : Task<PongResponse> =
        task { return { Message = "And dotnet will answer" } }

    [<JsonRpcMethod("fable/init", UseSingleObjectParameterDeserialization = true)>]
    member _.Init (p : ProjectChangedPayload) =
        task { return! mailbox.PostAndAsyncReply (fun replyChannel -> Msg.ProjectChanged (p, replyChannel)) }

    [<JsonRpcMethod("fable/compile", UseSingleObjectParameterDeserialization = true)>]
    member _.CompileFile (p : CompileFilePayload) =
        task { return! mailbox.PostAndAsyncReply (fun replyChannel -> Msg.CompileFile (p.FileName, replyChannel)) }

let input = Console.OpenStandardInput ()
let output = Console.OpenStandardOutput ()

let daemon =
    new FableServer (Console.OpenStandardOutput (), Console.OpenStandardInput ())

AppDomain.CurrentDomain.ProcessExit.Add (fun _ -> (daemon :> IDisposable).Dispose ())
daemon.WaitForClose.GetAwaiter().GetResult ()
exit 0
