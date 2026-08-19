/// Golden-file tests against checked-in reference feeds.
///
/// The assertions in the other test files say what a feed must contain. These say what it
/// looks like, byte for byte. That catches the changes nobody writes an assertion for —
/// an element quietly reordered, a namespace prefix renamed, a link relation dropped —
/// which is exactly the class of change that breaks a picky ereader.
///
/// To update a reference after an intentional change:
///
///     BINDERY_UPDATE_REFERENCE=1 dotnet test tests/Bindery.Core.Tests
///
/// then read the diff before committing it. If the diff is not obviously what you meant,
/// it is not.
module Bindery.Core.Tests.ReferenceFeedTests

open System
open System.IO
open Xunit
open Bindery.Core
open Bindery.Core.Opds
open Bindery.Core.Tests.Fixtures

let private referenceDirectory =
    Path.Combine(AppContext.BaseDirectory, "reference")

let private updating =
    match Environment.GetEnvironmentVariable "BINDERY_UPDATE_REFERENCE" with
    | null | "" | "0" -> false
    | _ -> true

/// Reference files live next to the source, not just in the build output, so that
/// regenerating them produces a reviewable diff in the repository.
let private sourceDirectory =
    let rec find (directory: DirectoryInfo) =
        if isNull (box directory) then None
        elif File.Exists(Path.Combine(directory.FullName, "Bindery.Core.Tests.fsproj")) then Some directory.FullName
        else find directory.Parent

    find (DirectoryInfo AppContext.BaseDirectory)

let private normalize (text: string) = text.Replace("\r\n", "\n").TrimEnd()

let private compare (name: string) (actual: string) =
    let path = Path.Combine(referenceDirectory, name)

    if updating then
        Directory.CreateDirectory referenceDirectory |> ignore
        File.WriteAllText(path, normalize actual + "\n")

        match sourceDirectory with
        | Some root ->
            let sourcePath = Path.Combine(root, "reference", name)
            Directory.CreateDirectory(Path.GetDirectoryName sourcePath) |> ignore
            File.WriteAllText(sourcePath, normalize actual + "\n")
        | None -> ()

    Assert.True(File.Exists path, sprintf "missing reference feed %s; regenerate with BINDERY_UPDATE_REFERENCE=1" name)
    Assert.Equal(normalize (File.ReadAllText path), normalize actual)

let private updated = at "2026-08-10T12:30:00Z"

let private rootFeed =
    { Feed.Create(Urn.feed "root", "Bindery", Navigation, updated) with
        Subtitle = Some "A small library"
        Author = Some { Name = "Bindery"; Uri = Some "https://github.com/OWNER/bindery" }
        Links = Build.standardLinks urls "/opds" Navigation None
        Entries =
          [ Build.navigationEntry (Urn.feed "new") "Recently added" "/opds/new" Acquisition
                (Some "The most recent arrivals") (Some 2) updated
            Build.navigationEntry (Urn.feed "authors") "Authors" "/opds/authors" Navigation
                (Some "Browse by author") (Some 1) updated ] }

let private acquisition =
    { Feed.Create(Urn.feed "new", "Recently added", Acquisition, updated) with
        Links =
            Build.standardLinks urls "/opds/new" Acquisition (Some "/opds")
            @ Build.pagingLinks
                { TotalResults = 2; ItemsPerPage = 20; StartIndex = 1 }
                Acquisition.ContentType
                (sprintf "/opds/new?page=%d")
        Entries = [ Build.bookEntry urls book; Build.bookEntry urls minimalBook ]
        Paging = Some { TotalResults = 2; ItemsPerPage = 20; StartIndex = 1 } }

[<Fact>]
let ``the root navigation feed matches its reference`` () =
    compare "root-navigation.xml" (OpdsAtom.render rootFeed)

[<Fact>]
let ``the acquisition feed matches its reference`` () =
    compare "new-acquisition.xml" (OpdsAtom.render acquisition)

[<Fact>]
let ``the OPDS 2 acquisition feed matches its reference`` () =
    compare "new-acquisition.json" (OpdsJson.render acquisition)

[<Fact>]
let ``the OPDS 2 navigation feed matches its reference`` () =
    compare "root-navigation.json" (OpdsJson.render rootFeed)

[<Fact>]
let ``the opensearch description matches its reference`` () =
    compare
        "opensearch.xml"
        (OpdsAtom.renderOpenSearch
            "Bindery"
            "Search the Bindery catalog"
            "/opds/search?q={searchTerms}"
            "/opds/v2/search?q={searchTerms}")

[<Fact>]
let ``a standalone entry document matches its reference`` () =
    compare "book-entry.xml" (OpdsAtom.renderEntry (Build.bookEntry urls book))
