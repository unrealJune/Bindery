module Bindery.Core.Tests.RoutingTests

open Xunit
open Bindery.Core.Protocol
open Bindery.Core.Routing

let private manifest name priority probe patterns =
    { ProtocolVersion = 1
      Name = name
      DisplayName = name
      Version = "1.0.0"
      Homepage = None
      Description = None
      Priority = priority
      Matches = patterns
      Formats = [ "epub" ]
      Capabilities = { Capabilities.Default with Probe = probe }
      Config = []
      Actions = []
      Ui = { Mode = Declarative; Entry = "/"; Nav = [] } }

let private ao3 = compile (manifest "fanficfare" 100 true [ @"^https?://(www\.)?archiveofourown\.org/works/\d+" ])
let private generic = compile (manifest "generic" 0 false [ @"^https?://" ])
let private prober = compile (manifest "prober" 50 true [])

[<Fact>]
let ``higher priority wins over a broader match`` () =
    let ranked = candidates [ generic; ao3 ] "https://archiveofourown.org/works/12345"

    Assert.Equal("fanficfare", ranked.Head.Plugin)
    Assert.Equal(2, ranked.Length)

[<Fact>]
let ``matching is case-insensitive because URLs are`` () =
    let ranked = candidates [ ao3 ] "HTTPS://ArchiveOfOurOwn.ORG/works/12345"
    Assert.Equal("fanficfare", ranked.Head.Plugin)

[<Fact>]
let ``a probe-capable plugin is a candidate even with no matching pattern`` () =
    let ranked = candidates [ prober ] "https://something.test/story/1"
    let candidate = Assert.Single ranked

    Assert.Equal("prober", candidate.Plugin)
    Assert.True candidate.NeedsProbe
    Assert.Equal(0.0, candidate.Confidence)

[<Fact>]
let ``a pattern match outranks a probe-only guess at equal priority`` () =
    let matcher = compile (manifest "matcher" 50 false [ @"^https://site\.test/" ])
    let ranked = candidates [ prober; matcher ] "https://site.test/1"

    Assert.Equal("matcher", ranked.Head.Plugin)
    Assert.Equal(1.0, ranked.Head.Confidence)

[<Fact>]
let ``a plugin claiming nothing and unable to probe is never a candidate`` () =
    let silent = compile (manifest "silent" 0 false [])
    Assert.Empty(candidates [ silent ] "https://site.test/1")

[<Fact>]
let ``an uncompilable pattern is dropped without disabling the plugin`` () =
    let broken = compile (manifest "broken" 10 false [ "(unclosed"; @"^https://ok\.test/" ])

    Assert.Single broken.RejectedPatterns |> ignore
    Assert.Single broken.Patterns |> ignore
    Assert.Equal("broken", (candidates [ broken ] "https://ok.test/x").Head.Plugin)

[<Fact>]
let ``an over-long pattern is refused before it reaches the regex engine`` () =
    let huge = String.replicate (MaxPatternLength + 1) "a"
    let plugin = compile (manifest "huge" 0 false [ huge ])

    Assert.Empty plugin.Patterns
    Assert.Contains("longer than", snd plugin.RejectedPatterns.Head)

[<Fact>]
let ``a catastrophically backtracking pattern times out instead of hanging the host`` () =
    // The classic (a+)+$ against a long non-matching string. Without the match timeout
    // this test would not finish, which is exactly the failure mode being prevented.
    let evil = compile (manifest "evil" 0 false [ @"^(a+)+$" ])
    let subject = "https://" + String.replicate 40 "a" + "!"

    Assert.Empty(candidates [ evil ] subject)

[<Fact>]
let ``a plugin that declines on probe is removed from the running`` () =
    let candidate = (candidates [ ao3 ] "https://archiveofourown.org/works/1").Head

    Assert.Equal(None, applyProbe { Supported = false; Confidence = 0.0; Reason = None } candidate)

    match applyProbe { Supported = true; Confidence = 0.4; Reason = Some "maybe" } candidate with
    | Some confirmed ->
        Assert.Equal(0.4, confirmed.Confidence)
        Assert.False confirmed.NeedsProbe
        Assert.Equal("maybe", confirmed.Reason)
    | None -> failwith "expected the candidate to survive"

[<Fact>]
let ``ranking is stable when priority and confidence tie`` () =
    let first = compile (manifest "aaa" 5 false [ "^https://" ])
    let second = compile (manifest "bbb" 5 false [ "^https://" ])

    let forwards = candidates [ first; second ] "https://x.test" |> List.map (fun c -> c.Plugin)
    let backwards = candidates [ second; first ] "https://x.test" |> List.map (fun c -> c.Plugin)

    Assert.Equal<string list>(forwards, backwards)
    Assert.Equal<string list>([ "aaa"; "bbb" ], forwards)

[<Fact>]
let ``an empty URL matches nothing`` () =
    Assert.Empty(candidates [ ao3; generic; prober ] "")
    Assert.Empty(candidates [ ao3; generic; prober ] "   ")
