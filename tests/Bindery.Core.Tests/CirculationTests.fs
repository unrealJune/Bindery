module Bindery.Core.Tests.CirculationTests

open System
open System.Text.Json
open Xunit
open Bindery.Core.Protocol
open Bindery.Core.Circulation

let private ok result =
    match result with
    | Ok value -> value
    | Error message -> failwithf "expected Ok, got Error: %s" message

let private answered reply =
    match reply with
    | Answered value -> value
    | Refused error -> failwithf "expected an answer, got %s" error.Message

let private now = DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero)
let private days (n: float) = now.AddDays n

// ---------------------------------------------------------------- manifest

let private circulationManifest =
    """{"protocolVersion":1,"name":"lib","version":"1",
        "capabilities":{"circulation":true},
        "circulation":{
          "accountFields":[
            {"key":"library","label":"Library","type":"select","options":[{"value":"seattle"}]},
            {"key":"card","label":"Card","type":"string","required":true},
            {"key":"pin","label":"PIN","type":"secret","default":"leaked"}],
          "mediums":["physical","digital","holographic"],
          "holdOperations":["cancel","suspend","teleport"]}}"""

[<Fact>]
let ``a circulation manifest exposes its account schema`` () =
    let manifest = Manifest.parse circulationManifest |> ok
    let spec = manifest.Circulation |> Option.get

    Assert.True manifest.Capabilities.Circulation
    Assert.Equal<string list>([ "library"; "card"; "pin" ], spec.AccountFields |> List.map (fun f -> f.Key))
    Assert.Equal<Medium list>([ Physical; Digital ], spec.Mediums)
    Assert.Equal<HoldOperation list>([ CancelHold; SuspendHold ], spec.HoldOperations)
    // The same rule as config: a secret never carries a default into a form.
    Assert.Equal(None, (spec.AccountFields |> List.find (fun f -> f.Key = "pin")).Default)

[<Fact>]
let ``a circulation block without the capability is ignored`` () =
    let json = circulationManifest.Replace("\"circulation\":true", "\"circulation\":false")
    Assert.Equal(None, (Manifest.parse json |> ok).Circulation)

[<Fact>]
let ``a circulation capability with no account fields is not a capability`` () =
    let json =
        """{"protocolVersion":1,"name":"lib","version":"1","capabilities":{"circulation":true},
            "circulation":{"accountFields":[]}}"""

    Assert.Equal(None, (Manifest.parse json |> ok).Circulation)

[<Fact>]
let ``hold operations default to cancel only`` () =
    let json =
        """{"protocolVersion":1,"name":"lib","version":"1","capabilities":{"circulation":true},
            "circulation":{"accountFields":[{"key":"card","label":"Card","type":"string"}]}}"""

    let spec = (Manifest.parse json |> ok).Circulation |> Option.get
    Assert.Equal<HoldOperation list>([ CancelHold ], spec.HoldOperations)
    Assert.Equal<Medium list>([ Physical; Digital ], spec.Mediums)

[<Fact>]
let ``circulation error codes round-trip and are not retryable`` () =
    for code in [ NotHoldable; AlreadyHeld; HoldLimit; AccountBlocked ] do
        Assert.Equal(code, ErrorCode.ofWire code.Wire)
        Assert.False code.RetryableByDefault

// ---------------------------------------------------------------- parsing replies

[<Fact>]
let ``search items carry their medium and availability`` () =
    let page =
        Parse.search
            """{"status":"ok","total":2,"page":1,"pages":1,"items":[
                 {"id":"b1","title":"Dune","authors":["Herbert, Frank"],"medium":"physical","format":"book",
                  "formatLabel":"Book","holdable":true,
                  "availability":{"status":"unavailable","copies":12,"availableCopies":0,"holds":24}},
                 {"id":"e1","title":"Dune","medium":"digital","format":"ebook","provider":"OverDrive",
                  "holdable":false,"borrowUrl":"https://libby.example/borrow/e1"}]}"""
        |> ok
        |> answered

    let book, ebook = page.Items[0], page.Items[1]
    Assert.Equal(Physical, book.Medium)
    Assert.Equal(Unavailable, book.Availability.Status)
    Assert.Equal(Some 24, book.Availability.Holds)
    Assert.Equal(Digital, ebook.Medium)
    Assert.Equal(Some "OverDrive", ebook.Provider)
    Assert.Equal(UnknownAvailability, ebook.Availability.Status)
    Assert.Equal(Some "https://libby.example/borrow/e1", ebook.BorrowUrl)

[<Fact>]
let ``a borrow link that is not https is dropped`` () =
    let page =
        Parse.search
            """{"status":"ok","items":[{"id":"e1","title":"x","medium":"digital","borrowUrl":"javascript:alert(1)"}]}"""
        |> ok
        |> answered

    Assert.Equal(None, page.Items[0].BorrowUrl)

[<Fact>]
let ``a refused call is an answer, not a parse failure`` () =
    match Parse.verify """{"status":"error","error":{"code":"auth_required","message":"bad PIN"}}""" |> ok with
    | Refused error ->
        Assert.Equal(AuthRequired, error.Code)
        Assert.False error.Retryable
    | Answered _ -> failwith "expected a refusal"

[<Fact>]
let ``a snapshot accepts bare dates and keeps an unknown state as unknown`` () =
    let snapshot =
        Parse.snapshot
            """{"status":"ok","loans":[],"holds":[
                 {"id":"h1","title":"Dune","state":"ready","readyUntil":"2026-11-14",
                  "pickupLocation":{"id":"3","name":"Central Library"}},
                 {"id":"h2","title":"Other","state":"levitating"}]}"""
        |> ok
        |> answered

    let ready, odd = snapshot.Holds[0], snapshot.Holds[1]
    Assert.Equal(Some ReportedReady, ready.State)
    Assert.Equal(Some(DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero)), ready.ReadyUntil)
    Assert.Equal(Some "Central Library", ready.PickupLocation)
    Assert.Equal(None, odd.State)
    Assert.Equal("levitating", odd.RawState)

[<Fact>]
let ``a partial snapshot is refused rather than half-read`` () =
    // A dropped hold would be reconciled as closed and the person mailed. Refusing is safe.
    let result = Parse.snapshot """{"status":"ok","loans":[],"holds":[{"id":"h1","title":"ok"},{"id":"h2"}]}"""
    Assert.True(Result.isError result)
    Assert.True(Result.isError (Parse.snapshot """{"status":"ok","holds":[]}"""))

[<Fact>]
let ``a search request carries the account, query, and medium`` () =
    let account = { Id = "acct-1"; Fields = Map [ "card", "123"; "pin", "9" ] }
    use doc = JsonDocument.Parse(Request.search account "dune" (Some Digital) 0)
    let root = doc.RootElement

    Assert.Equal("acct-1", root.GetProperty("account").GetProperty("id").GetString())
    Assert.Equal("9", root.GetProperty("account").GetProperty("fields").GetProperty("pin").GetString())
    Assert.Equal("digital", root.GetProperty("medium").GetString())
    Assert.Equal(1, root.GetProperty("page").GetInt32())

// ---------------------------------------------------------------- the lifecycle

let private hold id state =
    { Id = id
      ItemId = Some("item-" + id)
      Title = "Title " + id
      Authors = []
      CoverUrl = None
      Url = None
      Medium = Physical
      Format = Some "book"
      State = Some state
      RawState = ""
      QueuePosition = None
      QueueSize = None
      PickupLocation = None
      ReadyUntil = None
      SuspendedUntil = None
      PlacedAt = None }

let private loan id itemId due =
    { Id = id
      ItemId = Some itemId
      Title = "Loan " + id
      Authors = []
      CoverUrl = None
      Url = None
      Medium = Physical
      Format = Some "book"
      DueAt = Some due
      Overdue = false
      Renewable = Some true }

let private snapshot holds loans = { Holds = holds; Loans = loans }

let private sync firstSync active snap =
    reconcile Settings.Default now firstSync active snap

/// Runs a sync and hands back the tracked state to feed into the next one.
let private carry (outcomes: Outcome list) =
    outcomes |> List.map (fun outcome -> outcome.After) |> List.filter (fun item -> item.State.IsActive)

let private eventsFor key (outcomes: Outcome list) =
    (outcomes |> List.find (fun outcome -> outcome.After.Key = key)).Events

let private stateOf key (outcomes: Outcome list) =
    (outcomes |> List.find (fun outcome -> outcome.After.Key = key)).After.State

[<Fact>]
let ``a hold walks the queue, the van, and the shelf, notifying at each real step`` () =
    let first = sync false [] (snapshot [ { hold "h1" ReportedPending with QueuePosition = Some 9 } ] [])
    Assert.Equal<Event list>([ HoldPlaced ], eventsFor "h1" first)

    let moved = sync false (carry first) (snapshot [ { hold "h1" ReportedPending with QueuePosition = Some 2 } ] [])
    Assert.Equal<Event list>([ QueueMoved(Some 9, Some 2) ], eventsFor "h1" moved)

    let transit = sync false (carry moved) (snapshot [ hold "h1" ReportedInTransit ] [])
    Assert.Equal<Event list>([ WentInTransit ], eventsFor "h1" transit)
    Assert.Equal(InTransit, stateOf "h1" transit)

    let ready =
        sync
            false
            (carry transit)
            (snapshot [ { hold "h1" ReportedReady with ReadyUntil = Some(days 7.0); PickupLocation = Some "Ballard" } ] [])

    Assert.Equal<Event list>([ BecameReady(Some(days 7.0), Some "Ballard") ], eventsFor "h1" ready)

    let categories = (moved @ transit @ ready) |> List.collect (fun o -> o.Events) |> List.choose (fun e -> e.Category)
    // Queue motion is recorded but never mailed; the van and the shelf are.
    Assert.Equal<string list>([ "hold-in-transit"; "hold-ready" ], categories)

[<Fact>]
let ``a ready hold that vanishes alongside a loan for the same item was collected`` () =
    let ready = sync false [] (snapshot [ hold "h1" ReportedReady ] [])
    let picked = sync false (carry ready) (snapshot [] [ loan "c1" "item-h1" (days 21.0) ])

    Assert.Equal(Collected, stateOf "h1" picked)
    Assert.Equal<Event list>([ WasCollected ], eventsFor "h1" picked)
    Assert.Equal<Event list>([ LoanStarted ], eventsFor "c1" picked)

[<Fact>]
let ``a ready hold that vanishes with no loan expired on the shelf`` () =
    let ready = sync false [] (snapshot [ hold "h1" ReportedReady ] [])
    let gone = sync false (carry ready) (snapshot [] [])

    Assert.Equal(Expired, stateOf "h1" gone)
    Assert.Equal(Some "hold-ended", (eventsFor "h1" gone).Head.Category)

[<Fact>]
let ``a queued hold that vanishes was closed by someone else, and that is worth saying`` () =
    let queued = sync false [] (snapshot [ hold "h1" ReportedPending ] [])
    let gone = sync false (carry queued) (snapshot [] [])

    Assert.Equal(Closed, stateOf "h1" gone)
    Assert.Equal(Some "hold-ended", (eventsFor "h1" gone).Head.Category)

[<Fact>]
let ``a loan that vanishes was returned`` () =
    let out = sync false [] (snapshot [] [ loan "c1" "i" (days 10.0) ])
    let back = sync false (carry out) (snapshot [] [])

    Assert.Equal(Returned, stateOf "c1" back)
    Assert.Equal<Event list>([ LoanReturned ], eventsFor "c1" back)

[<Fact>]
let ``the first sync records everything without announcing history`` () =
    let outcomes =
        sync true [] (snapshot [ hold "h1" ReportedInTransit; hold "h2" ReportedReady ] [ loan "c1" "i" (days 10.0) ])

    let categories = outcomes |> List.collect (fun o -> o.Events) |> List.choose (fun e -> e.Category)
    Assert.Empty categories
    Assert.Equal(Ready, stateOf "h2" outcomes)

[<Fact>]
let ``a pickup deadline is reminded about once, even on a first sync`` () =
    let readyTomorrow = { hold "h1" ReportedReady with ReadyUntil = Some(days 1.0) }
    let first = sync true [] (snapshot [ readyTomorrow ] [])

    Assert.Contains(PickupReminder(days 1.0), eventsFor "h1" first)

    let again = sync false (carry first) (snapshot [ readyTomorrow ] [])
    Assert.Empty(eventsFor "h1" again)

[<Fact>]
let ``becoming ready close to the deadline does not also send a reminder`` () =
    let queued = sync false [] (snapshot [ hold "h1" ReportedInTransit ] [])
    let readyTomorrow = { hold "h1" ReportedReady with ReadyUntil = Some(days 1.0) }
    let ready = sync false (carry queued) (snapshot [ readyTomorrow ] [])

    Assert.Equal<Event list>([ BecameReady(Some(days 1.0), None) ], eventsFor "h1" ready)
    // ...and the deadline counts as told, so the next sync is silent too.
    Assert.Empty(eventsFor "h1" (sync false (carry ready) (snapshot [ readyTomorrow ] [])))

[<Fact>]
let ``a renewal moves the due date and earns a fresh reminder`` () =
    let dueSoon = sync false [] (snapshot [] [ loan "c1" "i" (days 2.0) ])
    Assert.Contains(DueReminder(days 2.0), eventsFor "c1" dueSoon)

    let renewed = sync false (carry dueSoon) (snapshot [] [ loan "c1" "i" (days 23.0) ])
    Assert.Equal<Event list>([ LoanRenewed(Some(days 2.0), Some(days 23.0)) ], eventsFor "c1" renewed)

    // Three weeks later the new deadline is close; it has not been reminded about yet.
    let later = reconcile Settings.Default (days 21.0) false (carry renewed) (snapshot [] [ loan "c1" "i" (days 23.0) ])
    Assert.Contains(DueReminder(days 23.0), eventsFor "c1" later)

[<Fact>]
let ``a loan past its due date becomes overdue once`` () =
    let out = sync false [] (snapshot [] [ loan "c1" "i" (days 1.0) ])
    let late = reconcile Settings.Default (days 2.0) false (carry out) (snapshot [] [ loan "c1" "i" (days 1.0) ])

    Assert.Equal(Overdue, stateOf "c1" late)
    Assert.Equal<Event list>([ BecameOverdue ], eventsFor "c1" late)
    Assert.Equal(Some "overdue", BecameOverdue.Category)

    let still = reconcile Settings.Default (days 3.0) false (carry late) (snapshot [] [ loan "c1" "i" (days 1.0) ])
    Assert.Empty(eventsFor "c1" still)

[<Fact>]
let ``an unrecognized state keeps the last known state and says so`` () =
    let transit = sync false [] (snapshot [ hold "h1" ReportedInTransit ] [])
    let odd = { hold "h1" ReportedPending with State = None; RawState = "levitating" }
    let after = sync false (carry transit) (snapshot [ odd ] [])

    Assert.Equal(InTransit, stateOf "h1" after)
    Assert.Equal<Event list>([ StateUnrecognized "levitating" ], eventsFor "h1" after)

[<Fact>]
let ``suspending and resuming are recorded and never mailed`` () =
    let queued = sync false [] (snapshot [ hold "h1" ReportedPending ] [])
    let paused = sync false (carry queued) (snapshot [ { hold "h1" ReportedSuspended with SuspendedUntil = Some(days 30.0) } ] [])
    let resumed = sync false (carry paused) (snapshot [ hold "h1" ReportedPending ] [])

    Assert.Equal<Event list>([ WasSuspended(Some(days 30.0)) ], eventsFor "h1" paused)
    Assert.Equal<Event list>([ WasResumed ], eventsFor "h1" resumed)
    Assert.True(
        (paused @ resumed) |> List.collect (fun o -> o.Events) |> List.forall (fun e -> e.Category.IsNone)
    )

[<Fact>]
let ``a hold cancelled through Bindery is terminal and quiet`` () =
    let queued = sync false [] (snapshot [ hold "h1" ReportedPending ] [])
    let outcome = cancel (carry queued).Head

    Assert.Equal(Cancelled, outcome.After.State)
    Assert.False outcome.After.State.IsActive
    Assert.Equal(None, HoldCancelled.Category)

[<Fact>]
let ``a ready digital hold reads as borrow, not pickup`` () =
    Assert.Contains("borrow", describe Digital (BecameReady(Some(days 3.0), None)))
    Assert.Contains("pickup at Ballard", describe Physical (BecameReady(Some(days 3.0), Some "Ballard")))

[<Fact>]
let ``every tracked state round-trips through its wire name`` () =
    for state in TrackedState.all do
        Assert.Equal(Some state, TrackedState.ofWire state.Wire)
