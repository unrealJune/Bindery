/// Is this the same work? Deciding when two books, or a book and a download, are one.
///
/// A book can be acquired from several places — Royal Road through FanFicFare, the author's
/// own WordPress site, a Discord advance copy — and each plugin names it its own way. This
/// module is the rule for recognising them as one work. It is a pure function of titles,
/// authors, and source identities, so the host never re-derives "duplicate" in C#.
///
/// Two strengths, on purpose:
///
/// - **Same source** — a shared source URL or source id. Certain: the host files the
///   download into the existing book without asking.
/// - **Probable** — titles (and ideally an author) agree. Never acted on automatically; the
///   person is shown the match and decides whether to merge. Two different serials can share
///   a title, and merging is destructive.
module Bindery.Core.WorkIdentity

open System
open System.Globalization
open System.Text

/// A work as identity sees it: what it is called, who wrote it, and where it comes from.
type Work =
    { Id: Guid
      Title: string
      Authors: string list
      /// Every source URL known for the work, in any form; normalized here.
      SourceUrls: string list
      /// Every plugin-reported source id (`ao3:123`, `wpserial:host/path`).
      SourceIds: string list }

type Likeness =
    | SameSource of reason: string
    | Probable of score: float * reason: string

module Likeness =
    let score =
        function
        | SameSource _ -> 1.0
        | Probable(score, _) -> score

    let reason =
        function
        | SameSource reason -> reason
        | Probable(_, reason) -> reason

    let isCertain =
        function
        | SameSource _ -> true
        | Probable _ -> false

/// Below this a probable match is noise and not shown.
[<Literal>]
let Threshold = 0.5

/// Accented Latin letters folded to their base letter. Spelled out rather than asked of
/// `String.Normalize`, because Bindery runs with invariant globalization, where Unicode
/// decomposition is not available and Normalize returns its input unchanged.
let private accents =
    [ "àáâãäåāăą", 'a'
      "çćĉċč", 'c'
      "ďđ", 'd'
      "èéêëēĕėęě", 'e'
      "ĝğġģ", 'g'
      "ĥħ", 'h'
      "ìíîïĩīĭįı", 'i'
      "ĵ", 'j'
      "ķ", 'k'
      "ĺļľŀł", 'l'
      "ñńņňŉ", 'n'
      "òóôõöøōŏő", 'o'
      "ŕŗř", 'r'
      "śŝşšß", 's'
      "ţťŧ", 't'
      "ùúûüũūŭůűų", 'u'
      "ŵ", 'w'
      "ýÿŷ", 'y'
      "źżž", 'z' ]
    |> List.collect (fun (letters, baseLetter) -> [ for ch in letters -> ch, baseLetter ])
    |> dict

let private foldText (text: string) =
    let source = string text
    let builder = StringBuilder(source.Length)

    for ch in source do
        let lower = Char.ToLowerInvariant ch

        match accents.TryGetValue lower with
        | true, folded -> builder.Append folded |> ignore
        | _ when CharUnicodeInfo.GetUnicodeCategory lower = UnicodeCategory.NonSpacingMark -> ()
        | _ when Char.IsLetterOrDigit lower -> builder.Append lower |> ignore
        | _ -> builder.Append ' ' |> ignore

    builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

/// Lowercase, accents and punctuation gone, leading article dropped:
/// "The Wandering Inn!" and "wandering inn" are one title.
let normalizeTitle (title: string) =
    match foldText title with
    | ("the" | "a" | "an") :: (_ :: _ as rest) -> String.Join(" ", rest)
    | words -> String.Join(" ", words)

/// Author names compared as folded word sets, so "Young, Hazel" and "Hazel Young" agree.
let normalizeName (name: string) =
    foldText name |> List.sort |> fun words -> String.Join(" ", words)

/// Identity of a URL: no scheme, no `www.`, no fragment, no trailing slash, no tracking
/// parameters, lowercase. Not a canonicalisation for fetching — only for comparing.
let normalizeUrl (url: string) =
    if String.IsNullOrWhiteSpace url then
        ""
    else
        let parsed =
            match Uri.TryCreate(url.Trim(), UriKind.Absolute) with
            | true, uri -> Option.ofObj uri
            | _ -> None

        match parsed with
        | Some uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps ->
            let host =
                let h = uri.Host.ToLowerInvariant()
                if h.StartsWith "www." then h.Substring 4 else h

            let path = uri.AbsolutePath.TrimEnd('/')

            let query =
                uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                |> Array.filter (fun pair ->
                    let key = (pair.Split('=').[0]).ToLowerInvariant()
                    not (key.StartsWith "utm_" || key = "ref" || key = "fbclid" || key = "share"))
                |> Array.sort

            let query = if query.Length = 0 then "" else "?" + String.Join("&", query)
            (host + path + query).ToLowerInvariant()
        | _ -> url.Trim().ToLowerInvariant()

let private words (title: string) =
    title.Split(' ', StringSplitOptions.RemoveEmptyEntries)

/// One title is the other plus a subtitle: "Maidens of the Fall" vs
/// "Maidens of the Fall a magical girl grimdark". Whole words only, and the shorter one
/// must be long enough to mean something on its own.
let private extends (shorter: string) (longer: string) =
    shorter.Length >= 12
    && words shorter |> Array.length >= 2
    && longer.Length > shorter.Length
    && longer.StartsWith(shorter + " ", StringComparison.Ordinal)

/// How alike two works are, or None when they are not alike enough to mention.
let likeness (a: Work) (b: Work) : Likeness option =
    let urls (work: Work) =
        work.SourceUrls |> List.map normalizeUrl |> List.filter (fun u -> u <> "") |> Set.ofList

    let ids (work: Work) =
        work.SourceIds
        |> List.filter (String.IsNullOrWhiteSpace >> not)
        |> List.map (fun id -> id.Trim())
        |> Set.ofList

    let sharedUrl = Set.intersect (urls a) (urls b)
    let sharedId = Set.intersect (ids a) (ids b)

    if not sharedUrl.IsEmpty then
        Some(SameSource $"both come from {Seq.head sharedUrl}")
    elif not sharedId.IsEmpty then
        Some(SameSource $"both are {Seq.head sharedId}")
    else
        let ta, tb = normalizeTitle a.Title, normalizeTitle b.Title

        let authors (work: Work) =
            work.Authors |> List.map normalizeName |> List.filter (fun n -> n <> "") |> Set.ofList

        let sharedAuthor = not (Set.intersect (authors a) (authors b)).IsEmpty

        let candidate =
            if ta = "" || tb = "" then None
            elif ta = tb && sharedAuthor then Some(Probable(0.95, "same title and author"))
            elif ta = tb then Some(Probable(0.75, "same title, different author names"))
            elif (extends ta tb || extends tb ta) && sharedAuthor then
                Some(Probable(0.7, "one title extends the other; same author"))
            elif extends ta tb || extends tb ta then
                Some(Probable(0.5, "one title extends the other"))
            else
                None

        candidate |> Option.filter (fun found -> Likeness.score found >= Threshold)

/// Every other work in the library that looks like this one, strongest first.
let duplicatesOf (work: Work) (library: Work seq) : (Work * Likeness) list =
    library
    |> Seq.filter (fun other -> other.Id <> work.Id)
    |> Seq.choose (fun other -> likeness work other |> Option.map (fun found -> other, found))
    |> Seq.sortByDescending (fun (other, found) -> Likeness.score found, other.Title)
    |> List.ofSeq
