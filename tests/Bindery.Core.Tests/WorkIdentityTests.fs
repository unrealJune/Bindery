module Bindery.Core.Tests.WorkIdentityTests

open System
open Xunit
open Bindery.Core.WorkIdentity

let private work title authors urls ids =
    { Id = Guid.NewGuid()
      Title = title
      Authors = authors
      SourceUrls = urls
      SourceIds = ids }

[<Fact>]
let ``a shared source url is certain, however differently it is written`` () =
    let a = work "Worm" [ "wildbow" ] [ "https://parahumans.wordpress.com/" ] []
    let b = work "Worm (complete)" [] [ "http://www.parahumans.wordpress.com?utm_source=x#top" ] []

    match likeness a b with
    | Some(SameSource _) -> ()
    | other -> failwithf "expected SameSource, got %A" other

[<Fact>]
let ``a shared source id is certain`` () =
    let a = work "A" [] [] [ "ao3:123" ]
    let b = work "B" [] [ "https://elsewhere.example/" ] [ "ao3:123" ]
    Assert.True(likeness a b |> Option.exists Likeness.isCertain)

[<Fact>]
let ``same title across plugins is probable, and stronger with a shared author`` () =
    let royalRoad = work "Maidens of the Fall" [ "Hazel Young" ] [ "https://www.royalroad.com/fiction/1" ] [ "royalroad:1" ]
    let site = work "Maidens of the Fall" [ "Hungry" ] [ "https://hungryshedgerow.net/maidens-of-the-fall/" ] []
    let sameAuthor = { site with Authors = [ "Young, Hazel" ] }

    let titleOnly = likeness royalRoad site |> Option.map Likeness.score
    let withAuthor = likeness royalRoad sameAuthor |> Option.map Likeness.score

    Assert.Equal(Some 0.75, titleOnly)
    Assert.Equal(Some 0.95, withAuthor)
    Assert.False(likeness royalRoad site |> Option.exists Likeness.isCertain, "a title is never certain")

[<Fact>]
let ``titles compare without case, accents, punctuation, or a leading article`` () =
    Assert.Equal(normalizeTitle "wandering inn", normalizeTitle "The Wandering Inn!")
    Assert.Equal("cafe", normalizeTitle "Café")
    Assert.Equal("a", normalizeTitle "A") // an article alone is the whole title

[<Fact>]
let ``a subtitle on one side still matches`` () =
    let a = work "Maidens of the Fall" [ "Hazel Young" ] [] []
    let b = work "Maidens of the Fall: A Magical Girl Grimdark" [ "Hazel Young" ] [] []
    Assert.Equal(Some 0.7, likeness a b |> Option.map Likeness.score)

[<Fact>]
let ``short or partial titles do not match on prefix alone`` () =
    Assert.Equal(None, likeness (work "Worm" [] [] []) (work "Worm Story" [] [] []))
    Assert.Equal(None, likeness (work "Pale" [ "wildbow" ] [] []) (work "Ward" [ "wildbow" ] [] []))

[<Fact>]
let ``duplicatesOf excludes the work itself and sorts strongest first`` () =
    let me = work "Sunspot" [ "yootie" ] [ "https://sunspot.gay/" ] []
    let exact = work "Something else" [] [ "https://sunspot.gay" ] []
    let probable = work "Sunspot" [] [] []
    let unrelated = work "Worm" [] [] []

    let found = duplicatesOf me [ me; probable; unrelated; exact ] |> List.map fst
    Assert.Equal<Work list>([ exact; probable ], found)

[<Fact>]
let ``normalizeUrl keeps what identifies a post`` () =
    Assert.NotEqual<string>(normalizeUrl "https://e.org/?p=1", normalizeUrl "https://e.org/?p=2")
    Assert.Equal("e.org/a/b", normalizeUrl "HTTPS://www.E.org/a/b/")
    Assert.Equal("", normalizeUrl "  ")
