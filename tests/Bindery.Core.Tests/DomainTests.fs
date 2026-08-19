module Bindery.Core.Tests.DomainTests

open Xunit
open Bindery.Core.Domain

[<Theory>]
[<InlineData("The Hobbit", "Hobbit")>]
[<InlineData("A Study in Scarlet", "Study in Scarlet")>]
[<InlineData("An Ideal Husband", "Ideal Husband")>]
[<InlineData("Theory of Everything", "Theory of Everything")>]
[<InlineData("  Spaced  ", "Spaced")>]
[<InlineData("", "")>]
let ``sort titles move leading articles out of the way`` (input: string) (expected: string) =
    Assert.Equal(expected, Naming.sortTitle input)

[<Theory>]
[<InlineData("Ursula Le Guin", "Guin, Ursula Le")>]
[<InlineData("Ursula K. Le Guin", "Ursula K. Le Guin")>]
[<InlineData("Terry Pratchett", "Pratchett, Terry")>]
[<InlineData("cassandra_clare", "cassandra_clare")>]
[<InlineData("xX_dark_phoenix_Xx", "xX_dark_phoenix_Xx")>]
[<InlineData("A B C D", "A B C D")>]
let ``author sort keys give up on names that are not names`` (input: string) (expected: string) =
    // Fanfic pseudonyms are not "First Last". Mangling them is worse than leaving them.
    Assert.Equal(expected, Naming.sortAuthor input)

[<Theory>]
[<InlineData("Normal Title", "Normal Title")>]
[<InlineData("../../etc/passwd", "_.._etc_passwd")>]
[<InlineData("with/slash", "with_slash")>]
[<InlineData("with\\backslash", "with_backslash")>]
[<InlineData("colon: here", "colon_ here")>]
[<InlineData("trailing dots...", "trailing dots")>]
[<InlineData("   ", "untitled")>]
[<InlineData("CON", "_CON")>]
[<InlineData("lpt1", "_lpt1")>]
let ``path segments survive contact with a real filesystem`` (input: string) (expected: string) =
    Assert.Equal(expected, Naming.pathSegment input)

[<Fact>]
let ``path segments never traverse, whatever the input`` () =
    for hostile in [ "../.."; "..\\.."; "a/../../b"; "\u0000null"; "C:\\Windows" ] do
        let segment = Naming.pathSegment hostile
        Assert.DoesNotContain("/", segment)
        Assert.DoesNotContain("\\", segment)
        Assert.NotEqual<string>("..", segment)

[<Fact>]
let ``path segments are bounded so the full path stays under filesystem limits`` () =
    Assert.True((Naming.pathSegment (String.replicate 500 "x")).Length <= 120)

[<Fact>]
let ``the library layout is author over title`` () =
    Assert.Equal("Some Author/The Hobbit", Naming.bookDirectory "Some Author" "The Hobbit")
    Assert.Equal("The Hobbit - Some Author.epub", Naming.fileName "The Hobbit" "Some Author" ".epub")

[<Fact>]
let ``content types are right for the formats ereaders care about`` () =
    Assert.Equal("application/epub+zip", Formats.contentType "epub")
    Assert.Equal("application/epub+zip", Formats.contentType ".EPUB")
    Assert.Equal("application/octet-stream", Formats.contentType "xyz")
    Assert.Equal("image/jpeg", Formats.imageContentType "JPG")

[<Fact>]
let ``summaries become plain text`` () =
    Assert.Equal(
        "A story about a journey & a ring.",
        Summary.plain "<p>A story about a <em>journey</em> &amp; a ring.</p>")

    Assert.Equal("", Summary.plain null)

[<Fact>]
let ``summaries truncate on a word boundary`` () =
    let truncated = Summary.truncate 20 "the quick brown fox jumps over the lazy dog"

    Assert.True(truncated.Length <= 21)
    Assert.EndsWith("…", truncated)
    Assert.DoesNotContain("jumps", truncated)

[<Fact>]
let ``the primary file prefers epub whatever the order`` () =
    let txt =
        { Format = "txt"
          ContentType = "text/plain"
          RelativePath = "a.txt"
          SizeBytes = 1L
          Sha256 = "" }

    let epub = { txt with Format = "epub"; RelativePath = "a.epub" }
    let book = { Fixtures.minimalBook with Files = [ txt; epub ] }

    Assert.Equal(Some epub, book.PrimaryFile)

[<Fact>]
let ``a book with no authors still reads as something`` () =
    Assert.Equal("Unknown", Fixtures.minimalBook.AuthorLine)
    Assert.Equal("Some Author", Fixtures.book.AuthorLine)
