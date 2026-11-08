module Bindery.Core.Tests.ProtocolTests

open System
open Xunit
open Bindery.Core.Protocol
open Bindery.Core.Tests.Fixtures

let private ok result =
    match result with
    | Ok value -> value
    | Error message -> failwithf "expected Ok, got Error: %s" message

let private err result =
    match result with
    | Error message -> message
    | Ok _ -> failwith "expected Error, got Ok"

// ---------------------------------------------------------------- manifest

[<Fact>]
let ``manifest parses the fields the host depends on`` () =
    let manifest = Manifest.parse manifestJson |> ok

    Assert.Equal("fanficfare", manifest.Name)
    Assert.Equal("FanFicFare", manifest.DisplayName)
    Assert.Equal(100, manifest.Priority)
    Assert.True manifest.Capabilities.Probe
    Assert.True manifest.Capabilities.Update
    Assert.Single manifest.Matches |> ignore

[<Fact>]
let ``manifest defaults cancel to true and everything else to false`` () =
    let manifest = Manifest.parse """{"protocolVersion":1,"name":"p","version":"1"}""" |> ok

    Assert.True manifest.Capabilities.Cancel
    Assert.False manifest.Capabilities.Probe
    Assert.False manifest.Capabilities.Cover
    Assert.Equal<string list>([ "epub" ], manifest.Formats)
    Assert.Equal(Declarative, manifest.Ui.Mode)
    Assert.Equal("p", manifest.DisplayName)

[<Fact>]
let ``manifest formats are lowercased`` () =
    let manifest = Manifest.parse manifestJson |> ok
    Assert.Equal<string list>([ "epub"; "txt" ], manifest.Formats)

[<Fact>]
let ``manifest rejects a protocol version this build does not implement`` () =
    let message = Manifest.parse """{"protocolVersion":2,"name":"p","version":"1"}""" |> err
    Assert.Contains("protocolVersion 2", message)

[<Fact>]
let ``manifest rejects a name that cannot go in a URL`` () =
    for name in [ "Has Spaces"; "UPPER"; "-leading"; ""; "../escape"; "under_score"; String('a', 40) ] do
        let json = sprintf """{"protocolVersion":1,"name":"%s","version":"1"}""" name
        Assert.True((Manifest.parse json |> Result.isError), sprintf "%s should have been rejected" name)

[<Fact>]
let ``manifest drops a default on a secret field`` () =
    let manifest = Manifest.parse manifestJson |> ok
    let secret = manifest.Config |> List.find (fun field -> field.Key = "ao3_password")

    // A prefilled password box is how a secret leaks back into a page.
    Assert.Equal(SecretField, secret.Type)
    Assert.Equal(None, secret.Default)
    Assert.Equal<string list>([ "ao3_password" ], manifest.SecretKeys)

[<Fact>]
let ``manifest select options default their label to their value`` () =
    let manifest = Manifest.parse manifestJson |> ok
    let field = manifest.Config |> List.find (fun field -> field.Key = "format")

    Assert.Equal(SelectField, field.Type)
    Assert.Equal<SelectOption list>(
        [ { Value = "epub"; Label = "EPUB" }; { Value = "txt"; Label = "txt" } ],
        field.Options)

[<Fact>]
let ``manifest drops nav entries that traverse or lack a path`` () =
    let manifest = Manifest.parse manifestJson |> ok

    Assert.Equal(Fragment, manifest.Ui.Mode)
    Assert.Equal<string list>([ "/" ], manifest.Ui.Nav |> List.map (fun entry -> entry.Path))

[<Fact>]
let ``manifest ignores members added by a later protocol revision`` () =
    // Additive change tolerance is a protocol requirement, not politeness.
    let manifest = Manifest.parse manifestJson |> ok
    Assert.Equal("0.1.0", manifest.Version)

[<Fact>]
let ``manifest parses an action schema`` () =
    let manifest = Manifest.parse manifestJson |> ok
    let action = Assert.Single manifest.Actions

    Assert.Equal("list_urls", action.Name)
    Assert.Equal(ListOutput DownloadItem, action.Output)
    Assert.True (Assert.Single action.Input).Required

[<Fact>]
let ``manifest config scope defaults to install and fails closed to user`` () =
    let manifest =
        Manifest.parse
            """{"protocolVersion":1,"name":"p","version":"1","config":[
                 {"key":"agent","label":"Agent","type":"string"},
                 {"key":"login","label":"Login","type":"string","scope":"user"},
                 {"key":"shared","label":"Shared","type":"string","scope":"install"},
                 {"key":"typo","label":"Typo","type":"secret","scope":"usr"}]}"""
        |> ok

    Assert.Equal<string list>([ "agent"; "shared" ], manifest.InstallConfig |> List.map (fun field -> field.Key))
    // An unknown scope must never turn one person's password into everyone's.
    Assert.Equal<string list>([ "login"; "typo" ], manifest.UserConfig |> List.map (fun field -> field.Key))

[<Fact>]
let ``manifest access defaults to member and fails closed to admin`` () =
    let manifest =
        Manifest.parse
            """{"protocolVersion":1,"name":"p","version":"1",
                "actions":[
                  {"name":"look","label":"Look","output":{"kind":"text"}},
                  {"name":"wipe","label":"Wipe","access":"admin","output":{"kind":"text"}},
                  {"name":"oops","label":"Oops","access":"admn","output":{"kind":"text"}}],
                "ui":{"mode":"sandboxed","nav":[
                  {"label":"Home","path":"/"},
                  {"label":"Sources","path":"/sources","access":"admin"}],
                  "adminPaths":["/sources"]}}"""
        |> ok

    Assert.Equal<Access list>([ MemberAccess; AdminAccess; AdminAccess ], manifest.Actions |> List.map (fun a -> a.Access))
    Assert.Equal<Access list>([ MemberAccess; AdminAccess ], manifest.Ui.Nav |> List.map (fun n -> n.Access))
    Assert.Equal<string list>([ "/sources" ], manifest.Ui.AdminPaths)
    Assert.True(MemberAccess.Permits false)
    Assert.False(AdminAccess.Permits false)
    Assert.True(AdminAccess.Permits true)

let private uiWith adminPaths =
    { Mode = Sandboxed
      Entry = "/"
      Nav = []
      AdminPaths = adminPaths }

[<Fact>]
let ``an admin path covers itself and what is below it, not its neighbours`` () =
    let ui = uiWith [ "/sources" ]

    for path in [ "/sources"; "/sources/"; "/sources/3"; "sources"; "/sources?x=1" ] do
        Assert.True(ui.IsAdminPath path, sprintf "%s should be admin" path)

    for path in [ "/"; "/sourcesx"; "/chapters"; "/chapters/sources" ] do
        Assert.False(ui.IsAdminPath path, sprintf "%s should not be admin" path)

    Assert.False((uiWith []).IsAdminPath "/sources")

[<Fact>]
let ``an admin path cannot be dodged by spelling it differently`` () =
    let ui = uiWith [ "/sources" ]

    for path in
        [ "/SOURCES"
          "//sources"
          "/./sources"
          "/x/../sources"
          "\\sources\\1"
          "/%73ources"
          "/%2573ources"
          "/x%2F..%2Fsources" ] do
        Assert.True(ui.IsAdminPath path, sprintf "%s should be admin" path)

[<Fact>]
let ``a root admin path reserves the whole interface`` () =
    let ui = uiWith [ "/" ]
    Assert.True(ui.IsAdminPath "/")
    Assert.True(ui.IsAdminPath "/anything/at/all")

// ---------------------------------------------------------------- events

[<Fact>]
let ``progress events clamp percent into range`` () =
    match Event.parse """{"event":"progress","percent":140,"message":"chapter 4"}""" |> ok with
    | Progress(Some percent, Some message) ->
        Assert.Equal(100.0, percent)
        Assert.Equal("chapter 4", message)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``progress may omit percent entirely`` () =
    match Event.parse """{"event":"progress","message":"working"}""" |> ok with
    | Progress(None, Some "working") -> ()
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``log level falls back to info rather than failing the stream`` () =
    match Event.parse """{"event":"log","level":"chatty","message":"hi"}""" |> ok with
    | LogLine(LogLevel.Info, "hi") -> ()
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``an unknown event kind is recognized as unknown, not an error`` () =
    match Event.parse """{"event":"heartbeat","at":12}""" |> ok with
    | Unrecognized "heartbeat" -> ()
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``an ok result carries normalized artifacts and metadata`` () =
    match Event.parse resultOkJson |> ok with
    | Result(Succeeded(artifacts, metadata)) ->
        Assert.Equal(2, artifacts.Length)
        let book = artifacts |> List.find (fun artifact -> artifact.Primary)
        Assert.Equal("epub", book.Format)
        Assert.Equal(Some "abc0000000000000000000000000000000000000000000000000000000000123", book.Sha256)
        Assert.Equal(CoverArtifact, (artifacts |> List.find (fun a -> a.Id = "cover")).Kind)
        Assert.Equal("Story", metadata.Title)
        Assert.Equal<string list>([ "Someone"; "Someone Else" ], metadata.Authors)
        Assert.Equal(Some 2.0, metadata.SeriesIndex)
        Assert.Equal(Some 10, metadata.Chapters)
        Assert.Equal(Some(at "2026-08-01T00:00:00Z"), metadata.Updated)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``an ok result must name exactly one primary artifact`` () =
    let twoPrimaries =
        """{"event":"result","status":"ok",
            "artifacts":[{"id":"a","filename":"a.epub","format":"epub","primary":true},
                         {"id":"b","filename":"b.epub","format":"epub","primary":true}],
            "metadata":{"title":"x"}}"""

    Assert.Contains("exactly one primary", Event.parse twoPrimaries |> err)

[<Fact>]
let ``an ok result must carry an artifact`` () =
    let none = """{"event":"result","status":"ok","artifacts":[],"metadata":{"title":"x"}}"""
    Assert.Contains("at least one artifact", Event.parse none |> err)

[<Fact>]
let ``an ok result must carry a title`` () =
    let untitled =
        """{"event":"result","status":"ok",
            "artifacts":[{"id":"a","filename":"a.epub","format":"epub","primary":true}],
            "metadata":{"authors":["x"]}}"""

    Assert.Contains("title", Event.parse untitled |> err)

[<Fact>]
let ``duplicate artifact ids are rejected because they become URLs`` () =
    let duplicated =
        """{"event":"result","status":"ok",
            "artifacts":[{"id":"a","filename":"a.epub","format":"epub","primary":true},
                         {"id":"a","filename":"b.epub","format":"epub"}],
            "metadata":{"title":"x"}}"""

    Assert.Contains("unique", Event.parse duplicated |> err)

[<Fact>]
let ``unchanged is terminal and carries nothing`` () =
    match Event.parse """{"event":"result","status":"unchanged"}""" |> ok with
    | Result Unchanged -> ()
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``error retryability defaults by code and is overridable`` () =
    match Event.parse """{"event":"result","status":"error","error":{"code":"network","message":"boom"}}""" |> ok with
    | Result(Failed error) ->
        Assert.Equal(NetworkError, error.Code)
        Assert.True error.Retryable
    | other -> failwithf "unexpected %A" other

    match
        Event.parse """{"event":"result","status":"error","error":{"code":"network","message":"boom","retryable":false}}"""
        |> ok
    with
    | Result(Failed error) -> Assert.False error.Retryable
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``an unknown error code is retryable and preserved verbatim`` () =
    match Event.parse """{"event":"result","status":"error","error":{"code":"teapot","message":"m"}}""" |> ok with
    | Result(Failed error) ->
        Assert.Equal(UnknownCode "teapot", error.Code)
        Assert.Equal("teapot", error.Code.Wire)
        Assert.True error.Retryable
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``a line that is not JSON is an error, not an exception`` () =
    Assert.Contains("invalid JSON", Event.parse "not json at all {" |> err)
    Assert.Contains("empty", Event.parse "   " |> err)
    Assert.Contains("'event'", Event.parse """{"percent":50}""" |> err)

// ---------------------------------------------------------------- probe

[<Fact>]
let ``probe confidence defaults from supported and clamps`` () =
    Assert.Equal(1.0, (Probe.parse """{"supported":true}""" |> ok).Confidence)
    Assert.Equal(0.0, (Probe.parse """{"supported":false}""" |> ok).Confidence)
    Assert.Equal(1.0, (Probe.parse """{"supported":true,"confidence":5}""" |> ok).Confidence)
    Assert.True(Probe.parse """{"reason":"no idea"}""" |> Result.isError)

// ---------------------------------------------------------------- actions

[<Fact>]
let ``action list output keeps only items with a title`` () =
    let json =
        """{"status":"ok","output":{"kind":"list","items":[
             {"title":"One","url":"https://example.test/1"},
             {"subtitle":"no title so dropped"}]}}"""

    match Action.parse json |> ok with
    | ItemsResult items ->
        let item = Assert.Single items
        Assert.Equal("One", item.Title)
        Assert.Equal(Some "https://example.test/1", item.Url)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``an action error is an outcome, not a parse failure`` () =
    match Action.parse """{"status":"error","error":{"code":"auth_required","message":"login"}}""" |> ok with
    | ActionFailed error -> Assert.Equal(AuthRequired, error.Code)
    | other -> failwithf "unexpected %A" other

// ---------------------------------------------------------------- requests

[<Fact>]
let ``a download request round-trips through its own wire format`` () =
    let request =
        { JobId = Guid.Parse "9f1b2c3d-0000-4444-8888-aaaabbbbcccc"
          Url = "https://example.test/works/1"
          Config = Map [ "ao3_username", "someone"; "is_adult", "true" ]
          Options =
            { Update = true
              KnownSourceId = Some "ao3:1"
              KnownUpdated = Some(at "2026-08-01T00:00:00Z")
              Formats = [ "epub" ] } }

    let json = Request.download request
    let parsed = Bindery.Core.Json.parse json |> ok

    Assert.Equal("9f1b2c3d-0000-4444-8888-aaaabbbbcccc", Bindery.Core.Json.stringOr "" "jobId" parsed)
    Assert.Equal("https://example.test/works/1", Bindery.Core.Json.stringOr "" "url" parsed)
    Assert.Equal("someone", Bindery.Core.Json.stringMap "config" parsed |> Map.find "ao3_username")

    let options = Bindery.Core.Json.tryProp "options" parsed |> Option.get
    Assert.True(Bindery.Core.Json.boolOr false "update" options)
    Assert.Equal("2026-08-01T00:00:00Z", Bindery.Core.Json.stringOr "" "knownUpdated" options)

[<Fact>]
let ``secrets are only ever sent, never echoed into a manifest default`` () =
    // Belt and braces around the one field type that must not leak: the parser drops the
    // default, so no code path can render it back into a form.
    let manifest = Manifest.parse manifestJson |> ok
    Assert.DoesNotContain("leaked?", sprintf "%A" manifest.Config)
