/// The plugin protocol, as data.
///
/// This is the F# half of the argument for having an F# core: every message in
/// `docs/PLUGIN-PROTOCOL.md` is a sum or product of other messages, the terminal states
/// are genuinely alternatives rather than a status string plus a pile of maybe-fields,
/// and the compiler will not let a new event kind be forgotten at a match site.
///
/// Parsers here are total. They return `Result` and never throw, because their input is
/// written by someone else's container.
module Bindery.Core.Protocol

open System
open System.Text
open System.Text.Json
open Bindery.Core

/// The protocol version this build implements.
[<Literal>]
let ProtocolVersion = 1

// ---------------------------------------------------------------- manifest

type FieldType =
    | StringField
    | SecretField
    | TextField
    | BoolField
    | IntField
    | SelectField
    | UrlField

    member this.Wire =
        match this with
        | StringField -> "string"
        | SecretField -> "secret"
        | TextField -> "text"
        | BoolField -> "bool"
        | IntField -> "int"
        | SelectField -> "select"
        | UrlField -> "url"

    /// Whether a value of this type must never be rendered back to a browser or logged.
    member this.IsSecret = this = SecretField

module FieldType =
    let ofWire (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "string" -> Some StringField
        | "secret" -> Some SecretField
        | "text" -> Some TextField
        | "bool" -> Some BoolField
        | "int" -> Some IntField
        | "select" -> Some SelectField
        | "url" -> Some UrlField
        | _ -> None

type SelectOption = { Value: string; Label: string }

type ConfigField =
    { Key: string
      Label: string
      Type: FieldType
      Required: bool
      Default: string option
      Help: string option
      Placeholder: string option
      Options: SelectOption list
      Min: int option
      Max: int option
      Pattern: string option }

type UiMode =
    | Declarative
    | Fragment
    | Iframe

    member this.Wire =
        match this with
        | Declarative -> "declarative"
        | Fragment -> "fragment"
        | Iframe -> "iframe"

    /// Whether the host must reverse-proxy and sanitize HTML for this plugin.
    member this.ServesHtml = this <> Declarative

module UiMode =
    let ofWire (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "fragment" -> Some Fragment
        | "iframe" -> Some Iframe
        | "declarative" -> Some Declarative
        | _ -> None

type NavEntry =
    { Label: string
      Path: string
      Icon: string option
      Section: string option }

type PluginUi = { Mode: UiMode; Nav: NavEntry list }

type Capabilities =
    { Probe: bool
      Update: bool
      Metadata: bool
      Cover: bool
      Cancel: bool }

    static member Default =
        { Probe = false
          Update = false
          Metadata = false
          Cover = false
          Cancel = true }

type ItemAction =
    | DownloadItem
    | InertItem

type ActionOutput =
    | ListOutput of itemAction: ItemAction
    | TextOutput
    | MessageOutput

    member this.Wire =
        match this with
        | ListOutput _ -> "list"
        | TextOutput -> "text"
        | MessageOutput -> "message"

type PluginAction =
    { Name: string
      Label: string
      Description: string option
      Input: ConfigField list
      Output: ActionOutput }

type Manifest =
    { ProtocolVersion: int
      Name: string
      DisplayName: string
      Version: string
      Homepage: string option
      Description: string option
      Priority: int
      Matches: string list
      Formats: string list
      Capabilities: Capabilities
      Config: ConfigField list
      Actions: PluginAction list
      Ui: PluginUi }

    member this.SecretKeys =
        this.Config |> List.filter (fun field -> field.Type.IsSecret) |> List.map (fun field -> field.Key)

// ---------------------------------------------------------------- messages

type ArtifactKind =
    | BookArtifact
    | CoverArtifact

type Artifact =
    { Id: string
      Filename: string
      Format: string
      ContentType: string option
      Kind: ArtifactKind
      Primary: bool
      Bytes: int64 option
      Sha256: string option }

type BookMetadata =
    { Title: string
      Authors: string list
      Series: string option
      SeriesIndex: float option
      Summary: string option
      Language: string option
      Tags: string list
      SourceId: string option
      SourceUrl: string option
      Publisher: string option
      Chapters: int option
      Published: DateTimeOffset option
      Updated: DateTimeOffset option }

    static member Empty =
        { Title = ""
          Authors = []
          Series = None
          SeriesIndex = None
          Summary = None
          Language = None
          Tags = []
          SourceId = None
          SourceUrl = None
          Publisher = None
          Chapters = None
          Published = None
          Updated = None }

type ErrorCode =
    | UnsupportedUrl
    | NotFound
    | AuthRequired
    | RateLimited
    | NetworkError
    | ParseError
    | Cancelled
    | InternalError
    /// A code this build does not know. Additive changes must not break older hosts.
    | UnknownCode of code: string

    member this.Wire =
        match this with
        | UnsupportedUrl -> "unsupported_url"
        | NotFound -> "not_found"
        | AuthRequired -> "auth_required"
        | RateLimited -> "rate_limited"
        | NetworkError -> "network"
        | ParseError -> "parse"
        | Cancelled -> "cancelled"
        | InternalError -> "internal"
        | UnknownCode value -> value

    /// The default when a plugin omits `retryable`. Unknown codes are treated as
    /// `internal`: retryable, because unrecognized failures are more often transient.
    member this.RetryableByDefault =
        match this with
        | RateLimited
        | NetworkError
        | InternalError
        | UnknownCode _ -> true
        | UnsupportedUrl
        | NotFound
        | AuthRequired
        | ParseError
        | Cancelled -> false

module ErrorCode =
    let ofWire (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "unsupported_url" -> UnsupportedUrl
        | "not_found" -> NotFound
        | "auth_required" -> AuthRequired
        | "rate_limited" -> RateLimited
        | "network" -> NetworkError
        | "parse" -> ParseError
        | "cancelled" -> Cancelled
        | "internal" -> InternalError
        | other -> UnknownCode other

type ProtocolError =
    { Code: ErrorCode
      Message: string
      Retryable: bool }

    static member Of(code: ErrorCode, message: string) =
        { Code = code; Message = message; Retryable = code.RetryableByDefault }

/// How a download ended. Three genuinely different outcomes, so three cases — not one
/// record with a status string and two sets of fields that are only sometimes populated.
type DownloadOutcome =
    | Succeeded of artifacts: Artifact list * metadata: BookMetadata
    | Unchanged
    | Failed of error: ProtocolError

/// Qualified access is required because an unqualified `Error` case would shadow
/// `Result.Error`, which every parser in this file returns.
[<RequireQualifiedAccess>]
type LogLevel =
    | Debug
    | Info
    | Warn
    | Error

    member this.Wire =
        match this with
        | LogLevel.Debug -> "debug"
        | LogLevel.Info -> "info"
        | LogLevel.Warn -> "warn"
        | LogLevel.Error -> "error"

module LogLevel =
    let ofWire (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "debug" -> Some LogLevel.Debug
        | "info" -> Some LogLevel.Info
        | "warn" | "warning" -> Some LogLevel.Warn
        | "error" -> Some LogLevel.Error
        | _ -> None

/// One line of the NDJSON stream.
type PluginEvent =
    | Progress of percent: float option * message: string option
    | LogLine of level: LogLevel * message: string
    | Result of outcome: DownloadOutcome
    /// An event kind this build does not implement. The protocol requires consumers to
    /// ignore these, and making that an explicit case means "ignore" is a decision at
    /// every match site rather than a silently missing branch.
    | Unrecognized of kind: string

// ---------------------------------------------------------------- requests

type DownloadOptions =
    { Update: bool
      KnownSourceId: string option
      KnownUpdated: DateTimeOffset option
      Formats: string list }

    static member Default =
        { Update = false
          KnownSourceId = None
          KnownUpdated = None
          Formats = [] }

type DownloadRequest =
    { JobId: Guid
      Url: string
      Config: Map<string, string>
      Options: DownloadOptions }

type ProbeResult =
    { Supported: bool
      Confidence: float
      Reason: string option }

type ActionItem =
    { Title: string
      Url: string option
      Subtitle: string option
      Thumbnail: string option }

type ActionOutcome =
    | ItemsResult of items: ActionItem list
    | TextResult of text: string
    | MessageResult of level: LogLevel * message: string
    | ActionFailed of error: ProtocolError

// ---------------------------------------------------------------- parsing

module private Parse =
    let field (element: JsonElement) : ConfigField option =
        match Json.tryText "key" element, Json.tryText "label" element, Json.tryText "type" element with
        | Some key, Some label, Some rawType ->
            FieldType.ofWire rawType
            |> Option.map (fun fieldType ->
                { Key = key
                  Label = label
                  Type = fieldType
                  Required = Json.boolOr false "required" element
                  // A default on a secret is a protocol violation; drop it rather than
                  // let it become a prefilled password in a form.
                  Default = (if fieldType.IsSecret then None else Json.tryString "default" element)
                  Help = Json.tryText "help" element
                  Placeholder = Json.tryString "placeholder" element
                  Options =
                    Json.items "options" element
                    |> List.choose (fun option ->
                        Json.tryText "value" option
                        |> Option.map (fun value ->
                            { Value = value
                              Label = Json.stringOr value "label" option }))
                  Min = Json.tryInt "min" element
                  Max = Json.tryInt "max" element
                  Pattern = Json.tryText "pattern" element })
        | _ -> None

    let capabilities (element: JsonElement) =
        match Json.tryProp "capabilities" element with
        | None -> Capabilities.Default
        | Some caps ->
            { Probe = Json.boolOr false "probe" caps
              Update = Json.boolOr false "update" caps
              Metadata = Json.boolOr false "metadata" caps
              Cover = Json.boolOr false "cover" caps
              Cancel = Json.boolOr true "cancel" caps }

    let ui (element: JsonElement) =
        match Json.tryProp "ui" element with
        | None -> { Mode = Declarative; Nav = [] }
        | Some ui ->
            { Mode = Json.tryText "mode" ui |> Option.bind UiMode.ofWire |> Option.defaultValue Declarative
              Nav =
                Json.items "nav" ui
                |> List.choose (fun entry ->
                    match Json.tryText "label" entry, Json.tryText "path" entry with
                    | Some label, Some path when path.StartsWith "/" && not (path.Contains "..") ->
                        Some
                            { Label = label
                              Path = path
                              Icon = Json.tryText "icon" entry
                              Section = Json.tryText "section" entry }
                    | _ -> None) }

    let action (element: JsonElement) : PluginAction option =
        match Json.tryText "name" element, Json.tryText "label" element with
        | Some name, Some label ->
            let output =
                match Json.tryProp "output" element with
                | None -> Some MessageOutput
                | Some output ->
                    match Json.tryText "kind" output with
                    | Some "list" ->
                        Some(
                            ListOutput(
                                if Json.stringOr "none" "itemAction" output = "download" then
                                    DownloadItem
                                else
                                    InertItem))
                    | Some "text" -> Some TextOutput
                    | Some "message" -> Some MessageOutput
                    | _ -> None

            output
            |> Option.map (fun output ->
                { Name = name
                  Label = label
                  Description = Json.tryText "description" element
                  Input = Json.items "input" element |> List.choose field
                  Output = output })
        | _ -> None

    let artifact (element: JsonElement) : Artifact option =
        match Json.tryText "id" element, Json.tryText "filename" element, Json.tryText "format" element with
        | Some id, Some filename, Some format ->
            Some
                { Id = id
                  Filename = filename
                  Format = format.ToLowerInvariant()
                  ContentType = Json.tryText "contentType" element
                  Kind = (if Json.stringOr "book" "kind" element = "cover" then CoverArtifact else BookArtifact)
                  Primary = Json.boolOr false "primary" element
                  Bytes = Json.tryLong "bytes" element
                  Sha256 = Json.tryText "sha256" element |> Option.map (fun value -> value.ToLowerInvariant()) }
        | _ -> None

    let metadata (element: JsonElement) : Result<BookMetadata, string> =
        match Json.tryText "title" element with
        | None -> Error "result metadata is missing a title"
        | Some title ->
            Ok
                { Title = title
                  Authors = Json.strings "authors" element
                  Series = Json.tryText "series" element
                  SeriesIndex = Json.tryFloat "seriesIndex" element
                  Summary = Json.tryText "summary" element
                  Language = Json.tryText "language" element
                  Tags = Json.strings "tags" element |> List.distinct
                  SourceId = Json.tryText "sourceId" element
                  SourceUrl = Json.tryText "sourceUrl" element
                  Publisher = Json.tryText "publisher" element
                  Chapters = Json.tryInt "chapters" element
                  Published = Json.tryTimestamp "published" element
                  Updated = Json.tryTimestamp "updated" element }

    let error (element: JsonElement) : ProtocolError =
        match Json.tryProp "error" element with
        | None -> ProtocolError.Of(InternalError, "plugin reported an error without describing it")
        | Some err ->
            let code = Json.tryText "code" err |> Option.map ErrorCode.ofWire |> Option.defaultValue InternalError

            { Code = code
              Message = Json.stringOr "the plugin gave no reason" "message" err
              Retryable = Json.tryBool "retryable" err |> Option.defaultValue code.RetryableByDefault }

module Manifest =
    /// Names go into URLs, config keys, and file paths, so the constraint is enforced
    /// rather than assumed.
    let private namePattern =
        Text.RegularExpressions.Regex(@"^[a-z0-9][a-z0-9-]{0,31}$", Text.RegularExpressions.RegexOptions.Compiled)

    let isValidName (name: string) = namePattern.IsMatch name

    let parseElement (element: JsonElement) : Result<Manifest, string> =
        match Json.tryInt "protocolVersion" element with
        | None -> Error "manifest is missing 'protocolVersion'"
        | Some version when version <> ProtocolVersion ->
            Error(sprintf "manifest declares protocolVersion %d; this build implements %d" version ProtocolVersion)
        | Some version ->
            match Json.tryText "name" element with
            | None -> Error "manifest is missing 'name'"
            | Some name when not (isValidName name) ->
                Error(sprintf "manifest name %s must match ^[a-z0-9][a-z0-9-]{0,31}$" name)
            | Some name ->
                match Json.tryText "version" element with
                | None -> Error "manifest is missing 'version'"
                | Some pluginVersion ->
                    Ok
                        { ProtocolVersion = version
                          Name = name
                          DisplayName = Json.stringOr name "displayName" element
                          Version = pluginVersion
                          Homepage = Json.tryText "homepage" element
                          Description = Json.tryText "description" element
                          Priority = Json.intOr 0 "priority" element
                          Matches = Json.strings "matches" element
                          Formats =
                            (match Json.strings "formats" element with
                             | [] -> [ "epub" ]
                             | formats -> formats |> List.map (fun f -> f.ToLowerInvariant()))
                          Capabilities = Parse.capabilities element
                          Config = Json.items "config" element |> List.choose Parse.field
                          Actions = Json.items "actions" element |> List.choose Parse.action
                          Ui = Parse.ui element }

    let parse (text: string) : Result<Manifest, string> =
        Json.parse text |> Result.bind parseElement

module Event =
    let parseElement (element: JsonElement) : Result<PluginEvent, string> =
        match Json.tryText "event" element with
        | None -> Error "NDJSON line has no 'event' member"
        | Some "progress" ->
            let percent =
                Json.tryFloat "percent" element
                |> Option.map (fun value -> Math.Clamp(value, 0.0, 100.0))

            Ok(Progress(percent, Json.tryText "message" element))
        | Some "log" ->
            let level = Json.tryText "level" element |> Option.bind LogLevel.ofWire |> Option.defaultValue LogLevel.Info
            Ok(LogLine(level, Json.stringOr "" "message" element))
        | Some "result" ->
            match Json.tryText "status" element with
            | Some "unchanged" -> Ok(Result Unchanged)
            | Some "error" -> Ok(Result(Failed(Parse.error element)))
            | Some "ok" ->
                let artifacts = Json.items "artifacts" element |> List.choose Parse.artifact

                if artifacts.IsEmpty then
                    Error "an 'ok' result carries at least one artifact"
                elif artifacts |> List.filter (fun a -> a.Primary) |> List.length <> 1 then
                    Error "an 'ok' result carries exactly one primary artifact"
                elif artifacts |> List.map (fun a -> a.Id) |> List.distinct |> List.length <> artifacts.Length then
                    Error "artifact ids must be unique within a job"
                else
                    match Json.tryProp "metadata" element with
                    | None -> Error "an 'ok' result carries metadata"
                    | Some metadata ->
                        Parse.metadata metadata
                        |> Result.map (fun metadata -> Result(Succeeded(artifacts, metadata)))
            | Some other -> Error(sprintf "unknown result status '%s'" other)
            | None -> Error "result event has no 'status'"
        | Some other -> Ok(Unrecognized other)

    let parse (line: string) : Result<PluginEvent, string> =
        Json.parse line |> Result.bind parseElement

module Probe =
    let parse (text: string) : Result<ProbeResult, string> =
        Json.parse text
        |> Result.bind (fun element ->
            match Json.tryBool "supported" element with
            | None -> Error "probe response is missing 'supported'"
            | Some supported ->
                Ok
                    { Supported = supported
                      Confidence =
                        Json.tryFloat "confidence" element
                        |> Option.map (fun value -> Math.Clamp(value, 0.0, 1.0))
                        |> Option.defaultValue (if supported then 1.0 else 0.0)
                      Reason = Json.tryText "reason" element })

module Action =
    let parse (text: string) : Result<ActionOutcome, string> =
        Json.parse text
        |> Result.bind (fun element ->
            match Json.stringOr "ok" "status" element with
            | "error" -> Ok(ActionFailed(Parse.error element))
            | _ ->
                match Json.tryProp "output" element with
                | None -> Error "action response is missing 'output'"
                | Some output ->
                    match Json.tryText "kind" output with
                    | Some "list" ->
                        Json.items "items" output
                        |> List.choose (fun item ->
                            Json.tryText "title" item
                            |> Option.map (fun title ->
                                { Title = title
                                  Url = Json.tryText "url" item
                                  Subtitle = Json.tryText "subtitle" item
                                  Thumbnail = Json.tryText "thumbnail" item }))
                        |> ItemsResult
                        |> Ok
                    | Some "text" -> Ok(TextResult(Json.stringOr "" "text" output))
                    | Some "message" ->
                        let level = Json.tryText "level" output |> Option.bind LogLevel.ofWire |> Option.defaultValue LogLevel.Info
                        Ok(MessageResult(level, Json.stringOr "" "message" output))
                    | Some other -> Error(sprintf "unknown action output kind '%s'" other)
                    | None -> Error "action output is missing 'kind'")

// ---------------------------------------------------------------- writing

module Request =
    let private write (build: Utf8JsonWriter -> unit) =
        use stream = new IO.MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        build writer
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    let download (request: DownloadRequest) : string =
        write (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("jobId", request.JobId.ToString "D")
            writer.WriteString("url", request.Url)

            writer.WriteStartObject "config"

            for KeyValue(key, value) in request.Config do
                writer.WriteString(key, value)

            writer.WriteEndObject()

            writer.WriteStartObject "options"
            writer.WriteBoolean("update", request.Options.Update)
            Json.writeOptString writer "knownSourceId" request.Options.KnownSourceId
            Json.writeOptTimestamp writer "knownUpdated" request.Options.KnownUpdated
            Json.writeStringArray writer "formats" request.Options.Formats
            writer.WriteEndObject()

            writer.WriteEndObject())

    let probe (url: string) : string =
        write (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("url", url)
            writer.WriteEndObject())

    let action (name: string) (input: Map<string, string>) (config: Map<string, string>) : string =
        write (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("action", name)

            writer.WriteStartObject "input"

            for KeyValue(key, value) in input do
                writer.WriteString(key, value)

            writer.WriteEndObject()

            writer.WriteStartObject "config"

            for KeyValue(key, value) in config do
                writer.WriteString(key, value)

            writer.WriteEndObject()
            writer.WriteEndObject())
