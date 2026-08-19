module Bindery.Core.Tests.OpdsJsonTests

open System.Text.Json
open Xunit
open Bindery.Core
open Bindery.Core.Opds
open Bindery.Core.Tests.Fixtures

let private feed () =
    { Feed.Create(Urn.feed "new", "Recently added", Acquisition, at "2026-08-10T12:30:00Z") with
        Links = Build.standardLinks urls "/opds/v2/new" Acquisition (Some "/opds/v2")
        Entries = [ Build.bookEntry urls book; Build.bookEntry urls minimalBook ]
        Paging = Some { TotalResults = 2; ItemsPerPage = 20; StartIndex = 1 } }

let private render () = JsonDocument.Parse(OpdsJson.render (feed ())).RootElement

let private prop (name: string) (element: JsonElement) = element.GetProperty name

[<Fact>]
let ``a feed carries metadata, links, and publications`` () =
    let root = render ()

    Assert.Equal("Recently added", (root |> prop "metadata" |> prop "title").GetString())
    Assert.Equal(2, (root |> prop "metadata" |> prop "numberOfItems").GetInt32())
    Assert.Equal(1, (root |> prop "metadata" |> prop "currentPage").GetInt32())
    Assert.Equal(2, (root |> prop "publications").GetArrayLength())

[<Fact>]
let ``books become publications and links become navigation`` () =
    // The 1.2 model has one entry type; 2.0 splits it. The split is derived from link
    // relations so the two formats cannot describe different things.
    let navigation =
        Build.navigationEntry (Urn.grouping "tag" "Fluff") "Fluff" "/opds/v2/tags/Fluff" Acquisition None (Some 3)
            (at "2026-01-01T00:00:00Z")

    let mixed =
        { Feed.Create("urn:test", "Mixed", Navigation, at "2026-01-01T00:00:00Z") with
            Entries = [ navigation; Build.bookEntry urls book ] }

    let root = JsonDocument.Parse(OpdsJson.render mixed).RootElement

    Assert.Equal(1, (root |> prop "navigation").GetArrayLength())
    Assert.Equal(1, (root |> prop "publications").GetArrayLength())

    let nav = (root |> prop "navigation").EnumerateArray() |> Seq.head
    Assert.Equal("Fluff", (nav |> prop "title").GetString())
    Assert.Equal(3, (nav |> prop "properties" |> prop "numberOfItems").GetInt32())

[<Fact>]
let ``a publication describes itself as a schema-org book`` () =
    let publication = (render () |> prop "publications").EnumerateArray() |> Seq.head
    let metadata = publication |> prop "metadata"

    Assert.Equal("http://schema.org/Book", (metadata |> prop "@type").GetString())
    Assert.Equal("The Hobbit, Rewritten", (metadata |> prop "title").GetString())
    Assert.Equal("en", (metadata |> prop "language").GetString())
    Assert.Equal("Some Author", ((metadata |> prop "author").EnumerateArray() |> Seq.head |> prop "name").GetString())
    Assert.Equal("2026-08-10T12:30:00Z", (metadata |> prop "modified").GetString())

[<Fact>]
let ``series becomes belongsTo, which 1_2 has no room for`` () =
    let metadata = (render () |> prop "publications").EnumerateArray() |> Seq.head |> prop "metadata"
    let series = metadata |> prop "belongsTo" |> prop "series"

    Assert.Equal("Middle Rewrites", (series |> prop "name").GetString())
    Assert.Equal(2.0, (series |> prop "position").GetDouble())

[<Fact>]
let ``images are separated from links, as 2_0 requires`` () =
    let publication = (render () |> prop "publications").EnumerateArray() |> Seq.head

    let imageRels =
        (publication |> prop "images").EnumerateArray()
        |> Seq.map (fun image -> (image |> prop "rel").GetString())
        |> Set.ofSeq

    Assert.Contains("http://opds-spec.org/image", imageRels)

    let linkRels =
        (publication |> prop "links").EnumerateArray()
        |> Seq.map (fun link -> (link |> prop "rel").GetString())
        |> Set.ofSeq

    Assert.DoesNotContain("http://opds-spec.org/image", linkRels)
    Assert.Contains("http://opds-spec.org/acquisition/open-access", linkRels)

[<Fact>]
let ``a publication without a cover carries no images array`` () =
    let sparse = (render () |> prop "publications").EnumerateArray() |> Seq.item 1

    Assert.False(fst (sparse.TryGetProperty "images"))
    Assert.False(fst (sparse |> prop "metadata" |> fun m -> m.TryGetProperty "belongsTo"))

[<Fact>]
let ``acquisition links keep their byte length`` () =
    let publication = (render () |> prop "publications").EnumerateArray() |> Seq.head

    let epub =
        (publication |> prop "links").EnumerateArray()
        |> Seq.find (fun link -> (link |> prop "type").GetString() = "application/epub+zip")

    Assert.Equal(481920L, (epub |> prop "length").GetInt64())

[<Fact>]
let ``non-ascii titles stay legible rather than becoming escape soup`` () =
    let accented = { book with Title = "Le Château des Étoiles — 星の城" }

    let json =
        OpdsJson.render
            { Feed.Create("urn:test", "t", Acquisition, at "2026-01-01T00:00:00Z") with
                Entries = [ Build.bookEntry urls accented ] }

    Assert.Contains("Le Château des Étoiles — 星の城", json)
    Assert.DoesNotContain("\\u", json)

[<Fact>]
let ``the rendered document is valid JSON whatever the input`` () =
    let hostile =
        { book with
            Title = "\"quotes\" and \\backslashes\\ and </script>"
            Summary = Some "tab\there" }

    let json =
        OpdsJson.render
            { Feed.Create("urn:test", "t", Acquisition, at "2026-01-01T00:00:00Z") with
                Entries = [ Build.bookEntry urls hostile ] }

    let root = JsonDocument.Parse(json).RootElement
    let title = (root |> prop "publications").EnumerateArray() |> Seq.head |> prop "metadata" |> prop "title"

    Assert.Equal("\"quotes\" and \\backslashes\\ and </script>", title.GetString())
