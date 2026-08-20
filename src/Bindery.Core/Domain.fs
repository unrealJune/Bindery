/// The library's shape, independent of how it is stored.
///
/// These types are what the OPDS serializers consume. They are deliberately not EF Core
/// entities: the host maps its rows onto these, so feed generation stays a pure function
/// of data and can be tested without a database.
module Bindery.Core.Domain

open System
open System.Globalization
open System.Text.RegularExpressions

type Author =
    { Name: string
      /// "Le Guin, Ursula K." — what an alphabetical browse sorts on.
      Sort: string }

type SeriesRef = { Name: string; Index: float option }

type BookFile =
    { Format: string
      ContentType: string
      /// Relative to the library root, always with forward slashes.
      RelativePath: string
      SizeBytes: int64
      Sha256: string }

type Book =
    { Id: Guid
      Title: string
      SortTitle: string
      Authors: Author list
      Summary: string option
      Language: string option
      Series: SeriesRef option
      Tags: string list
      Published: DateTimeOffset option
      Added: DateTimeOffset
      Updated: DateTimeOffset
      SourceUrl: string option
      SourcePlugin: string option
      SourceId: string option
      Chapters: int option
      HasCover: bool
      Files: BookFile list }

    member this.PrimaryFile =
        this.Files
        |> List.sortBy (fun file -> if file.Format = "epub" then 0 else 1)
        |> List.tryHead

    member this.AuthorLine =
        match this.Authors with
        | [] -> "Unknown"
        | authors -> String.Join(", ", authors |> List.map (fun author -> author.Name))

/// A navigation target: an author, a series, or a tag, with how many books it holds.
type Grouping =
    { Key: string
      Label: string
      Sort: string
      Count: int }

// ---------------------------------------------------------------- naming

/// Callers include C# code where a string can still be null, so text arriving from the
/// host is normalized once here rather than guarded at every use. The parameter is typed
/// as plain `string` (rather than `string | null`) because the union-with-null syntax
/// needs a newer F# compiler than the .NET 8 SDK the Docker build uses; `null` is still
/// matched safely at runtime below.
let internal orEmpty (value: string) : string =
    match value with
    | null -> ""
    | text -> text

module Naming =
    let private leadingArticles = [ "the "; "a "; "an " ]

    /// Sort key for a title: leading articles moved out of the way, case-folded.
    let sortTitle (title: string) =
        let trimmed = (orEmpty title).Trim()
        let lowered = trimmed.ToLowerInvariant()

        match leadingArticles |> List.tryFind lowered.StartsWith with
        | Some article -> trimmed.Substring(article.Length).Trim()
        | None -> trimmed

    /// Sort key for a person: last name first, on a best-effort basis.
    ///
    /// Best-effort is the honest description. Fanfic pseudonyms are not "First Last" and
    /// pretending otherwise produces worse results than leaving them alone, so anything
    /// that does not look like a plain two-or-three-word Western name is left as written.
    let sortAuthor (name: string) =
        let trimmed = (orEmpty name).Trim()
        let parts = trimmed.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)

        if parts.Length < 2 || parts.Length > 3 then
            trimmed
        elif parts |> Array.exists (fun part -> part.Length > 1 && part |> Seq.forall (fun c -> not (Char.IsLetter c))) then
            trimmed
        else
            let last = parts.[parts.Length - 1]
            let rest = String.Join(" ", parts.[.. parts.Length - 2])
            sprintf "%s, %s" last rest

    let private invalidPathChars =
        Set.ofArray (Array.append (IO.Path.GetInvalidFileNameChars()) [| '/'; '\\'; ':'; '*'; '?'; '"'; '<'; '>'; '|' |])

    /// A single path segment that is safe on every filesystem Bindery might run on,
    /// including the Windows reserved names that bite exactly once per project.
    let pathSegment (value: string) =
        let source = (orEmpty value).Trim()

        let cleaned =
            source
            |> Seq.map (fun c ->
                if invalidPathChars.Contains c || Char.IsControl c then '_' else c)
            |> Seq.toArray
            |> String

        let collapsed = Regex.Replace(cleaned, @"\s+", " ").Trim([| ' '; '.' |])

        let reserved =
            [ "CON"; "PRN"; "AUX"; "NUL"
              "COM1"; "COM2"; "COM3"; "COM4"; "COM5"; "COM6"; "COM7"; "COM8"; "COM9"
              "LPT1"; "LPT2"; "LPT3"; "LPT4"; "LPT5"; "LPT6"; "LPT7"; "LPT8"; "LPT9" ]

        let safe =
            if collapsed = "" then "untitled"
            elif reserved |> List.contains (collapsed.ToUpperInvariant()) then "_" + collapsed
            else collapsed

        if safe.Length > 120 then safe.Substring(0, 120).TrimEnd([| ' '; '.' |]) else safe

    /// `{Author}/{Title}` — the library layout. Human-browsable and rsync-able on
    /// purpose: the files are the source of truth and the database is an index.
    let bookDirectory (authorLine: string) (title: string) =
        sprintf "%s/%s" (pathSegment authorLine) (pathSegment title)

    let fileName (title: string) (authorLine: string) (extension: string) =
        let ext = extension.TrimStart '.'
        pathSegment (sprintf "%s - %s" title authorLine) + "." + ext

// ---------------------------------------------------------------- formats

module Formats =
    let contentType (format: string) =
        match format.TrimStart('.').ToLowerInvariant() with
        | "epub" -> "application/epub+zip"
        | "mobi" -> "application/x-mobipocket-ebook"
        | "azw3" -> "application/vnd.amazon.ebook"
        | "pdf" -> "application/pdf"
        | "txt" -> "text/plain; charset=utf-8"
        | "html" | "htm" -> "text/html; charset=utf-8"
        | "cbz" -> "application/vnd.comicbook+zip"
        | "fb2" -> "application/x-fictionbook+xml"
        | _ -> "application/octet-stream"

    let imageContentType (extension: string) =
        match extension.TrimStart('.').ToLowerInvariant() with
        | "jpg" | "jpeg" -> "image/jpeg"
        | "png" -> "image/png"
        | "gif" -> "image/gif"
        | "webp" -> "image/webp"
        | _ -> "application/octet-stream"

    /// Extensions an ereader will actually open, best first.
    let preferred = [ "epub"; "azw3"; "mobi"; "pdf"; "fb2"; "cbz"; "html"; "txt" ]

module Summary =
    /// Strip markup and collapse whitespace. Summaries arrive as site HTML and OPDS
    /// clients render text wildly differently; plain text is the only portable answer.
    let plain (value: string) =
        let source = orEmpty value
        let stripped = Regex.Replace(source, "<[^>]+>", " ")
        let decoded = orEmpty (Net.WebUtility.HtmlDecode stripped)
        Regex.Replace(decoded, @"\s+", " ").Trim()

    let truncate (limit: int) (value: string) =
        if value.Length <= limit then
            value
        else
            let cut = value.Substring(0, limit)

            match cut.LastIndexOf ' ' with
            | index when index > limit / 2 -> cut.Substring(0, index) + "…"
            | _ -> cut + "…"

    let titleCaseTag (tag: string) =
        if tag |> Seq.exists Char.IsUpper then tag
        else CultureInfo.InvariantCulture.TextInfo.ToTitleCase tag
