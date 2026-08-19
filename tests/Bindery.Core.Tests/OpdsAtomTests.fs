module Bindery.Core.Tests.OpdsAtomTests

open System
open System.Xml.Linq
open Xunit
open Bindery.Core
open Bindery.Core.Opds
open Bindery.Core.Tests.Fixtures

let private atom = XNamespace.Get "http://www.w3.org/2005/Atom"
let private dcterms = XNamespace.Get "http://purl.org/dc/terms/"
let private opensearch = XNamespace.Get "http://a9.com/-/spec/opensearch/1.1/"

let private acquisitionFeed () =
    { Feed.Create(Urn.feed "new", "Recently added", Acquisition, at "2026-08-10T12:30:00Z") with
        Links = Build.standardLinks urls "/opds/new" Acquisition (Some "/opds")
        Entries = [ Build.bookEntry urls book; Build.bookEntry urls minimalBook ] }

let private parse (xml: string) = XDocument.Parse xml

/// Written as a code point rather than a literal: a raw control character in a source
/// file is invisible in review and does not survive every editor.
let private control (code: int) = string (char code)

// ---------------------------------------------------------------- the basics

[<Fact>]
let ``a feed declares utf-8 and parses`` () =
    let xml = OpdsAtom.render (acquisitionFeed ())

    Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml)
    let document = parse xml
    Assert.Equal(atom + "feed", document.Root.Name)

[<Fact>]
let ``the two feed kinds have the content types ereaders check`` () =
    // Getting these backwards produces a catalog that validates and does not work.
    Assert.Equal("application/atom+xml;profile=opds-catalog;kind=navigation", Navigation.ContentType)
    Assert.Equal("application/atom+xml;profile=opds-catalog;kind=acquisition", Acquisition.ContentType)

[<Fact>]
let ``a feed carries id, title, updated, and a self link of its own kind`` () =
    let document = parse (OpdsAtom.render (acquisitionFeed ()))
    let root = document.Root

    Assert.Equal("urn:bindery:feed:new", root.Element(atom + "id").Value)
    Assert.Equal("Recently added", root.Element(atom + "title").Value)
    Assert.Equal("2026-08-10T12:30:00Z", root.Element(atom + "updated").Value)

    let self =
        root.Elements(atom + "link")
        |> Seq.find (fun link -> link.Attribute(XName.Get "rel").Value = "self")

    Assert.Equal(Acquisition.ContentType, self.Attribute(XName.Get "type").Value)

[<Fact>]
let ``every feed offers start and search links`` () =
    let document = parse (OpdsAtom.render (acquisitionFeed ()))
    let rels =
        document.Root.Elements(atom + "link")
        |> Seq.map (fun link -> link.Attribute(XName.Get "rel").Value)
        |> Set.ofSeq

    Assert.Contains("start", rels)
    Assert.Contains("search", rels)
    Assert.Contains("up", rels)

// ---------------------------------------------------------------- entries

[<Fact>]
let ``a book entry has one acquisition link per format, with sizes`` () =
    let document = parse (OpdsAtom.render (acquisitionFeed ()))
    let entry = document.Root.Elements(atom + "entry") |> Seq.head

    let acquisitions =
        entry.Elements(atom + "link")
        |> Seq.filter (fun link -> link.Attribute(XName.Get "rel").Value.StartsWith "http://opds-spec.org/acquisition")
        |> List.ofSeq

    Assert.Equal(2, acquisitions.Length)
    // EPUB first: it is the format every reader opens.
    Assert.Equal("application/epub+zip", acquisitions.Head.Attribute(XName.Get "type").Value)
    Assert.Equal("481920", acquisitions.Head.Attribute(XName.Get "length").Value)
    Assert.EndsWith(".epub", acquisitions.Head.Attribute(XName.Get "href").Value)

[<Fact>]
let ``a book entry links a cover and a thumbnail when it has one`` () =
    let document = parse (OpdsAtom.render (acquisitionFeed ()))
    let entries = document.Root.Elements(atom + "entry") |> List.ofSeq

    let rels (entry: XElement) =
        entry.Elements(atom + "link") |> Seq.map (fun link -> link.Attribute(XName.Get "rel").Value) |> Set.ofSeq

    Assert.Contains("http://opds-spec.org/image", rels entries.Head)
    Assert.Contains("http://opds-spec.org/image/thumbnail", rels entries.Head)
    // The second fixture has no cover, and must not claim one.
    Assert.DoesNotContain("http://opds-spec.org/image", rels entries.[1])

[<Fact>]
let ``entry ids are stable urns rather than URLs`` () =
    let document = parse (OpdsAtom.render (acquisitionFeed ()))
    let id = (document.Root.Elements(atom + "entry") |> Seq.head).Element(atom + "id").Value

    // A hostname in an id breaks client-side dedup the day the deployment moves.
    Assert.Equal("urn:bindery:book:11111111222233334444555555555555", id)
    Assert.DoesNotContain("http", id)

[<Fact>]
let ``summaries are plain text and mention series and length`` () =
    let document = parse (OpdsAtom.render (acquisitionFeed ()))
    let entry = document.Root.Elements(atom + "entry") |> Seq.head
    let summary = entry.Element(atom + "summary")

    Assert.Equal("text", summary.Attribute(XName.Get "type").Value)
    Assert.Contains("Middle Rewrites, book 2", summary.Value)
    Assert.Contains("10 chapters", summary.Value)
    Assert.Contains("A story about a journey & a ring.", summary.Value)
    Assert.DoesNotContain("<em>", summary.Value)

[<Fact>]
let ``an entry without optional data omits the elements rather than emitting empties`` () =
    let document = parse (OpdsAtom.render (acquisitionFeed ()))
    let sparse = document.Root.Elements(atom + "entry") |> Seq.item 1

    Assert.Null(sparse.Element(atom + "summary"))
    Assert.Null(sparse.Element(dcterms + "language"))
    Assert.Empty(sparse.Elements(atom + "author"))
    Assert.Empty(sparse.Elements(atom + "category"))

[<Fact>]
let ``series survives as dcterms isPartOf`` () =
    let document = parse (OpdsAtom.render (acquisitionFeed ()))
    let entry = document.Root.Elements(atom + "entry") |> Seq.head

    Assert.Equal("Middle Rewrites", entry.Element(dcterms + "isPartOf").Value)

// ---------------------------------------------------------------- hostile input

[<Fact>]
let ``control characters are stripped instead of poisoning the whole feed`` () =
    // One stray form feed in a scraped summary otherwise turns the catalog into a parse
    // error in the reader rather than a missing character.
    let hostile =
        { book with
            Title = "Bad " + control 0x0C + "Title " + control 0x00 + "here"
            Summary = Some("line" + control 0x08 + "break") }

    let feed =
        { Feed.Create("urn:test", "t", Acquisition, at "2026-01-01T00:00:00Z") with
            Entries = [ Build.bookEntry urls hostile ] }

    let xml = OpdsAtom.render feed
    let document = parse xml // would throw outright if the characters had been emitted

    let title = (document.Root.Elements(atom + "entry") |> Seq.head).Element(atom + "title").Value
    Assert.DoesNotContain(control 0x0C, title)
    Assert.DoesNotContain(control 0x00, title)
    Assert.Contains("Bad", title)
    Assert.Contains("Title", title)

[<Fact>]
let ``markup in a title is escaped, not injected`` () =
    let hostile = { book with Title = "</title><script>alert(1)</script>" }

    let feed =
        { Feed.Create("urn:test", "t", Acquisition, at "2026-01-01T00:00:00Z") with
            Entries = [ Build.bookEntry urls hostile ] }

    let xml = OpdsAtom.render feed
    Assert.DoesNotContain("<script>", xml)

    let document = parse xml
    let title = (document.Root.Elements(atom + "entry") |> Seq.head).Element(atom + "title").Value
    Assert.Equal("</title><script>alert(1)</script>", title)

// ---------------------------------------------------------------- paging

[<Fact>]
let ``paging emits opensearch counters and the right neighbours`` () =
    let paging = { TotalResults = 95; ItemsPerPage = 20; StartIndex = 41 }
    let href page = sprintf "/opds/all?page=%d" page

    let feed =
        { Feed.Create("urn:test", "All", Acquisition, at "2026-01-01T00:00:00Z") with
            Paging = Some paging
            Links = Build.pagingLinks paging Acquisition.ContentType href }

    let document = parse (OpdsAtom.render feed)
    Assert.Equal("95", document.Root.Element(opensearch + "totalResults").Value)
    Assert.Equal("20", document.Root.Element(opensearch + "itemsPerPage").Value)
    Assert.Equal("41", document.Root.Element(opensearch + "startIndex").Value)

    let links =
        document.Root.Elements(atom + "link")
        |> Seq.map (fun link -> link.Attribute(XName.Get "rel").Value, link.Attribute(XName.Get "href").Value)
        |> Map.ofSeq

    Assert.Equal("/opds/all?page=0", links.["first"])
    Assert.Equal("/opds/all?page=4", links.["last"])
    Assert.Equal("/opds/all?page=1", links.["previous"])
    Assert.Equal("/opds/all?page=3", links.["next"])

[<Fact>]
let ``the last page offers no next link`` () =
    // Readers that trust `next` unconditionally will page forever if it is always there.
    let paging = { TotalResults = 40; ItemsPerPage = 20; StartIndex = 21 }
    let links = Build.pagingLinks paging Acquisition.ContentType (sprintf "/p/%d")

    Assert.DoesNotContain(Rel.Next, links |> List.map (fun link -> link.Rel))
    Assert.Contains(Rel.Previous, links |> List.map (fun link -> link.Rel))

[<Fact>]
let ``a single page needs no paging links at all`` () =
    let paging = { TotalResults = 5; ItemsPerPage = 20; StartIndex = 1 }
    Assert.Empty(Build.pagingLinks paging Acquisition.ContentType (sprintf "/p/%d"))

// ---------------------------------------------------------------- navigation

[<Fact>]
let ``a navigation entry points at a subsection and can carry a count`` () =
    let entry =
        Build.navigationEntry
            (Urn.grouping "author" "Some Author")
            "Some Author"
            "/opds/authors/Some%20Author"
            Acquisition
            (Some "12 books")
            (Some 12)
            (at "2026-01-01T00:00:00Z")

    let feed =
        { Feed.Create("urn:test", "Authors", Navigation, at "2026-01-01T00:00:00Z") with Entries = [ entry ] }

    let document = parse (OpdsAtom.render feed)
    let link = (document.Root.Elements(atom + "entry") |> Seq.head).Element(atom + "link")

    Assert.Equal("subsection", link.Attribute(XName.Get "rel").Value)
    Assert.Equal(Acquisition.ContentType, link.Attribute(XName.Get "type").Value)
    Assert.Equal("12", link.Attribute(XName.Get("{http://purl.org/syndication/thread/1.0}count")).Value)

// ---------------------------------------------------------------- opensearch

[<Fact>]
let ``the opensearch document offers both catalog formats`` () =
    let xml =
        OpdsAtom.renderOpenSearch
            "Bindery"
            "Search the catalog"
            "https://books.test/opds/search?q={searchTerms}"
            "https://books.test/opds/v2/search?q={searchTerms}"

    let document = parse xml
    let templates =
        document.Root.Elements(opensearch + "Url")
        |> Seq.map (fun url -> url.Attribute(XName.Get "type").Value)
        |> Set.ofSeq

    Assert.Contains(ContentTypes.Acquisition, templates)
    Assert.Contains(ContentTypes.Opds2, templates)
    Assert.Contains("{searchTerms}", xml)
