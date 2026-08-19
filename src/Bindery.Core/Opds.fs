/// The OPDS feed model, shared by the 1.2 (Atom) and 2.0 (JSON) serializers.
///
/// Feeds are described once as data and rendered twice. That is the whole reason the two
/// formats stay in agreement: there is no second place to forget a field.
module Bindery.Core.Opds

open System
open Bindery.Core.Domain

/// Content types. These are not decoration — ereaders genuinely break on the wrong one,
/// and `kind=navigation` versus `kind=acquisition` is the single most common way to ship
/// a catalog that validates and does not work.
module ContentTypes =
    [<Literal>]
    let Navigation = "application/atom+xml;profile=opds-catalog;kind=navigation"

    [<Literal>]
    let Acquisition = "application/atom+xml;profile=opds-catalog;kind=acquisition"

    [<Literal>]
    let Entry = "application/atom+xml;type=entry;profile=opds-catalog"

    [<Literal>]
    let Opds2 = "application/opds+json"

    [<Literal>]
    let OpenSearch = "application/opensearchdescription+xml"

module Rel =
    [<Literal>]
    let SelfRel = "self"

    [<Literal>]
    let Start = "start"

    [<Literal>]
    let Up = "up"

    [<Literal>]
    let Next = "next"

    [<Literal>]
    let Previous = "previous"

    [<Literal>]
    let First = "first"

    [<Literal>]
    let Last = "last"

    [<Literal>]
    let Search = "search"

    [<Literal>]
    let Alternate = "alternate"

    [<Literal>]
    let Acquisition = "http://opds-spec.org/acquisition"

    [<Literal>]
    let OpenAccess = "http://opds-spec.org/acquisition/open-access"

    [<Literal>]
    let Image = "http://opds-spec.org/image"

    [<Literal>]
    let Thumbnail = "http://opds-spec.org/image/thumbnail"

    [<Literal>]
    let Facet = "http://opds-spec.org/facet"

    [<Literal>]
    let Sort = "http://opds-spec.org/sort/new"

type FeedKind =
    | Navigation
    | Acquisition

    member this.ContentType =
        match this with
        | Navigation -> ContentTypes.Navigation
        | Acquisition -> ContentTypes.Acquisition

type Link =
    { Rel: string
      Href: string
      Type: string
      Title: string option
      /// Atom's `length`, in bytes. Ereaders show it as a download size.
      Length: int64 option
      /// `thr:count`, how many entries lie behind a navigation link.
      Count: int option
      /// Facet grouping label, for `rel="...facet"` links.
      FacetGroup: string option
      Active: bool }

    static member Of(rel, href, contentType) =
        { Rel = rel
          Href = href
          Type = contentType
          Title = None
          Length = None
          Count = None
          FacetGroup = None
          Active = false }

type EntryAuthor = { Name: string; Uri: string option }

type Category = { Term: string; Label: string option; Scheme: string option }

type Entry =
    { Id: string
      Title: string
      Updated: DateTimeOffset
      Published: DateTimeOffset option
      Authors: EntryAuthor list
      Summary: string option
      Content: string option
      Categories: Category list
      Language: string option
      Publisher: string option
      Series: SeriesRef option
      /// Set for book entries; drives OPDS 2.0's publication shape.
      Identifier: string option
      Links: Link list }

    static member Create(id, title, updated) =
        { Id = id
          Title = title
          Updated = updated
          Published = None
          Authors = []
          Summary = None
          Content = None
          Categories = []
          Language = None
          Publisher = None
          Series = None
          Identifier = None
          Links = [] }

/// OpenSearch paging, echoed into both formats so clients can page consistently.
type Paging =
    { TotalResults: int
      ItemsPerPage: int
      /// 1-based, as OpenSearch specifies.
      StartIndex: int }

    member this.PageIndex = (this.StartIndex - 1) / max 1 this.ItemsPerPage
    member this.PageCount = max 1 ((this.TotalResults + this.ItemsPerPage - 1) / max 1 this.ItemsPerPage)

type Feed =
    { Id: string
      Title: string
      Subtitle: string option
      Updated: DateTimeOffset
      Kind: FeedKind
      Icon: string option
      Author: EntryAuthor option
      Links: Link list
      Entries: Entry list
      Paging: Paging option }

    static member Create(id, title, kind, updated) =
        { Id = id
          Title = title
          Subtitle = None
          Updated = updated
          Kind = kind
          Icon = None
          Author = None
          Links = []
          Entries = []
          Paging = None }

// ---------------------------------------------------------------- building

/// Where things live. Supplied by the host so this library knows nothing about routing.
[<NoComparison; NoEquality>]
type UrlBuilder =
    { Root: string
      Feed: string -> string
      Book: Guid -> string
      Download: Guid -> string -> string
      Cover: Guid -> string
      Thumbnail: Guid -> string
      Search: string -> string
      OpenSearch: string }

module Urn =
    /// Stable, absolute entry ids. Clients use them for deduplication and read state, so
    /// they must not change when the deployment's hostname does.
    let book (id: Guid) = sprintf "urn:bindery:book:%s" (id.ToString "N")
    let feed (path: string) = sprintf "urn:bindery:feed:%s" (path.Trim '/')

    let grouping (kind: string) (key: string) =
        sprintf "urn:bindery:%s:%s" kind (Uri.EscapeDataString key)

module Build =
    let private tagCategory (tag: string) =
        { Term = tag; Label = Some(Summary.titleCaseTag tag); Scheme = None }

    let private seriesCategory (series: SeriesRef) =
        { Term = series.Name
          Label = Some series.Name
          Scheme = Some "http://bindery.local/series" }

    /// A description that survives every client: plain text, bounded, and carrying the
    /// facts a reader picks by — series position, length, source.
    let describe (book: Book) =
        let parts =
            [ match book.Series with
              | Some series ->
                  match series.Index with
                  | Some index -> yield sprintf "%s, book %g" series.Name index
                  | None -> yield series.Name
              | None -> ()

              match book.Chapters with
              | Some chapters when chapters > 0 -> yield sprintf "%d chapter%s" chapters (if chapters = 1 then "" else "s")
              | _ -> ()

              match book.Summary with
              | Some summary when summary.Trim() <> "" -> yield Summary.truncate 1200 (Summary.plain summary)
              | _ -> () ]

        match parts with
        | [] -> None
        | parts -> Some(String.Join(" · ", parts))

    /// One acquisition entry per book, with a link per available format.
    let bookEntry (urls: UrlBuilder) (book: Book) =
        let acquisitionLinks =
            book.Files
            |> List.sortBy (fun file ->
                match Formats.preferred |> List.tryFindIndex ((=) file.Format) with
                | Some index -> index
                | None -> Int32.MaxValue)
            |> List.map (fun file ->
                { Link.Of(Rel.OpenAccess, urls.Download book.Id file.Format, file.ContentType) with
                    Title = Some(file.Format.ToUpperInvariant())
                    Length = Some file.SizeBytes })

        let imageLinks =
            if book.HasCover then
                [ Link.Of(Rel.Image, urls.Cover book.Id, "image/jpeg")
                  Link.Of(Rel.Thumbnail, urls.Thumbnail book.Id, "image/jpeg") ]
            else
                []

        let alternate =
            match book.SourceUrl with
            | Some source -> [ { Link.Of(Rel.Alternate, source, "text/html") with Title = Some "Source" } ]
            | None -> []

        { Entry.Create(Urn.book book.Id, book.Title, book.Updated) with
            Published = book.Published
            Authors = book.Authors |> List.map (fun author -> { Name = author.Name; Uri = None })
            Summary = describe book
            Categories =
                (book.Tags |> List.map tagCategory)
                @ (book.Series |> Option.map seriesCategory |> Option.toList)
            Language = book.Language
            Publisher = book.SourcePlugin
            Series = book.Series
            Identifier = Some(Urn.book book.Id)
            Links = acquisitionLinks @ imageLinks @ alternate }

    /// A navigation entry: a link to another feed, with a count when we know one.
    let navigationEntry (id: string) (title: string) (href: string) (kind: FeedKind) (subtitle: string option) (count: int option) (updated: DateTimeOffset) =
        { Entry.Create(id, title, updated) with
            Content = subtitle
            Links = [ { Link.Of("subsection", href, kind.ContentType) with Count = count } ] }

    /// Self, start, and up — the three links every feed needs and half of them omit.
    let standardLinks (urls: UrlBuilder) (selfHref: string) (kind: FeedKind) (upHref: string option) =
        [ { Link.Of(Rel.SelfRel, selfHref, kind.ContentType) with Title = None }
          { Link.Of(Rel.Start, urls.Root, ContentTypes.Navigation) with Title = Some "Catalog" }
          { Link.Of(Rel.Search, urls.OpenSearch, ContentTypes.OpenSearch) with Title = Some "Search" }
          match upHref with
          | Some href -> { Link.Of(Rel.Up, href, ContentTypes.Navigation) with Title = None }
          | None -> () ]

    /// first/previous/next/last for a paged acquisition feed.
    ///
    /// `hrefForPage` takes a zero-based page number. Omitting `next` on the final page is
    /// not cosmetic: several readers page forever if you always emit it.
    let pagingLinks (paging: Paging) (contentType: string) (hrefForPage: int -> string) =
        let page = paging.PageIndex
        let last = paging.PageCount - 1

        [ if last > 0 then
              yield Link.Of(Rel.First, hrefForPage 0, contentType)
              yield Link.Of(Rel.Last, hrefForPage last, contentType)
          if page > 0 then
              yield Link.Of(Rel.Previous, hrefForPage (page - 1), contentType)
          if page < last then
              yield Link.Of(Rel.Next, hrefForPage (page + 1), contentType) ]
