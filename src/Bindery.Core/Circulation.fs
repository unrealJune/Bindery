/// Library circulation (protocol 3.8): what a plugin reports, and what the host concludes.
///
/// A download is one request with one outcome. A hold is a lifecycle that runs for weeks and
/// is only ever *observed*, never told: a plugin reports what an account looks like now, and
/// something has to decide that the hold which disappeared was collected rather than lapsed.
/// That something is `reconcile`, and it lives here — a pure function from (what we knew,
/// what we see, what time it is) to (what we now know, and what happened) — so every
/// circulation plugin shares one set of rules, and the rules are testable without a library,
/// a network, or a database.
///
/// See docs/CIRCULATION.md for the lifecycle as a diagram.
module Bindery.Core.Circulation

open System
open System.Text
open System.Text.Json
open Bindery.Core
open Bindery.Core.Protocol

// ---------------------------------------------------------------- wire: what plugins report

type AvailabilityStatus =
    | Available
    | Unavailable
    | OnOrder
    | NotHoldableStatus
    | LibraryUseOnly
    | UnknownAvailability

    member this.Wire =
        match this with
        | Available -> "available"
        | Unavailable -> "unavailable"
        | OnOrder -> "on_order"
        | NotHoldableStatus -> "not_holdable"
        | LibraryUseOnly -> "library_use_only"
        | UnknownAvailability -> "unknown"

module AvailabilityStatus =
    let ofWire (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "available" -> Available
        | "unavailable" -> Unavailable
        | "on_order" -> OnOrder
        | "not_holdable" -> NotHoldableStatus
        | "library_use_only" -> LibraryUseOnly
        | _ -> UnknownAvailability

type Availability =
    { Status: AvailabilityStatus
      Copies: int option
      AvailableCopies: int option
      Holds: int option
      OnOrder: int option }

type CatalogItem =
    { Id: string
      Title: string
      Subtitle: string option
      Authors: string list
      Year: string option
      Url: string option
      CoverUrl: string option
      Medium: Medium
      Format: string
      FormatLabel: string
      Provider: string option
      Availability: Availability
      Holdable: bool
      BorrowUrl: string option }

type SearchPage =
    { Total: int option
      Page: int
      Pages: int option
      Items: CatalogItem list }

type PickupLocation = { Id: string; Name: string }

type Verified =
    { LibraryId: string option
      LibraryName: string
      DisplayName: string option
      PickupLocations: PickupLocation list }

/// A hold's state as the plugin reports it. Deliberately the protocol's four states and no
/// more: everything else in the lifecycle (collected, expired, ...) is an *inference* the
/// host makes, never a claim a plugin gets to make.
type ReportedHoldState =
    | ReportedPending
    | ReportedInTransit
    | ReportedReady
    | ReportedSuspended

module ReportedHoldState =
    let ofWire (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "pending" -> Some ReportedPending
        | "in_transit" -> Some ReportedInTransit
        | "ready" -> Some ReportedReady
        | "suspended" -> Some ReportedSuspended
        | _ -> None

type ObservedHold =
    { Id: string
      ItemId: string option
      Title: string
      Authors: string list
      CoverUrl: string option
      Url: string option
      Medium: Medium
      Format: string option
      /// None when the plugin reported a state this build does not know. The host then keeps
      /// whatever state it already had rather than inventing one.
      State: ReportedHoldState option
      RawState: string
      QueuePosition: int option
      QueueSize: int option
      PickupLocation: string option
      ReadyUntil: DateTimeOffset option
      SuspendedUntil: DateTimeOffset option
      PlacedAt: DateTimeOffset option }

type ObservedLoan =
    { Id: string
      ItemId: string option
      Title: string
      Authors: string list
      CoverUrl: string option
      Url: string option
      Medium: Medium
      Format: string option
      DueAt: DateTimeOffset option
      Overdue: bool
      Renewable: bool option }

type Snapshot =
    { Holds: ObservedHold list
      Loans: ObservedLoan list }

/// Every circulation call answers either with its payload or with a protocol error. The
/// error is an answer, not a transport failure — `auth_required` from `verify` is how the
/// host learns a PIN was wrong.
type Reply<'T> =
    | Answered of 'T
    | Refused of ProtocolError

// ---------------------------------------------------------------- parsing

module private Read =
    let medium (element: JsonElement) =
        // A missing or unrecognized medium is read as physical: the protocol requires one,
        // and of the two mistakes, telling someone to go to a branch for an ebook is the one
        // they will notice and correct, rather than silently missing a pickup.
        Json.tryText "medium" element |> Option.bind Medium.ofWire |> Option.defaultValue Physical

    let nonNegative name element =
        Json.tryInt name element |> Option.filter (fun value -> value >= 0)

    /// RFC 3339 timestamps, or bare dates, which libraries overwhelmingly use for deadlines.
    let instant name element =
        match Json.tryTimestamp name element with
        | Some value -> Some value
        | None ->
            Json.tryText name element
            |> Option.bind (fun text ->
                match
                    DateTime.TryParseExact(
                        text,
                        "yyyy-MM-dd",
                        Globalization.CultureInfo.InvariantCulture,
                        Globalization.DateTimeStyles.AssumeUniversal ||| Globalization.DateTimeStyles.AdjustToUniversal
                    )
                with
                | true, date -> Some(DateTimeOffset(date, TimeSpan.Zero))
                | _ -> None)

    let location (element: JsonElement) =
        match Json.tryProp "pickupLocation" element with
        | Some location when location.ValueKind = JsonValueKind.Object -> Json.tryText "name" location
        | Some location when location.ValueKind = JsonValueKind.String -> Json.tryText "pickupLocation" element
        | _ -> None

    let availability (element: JsonElement) =
        match Json.tryProp "availability" element with
        | None ->
            { Status = UnknownAvailability
              Copies = None
              AvailableCopies = None
              Holds = None
              OnOrder = None }
        | Some value ->
            { Status = Json.tryText "status" value |> Option.map AvailabilityStatus.ofWire |> Option.defaultValue UnknownAvailability
              Copies = nonNegative "copies" value
              AvailableCopies = nonNegative "availableCopies" value
              Holds = nonNegative "holds" value
              OnOrder = nonNegative "onOrder" value }

    let item (element: JsonElement) : CatalogItem option =
        match Json.tryText "id" element, Json.tryText "title" element with
        | Some id, Some title ->
            let format = Json.stringOr "other" "format" element

            Some
                { Id = id
                  Title = title
                  Subtitle = Json.tryText "subtitle" element
                  Authors = Json.strings "authors" element
                  Year = Json.tryText "year" element
                  Url = Json.tryText "url" element
                  CoverUrl = Json.tryText "coverUrl" element
                  Medium = medium element
                  Format = format
                  FormatLabel = Json.stringOr format "formatLabel" element
                  Provider = Json.tryText "provider" element
                  Availability = availability element
                  Holdable = Json.boolOr false "holdable" element
                  BorrowUrl =
                    Json.tryText "borrowUrl" element
                    |> Option.filter (fun url -> url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) }
        | _ -> None

    let hold (element: JsonElement) : ObservedHold option =
        match Json.tryText "id" element, Json.tryText "title" element with
        | Some id, Some title ->
            let raw = Json.stringOr "" "state" element

            Some
                { Id = id
                  ItemId = Json.tryText "itemId" element
                  Title = title
                  Authors = Json.strings "authors" element
                  CoverUrl = Json.tryText "coverUrl" element
                  Url = Json.tryText "url" element
                  Medium = medium element
                  Format = Json.tryText "format" element
                  State = ReportedHoldState.ofWire raw
                  RawState = raw
                  QueuePosition = nonNegative "queuePosition" element
                  QueueSize = nonNegative "queueSize" element
                  PickupLocation = location element
                  ReadyUntil = instant "readyUntil" element
                  SuspendedUntil = instant "suspendedUntil" element
                  PlacedAt = instant "placedAt" element }
        | _ -> None

    let loan (element: JsonElement) : ObservedLoan option =
        match Json.tryText "id" element, Json.tryText "title" element with
        | Some id, Some title ->
            Some
                { Id = id
                  ItemId = Json.tryText "itemId" element
                  Title = title
                  Authors = Json.strings "authors" element
                  CoverUrl = Json.tryText "coverUrl" element
                  Url = Json.tryText "url" element
                  Medium = medium element
                  Format = Json.tryText "format" element
                  DueAt = instant "dueAt" element
                  Overdue = Json.boolOr false "overdue" element
                  Renewable = Json.tryBool "renewable" element }
        | _ -> None

    /// The envelope every circulation reply shares: `status: "error"` is an answer.
    let reply (text: string) (payload: JsonElement -> Result<'T, string>) : Result<Reply<'T>, string> =
        Json.parse text
        |> Result.bind (fun element ->
            match Json.stringOr "ok" "status" element with
            | "error" -> Ok(Refused(Parse.error element))
            | "ok" -> payload element |> Result.map Answered
            | other -> Error(sprintf "unknown circulation status '%s'" other))

module Parse =
    let verify (text: string) : Result<Reply<Verified>, string> =
        Read.reply text (fun element ->
            let library = Json.tryProp "library" element

            match library |> Option.bind (Json.tryText "name") with
            | None -> Error "verify response is missing library.name"
            | Some name ->
                Ok
                    { LibraryId = library |> Option.bind (Json.tryText "id")
                      LibraryName = name
                      DisplayName = Json.tryText "displayName" element
                      PickupLocations =
                        Json.items "pickupLocations" element
                        |> List.choose (fun location ->
                            match Json.tryText "id" location, Json.tryText "name" location with
                            | Some id, Some name -> Some { Id = id; Name = name }
                            | _ -> None) })

    let search (text: string) : Result<Reply<SearchPage>, string> =
        Read.reply text (fun element ->
            match Json.tryProp "items" element with
            | None -> Error "search response is missing 'items'"
            | Some _ ->
                Ok
                    { Total = Read.nonNegative "total" element
                      Page = Json.intOr 1 "page" element
                      Pages = Read.nonNegative "pages" element
                      Items = Json.items "items" element |> List.choose Read.item })

    /// A snapshot is complete or it is nothing (protocol 3.8). Both arrays are required, and
    /// a malformed entry fails the whole snapshot rather than being skipped: a silently
    /// dropped hold would be reconciled as "closed" and the person mailed about it.
    let snapshot (text: string) : Result<Reply<Snapshot>, string> =
        Read.reply text (fun element ->
            match Json.tryProp "holds" element, Json.tryProp "loans" element with
            | Some _, Some _ ->
                let rawHolds = Json.items "holds" element
                let rawLoans = Json.items "loans" element
                let holds = rawHolds |> List.choose Read.hold
                let loans = rawLoans |> List.choose Read.loan

                if holds.Length <> rawHolds.Length || loans.Length <> rawLoans.Length then
                    Error "snapshot contains an entry without an id or title; refusing a partial snapshot"
                else
                    Ok { Holds = holds; Loans = loans }
            | _ -> Error "snapshot response must carry both 'holds' and 'loans'")

    /// Placing or changing a hold. The hold in the reply is optional; the next snapshot is
    /// what the host believes.
    let holdChange (text: string) : Result<Reply<ObservedHold option>, string> =
        Read.reply text (fun element -> Ok(Json.tryProp "hold" element |> Option.bind Read.hold))

// ---------------------------------------------------------------- writing requests

type Account =
    { Id: string
      Fields: Map<string, string> }

module Request =
    let private write (build: Utf8JsonWriter -> unit) =
        use stream = new IO.MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        build writer
        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    let private account (writer: Utf8JsonWriter) (account: Account) =
        writer.WriteStartObject "account"
        writer.WriteString("id", account.Id)
        writer.WriteStartObject "fields"

        for KeyValue(key, value) in account.Fields do
            writer.WriteString(key, value)

        writer.WriteEndObject()
        writer.WriteEndObject()

    let verify (acct: Account) = write (fun writer -> account writer acct)

    let snapshot (acct: Account) = write (fun writer -> account writer acct)

    let search (acct: Account) (query: string) (medium: Medium option) (page: int) =
        write (fun writer ->
            account writer acct
            writer.WriteString("query", query)
            writer.WriteString("medium", medium |> Option.map (fun m -> m.Wire) |> Option.defaultValue "any")
            writer.WriteNumber("page", max 1 page))

    let placeHold (acct: Account) (itemId: string) (pickupLocationId: string option) =
        write (fun writer ->
            account writer acct
            writer.WriteString("itemId", itemId)
            Json.writeOptString writer "pickupLocationId" pickupLocationId)

    let holdOperation (acct: Account) (until: DateTimeOffset option) =
        write (fun writer ->
            account writer acct

            match until with
            | Some value -> writer.WriteString("until", value.UtcDateTime.ToString("yyyy-MM-dd"))
            | None -> ())

// ---------------------------------------------------------------- the lifecycle

type TrackedKind =
    | HoldKind
    | LoanKind

    member this.Wire =
        match this with
        | HoldKind -> "hold"
        | LoanKind -> "loan"

/// Every state a tracked hold or loan can be in, as the host knows it.
///
/// One union for both kinds because the host stores them in one table and shows them on one
/// board; `Kind` says which states are reachable. The terminal cases are what make this more
/// than the plugin's four states: they are conclusions the host draws from disappearance.
type TrackedState =
    // holds, active
    | Pending
    | InTransit
    | Ready
    | Suspended
    // loans, active
    | CheckedOut
    | Overdue
    // terminal
    | Collected
    | Expired
    | Cancelled
    | Closed
    | Returned

    member this.IsActive =
        match this with
        | Pending
        | InTransit
        | Ready
        | Suspended
        | CheckedOut
        | Overdue -> true
        | Collected
        | Expired
        | Cancelled
        | Closed
        | Returned -> false

    member this.Wire =
        match this with
        | Pending -> "pending"
        | InTransit -> "in_transit"
        | Ready -> "ready"
        | Suspended -> "suspended"
        | CheckedOut -> "checked_out"
        | Overdue -> "overdue"
        | Collected -> "collected"
        | Expired -> "expired"
        | Cancelled -> "cancelled"
        | Closed -> "closed"
        | Returned -> "returned"

module TrackedState =
    let all =
        [ Pending; InTransit; Ready; Suspended; CheckedOut; Overdue; Collected; Expired; Cancelled; Closed; Returned ]

    let ofWire (value: string) =
        all |> List.tryFind (fun state -> state.Wire = value)

    let ofReported reported =
        match reported with
        | ReportedPending -> Pending
        | ReportedInTransit -> InTransit
        | ReportedReady -> Ready
        | ReportedSuspended -> Suspended

/// What the host remembers about one hold or loan between syncs.
type Tracked =
    { Key: string
      Kind: TrackedKind
      State: TrackedState
      ItemId: string option
      Title: string
      Medium: Medium
      QueuePosition: int option
      QueueSize: int option
      PickupLocation: string option
      ReadyUntil: DateTimeOffset option
      SuspendedUntil: DateTimeOffset option
      DueAt: DateTimeOffset option
      /// The deadline a reminder was last sent for. A reminder fires once per deadline; a
      /// renewal moves the deadline and so earns a fresh one.
      RemindedFor: DateTimeOffset option }

/// Something that happened, as the host concluded it.
type Event =
    | HoldPlaced
    | QueueMoved of fromPosition: int option * toPosition: int option
    | WentInTransit
    | BecameReady of until: DateTimeOffset option * location: string option
    | WasSuspended of until: DateTimeOffset option
    | WasResumed
    | WasCollected
    | HoldExpired
    | HoldClosed
    | HoldCancelled
    /// A move the lifecycle does not name, e.g. a library putting a ready hold back in the
    /// queue. Recorded faithfully rather than squeezed into a case it is not.
    | Moved of fromState: TrackedState * toState: TrackedState
    | StateUnrecognized of raw: string
    | LoanStarted
    | LoanRenewed of fromDue: DateTimeOffset option * toDue: DateTimeOffset option
    | BecameOverdue
    | LoanReturned
    | PickupReminder of until: DateTimeOffset
    | DueReminder of due: DateTimeOffset

    member this.Wire =
        match this with
        | HoldPlaced -> "hold-placed"
        | QueueMoved _ -> "queue-moved"
        | WentInTransit -> "in-transit"
        | BecameReady _ -> "ready"
        | WasSuspended _ -> "suspended"
        | WasResumed -> "resumed"
        | WasCollected -> "collected"
        | HoldExpired -> "expired"
        | HoldClosed -> "closed"
        | HoldCancelled -> "cancelled"
        | Moved _ -> "moved"
        | StateUnrecognized _ -> "state-unrecognized"
        | LoanStarted -> "checked-out"
        | LoanRenewed _ -> "renewed"
        | BecameOverdue -> "overdue"
        | LoanReturned -> "returned"
        | PickupReminder _ -> "pickup-reminder"
        | DueReminder _ -> "due-reminder"

    /// The notification category this event belongs to, or None for events that are
    /// recorded but never worth interrupting someone over. The categories are the host's
    /// notification vocabulary (docs/CIRCULATION.md §4); a person can mute each one.
    member this.Category =
        match this with
        | WentInTransit -> Some "hold-in-transit"
        | BecameReady _ -> Some "hold-ready"
        | PickupReminder _ -> Some "pickup-reminder"
        | HoldExpired
        | HoldClosed -> Some "hold-ended"
        | DueReminder _ -> Some "due-reminder"
        | BecameOverdue -> Some "overdue"
        // The person did these themselves, or they are routine motion.
        | HoldPlaced
        | QueueMoved _
        | WasSuspended _
        | WasResumed
        | WasCollected
        | HoldCancelled
        | Moved _
        | StateUnrecognized _
        | LoanStarted
        | LoanRenewed _
        | LoanReturned -> None

/// One tracked item's result from a sync: what it was, what it is, and why.
type Outcome =
    { Before: Tracked option
      After: Tracked
      Events: Event list }

    member this.Changed =
        not this.Events.IsEmpty || this.Before <> Some this.After

type Settings =
    { PickupReminder: TimeSpan
      DueReminder: TimeSpan }

    static member Default =
        { PickupReminder = TimeSpan.FromHours 48.0
          DueReminder = TimeSpan.FromDays 3.0 }

module private Lifecycle =
    let holdState (previous: TrackedState option) (observed: ObservedHold) =
        match observed.State, previous with
        | Some reported, _ -> TrackedState.ofReported reported
        // Unknown state: keep what we had, or start conservatively in the queue.
        | None, Some state -> state
        | None, None -> Pending

    let loanState (now: DateTimeOffset) (observed: ObservedLoan) =
        let pastDue =
            match observed.DueAt with
            | Some due -> due < now
            | None -> false

        if observed.Overdue || pastDue then Overdue else CheckedOut

    let trackHold (state: TrackedState) (remindedFor: DateTimeOffset option) (observed: ObservedHold) =
        { Key = observed.Id
          Kind = HoldKind
          State = state
          ItemId = observed.ItemId
          Title = observed.Title
          Medium = observed.Medium
          QueuePosition = observed.QueuePosition
          QueueSize = observed.QueueSize
          PickupLocation = observed.PickupLocation
          ReadyUntil = observed.ReadyUntil
          SuspendedUntil = observed.SuspendedUntil
          DueAt = None
          RemindedFor = remindedFor }

    let trackLoan (state: TrackedState) (remindedFor: DateTimeOffset option) (observed: ObservedLoan) =
        { Key = observed.Id
          Kind = LoanKind
          State = state
          ItemId = observed.ItemId
          Title = observed.Title
          Medium = observed.Medium
          QueuePosition = None
          QueueSize = None
          PickupLocation = None
          ReadyUntil = None
          SuspendedUntil = None
          DueAt = observed.DueAt
          RemindedFor = remindedFor }

    /// The hold-side events for a move from one state to another.
    let holdTransition (before: Tracked) (after: Tracked) =
        match before.State, after.State with
        | a, b when a = b ->
            if a = Pending && before.QueuePosition <> after.QueuePosition then
                [ QueueMoved(before.QueuePosition, after.QueuePosition) ]
            else
                []
        | _, InTransit -> [ WentInTransit ]
        | _, Ready -> [ BecameReady(after.ReadyUntil, after.PickupLocation) ]
        | _, Suspended -> [ WasSuspended after.SuspendedUntil ]
        | Suspended, Pending -> [ WasResumed ]
        | a, b -> [ Moved(a, b) ]

    let loanTransition (before: Tracked) (after: Tracked) =
        let renewed =
            match before.DueAt, after.DueAt with
            | Some a, Some b when b > a -> [ LoanRenewed(before.DueAt, after.DueAt) ]
            | _ -> []

        let overdue =
            match before.State, after.State with
            | CheckedOut, Overdue -> [ BecameOverdue ]
            | _ -> []

        renewed @ overdue

    /// A reminder for a deadline inside its window that has not been reminded about yet.
    /// `alreadyTold` covers the sync in which the item *became* ready: that notification
    /// carries the deadline itself, so a reminder in the same breath would be a duplicate.
    let remind (settings: Settings) (now: DateTimeOffset) (alreadyTold: bool) (item: Tracked) =
        let within (window: TimeSpan) (deadline: DateTimeOffset) = deadline > now && deadline - now <= window

        match item.State, item.ReadyUntil, item.DueAt with
        | Ready, Some until, _ when within settings.PickupReminder until && item.RemindedFor <> Some until ->
            let item = { item with RemindedFor = Some until }
            item, (if alreadyTold then [] else [ PickupReminder until ])
        | CheckedOut, _, Some due when within settings.DueReminder due && item.RemindedFor <> Some due ->
            { item with RemindedFor = Some due }, [ DueReminder due ]
        | _ -> item, []

    let unrecognized (observed: ObservedHold) =
        match observed.State with
        | None when observed.RawState <> "" -> [ StateUnrecognized observed.RawState ]
        | _ -> []

/// Reconciles what the host knew about an account with a complete snapshot of it.
///
/// `active` is every tracked item for the account that is still in an active state.
/// `firstSync` is true the first time an account is ever synced: everything is new then,
/// and announcing that a hold placed months ago "went in transit" would be noise — so new
/// items on a first sync are recorded without notification-worthy events. Reminders still
/// fire, because a book that must be collected tomorrow is worth hearing about however long
/// Bindery has known about it.
///
/// The result has one outcome per item that exists or existed: every observed hold and
/// loan, and every active item that vanished. Unchanged items are included (with no
/// events) so the caller can refresh "last seen" without a second pass.
let reconcile
    (settings: Settings)
    (now: DateTimeOffset)
    (firstSync: bool)
    (active: Tracked list)
    (snapshot: Snapshot)
    : Outcome list =
    let find kind key =
        active |> List.tryFind (fun item -> item.Kind = kind && item.Key = key)

    let holds = snapshot.Holds |> List.distinctBy (fun hold -> hold.Id)
    let loans = snapshot.Loans |> List.distinctBy (fun loan -> loan.Id)

    let holdOutcomes =
        holds
        |> List.map (fun observed ->
            match find HoldKind observed.Id with
            | None ->
                let state = Lifecycle.holdState None observed
                let fresh = Lifecycle.trackHold state None observed

                let arrival =
                    match firstSync, state with
                    | true, _ -> []
                    | false, InTransit -> [ WentInTransit ]
                    | false, Ready -> [ BecameReady(fresh.ReadyUntil, fresh.PickupLocation) ]
                    | false, _ -> []

                let told = not arrival.IsEmpty
                let after, reminders = Lifecycle.remind settings now told fresh

                { Before = None
                  After = after
                  Events = (HoldPlaced :: arrival) @ reminders @ Lifecycle.unrecognized observed }
            | Some before ->
                let state = Lifecycle.holdState (Some before.State) observed
                let next = Lifecycle.trackHold state before.RemindedFor observed
                let transition = Lifecycle.holdTransition before next

                let told =
                    transition
                    |> List.exists (function
                        | BecameReady _ -> true
                        | _ -> false)

                let after, reminders = Lifecycle.remind settings now told next

                { Before = Some before
                  After = after
                  Events = transition @ reminders @ Lifecycle.unrecognized observed })

    let loanOutcomes =
        loans
        |> List.map (fun observed ->
            let state = Lifecycle.loanState now observed

            match find LoanKind observed.Id with
            | None ->
                let after, reminders = Lifecycle.remind settings now false (Lifecycle.trackLoan state None observed)

                let overdue = if state = Overdue && not firstSync then [ BecameOverdue ] else []

                { Before = None
                  After = after
                  Events = (LoanStarted :: overdue) @ reminders }
            | Some before ->
                let next = Lifecycle.trackLoan state before.RemindedFor observed
                let after, reminders = Lifecycle.remind settings now false next

                { Before = Some before
                  After = after
                  Events = Lifecycle.loanTransition before next @ reminders })

    let seenHolds = holds |> List.map (fun hold -> hold.Id) |> Set.ofList
    let seenLoans = loans |> List.map (fun loan -> loan.Id) |> Set.ofList
    let loanedItems = loans |> List.choose (fun loan -> loan.ItemId) |> Set.ofList

    let vanished =
        active
        |> List.filter (fun item ->
            item.State.IsActive
            && match item.Kind with
               | HoldKind -> not (seenHolds.Contains item.Key)
               | LoanKind -> not (seenLoans.Contains item.Key))
        |> List.map (fun item ->
            let state, event =
                match item.Kind with
                | LoanKind -> Returned, LoanReturned
                | HoldKind ->
                    match item.ItemId with
                    | Some itemId when loanedItems.Contains itemId -> Collected, WasCollected
                    | _ when item.State = Ready -> Expired, HoldExpired
                    | _ -> Closed, HoldClosed

            { Before = Some item
              After = { item with State = state }
              Events = [ event ] })

    holdOutcomes @ loanOutcomes @ vanished

/// A hold cancelled through Bindery. Recorded at once, so the next snapshot's absence is not
/// misread as the library closing it — and so nobody is mailed about their own click.
let cancel (item: Tracked) : Outcome =
    { Before = Some item
      After = { item with State = Cancelled }
      Events = [ HoldCancelled ] }

/// One line of plain text describing an event, for the history list and notification bodies.
let describe (medium: Medium) (event: Event) : string =
    let date (value: DateTimeOffset) = value.UtcDateTime.ToString("ddd d MMM yyyy", Globalization.CultureInfo.InvariantCulture)

    match event with
    | HoldPlaced -> "Hold placed."
    | QueueMoved(Some a, Some b) -> sprintf "Moved from #%d to #%d in the queue." a b
    | QueueMoved(_, Some b) -> sprintf "Now #%d in the queue." b
    | QueueMoved _ -> "Queue position changed."
    | WentInTransit ->
        match medium with
        | Physical -> "A copy is on its way to your pickup branch."
        | Digital -> "Being prepared for you."
    | BecameReady(until, location) ->
        let deadline =
            until |> Option.map (fun value -> sprintf " by %s" (date value)) |> Option.defaultValue ""

        match medium, location with
        | Physical, Some place -> sprintf "Ready for pickup at %s%s." place deadline
        | Physical, None -> sprintf "Ready for pickup%s." deadline
        | Digital, _ -> sprintf "Ready to borrow%s." deadline
    | WasSuspended(Some until) -> sprintf "Paused until %s." (date until)
    | WasSuspended None -> "Paused."
    | WasResumed -> "Resumed — back in the queue."
    | WasCollected ->
        match medium with
        | Physical -> "Picked up."
        | Digital -> "Borrowed."
    | HoldExpired ->
        match medium with
        | Physical -> "Not picked up in time; the hold has ended."
        | Digital -> "Not borrowed in time; the hold has ended."
    | HoldClosed -> "No longer on your account — cancelled on the library's site or by the library."
    | HoldCancelled -> "Cancelled."
    | Moved(a, b) -> sprintf "Changed from %s to %s." a.Wire b.Wire
    | StateUnrecognized raw -> sprintf "The library reported a state Bindery does not know (%s)." raw
    | LoanStarted -> "Checked out."
    | LoanRenewed(_, Some due) -> sprintf "Renewed; now due %s." (date due)
    | LoanRenewed _ -> "Renewed."
    | BecameOverdue -> "Overdue."
    | LoanReturned -> "Returned."
    | PickupReminder until ->
        match medium with
        | Physical -> sprintf "Waiting for you — pick it up by %s." (date until)
        | Digital -> sprintf "Waiting for you — borrow it by %s." (date until)
    | DueReminder due -> sprintf "Due back %s." (date due)
