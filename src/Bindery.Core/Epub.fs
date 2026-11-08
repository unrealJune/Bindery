/// Reading an EPUB's own metadata.
///
/// This is the parsing half only: the two XML documents inside the container arrive here as
/// strings and leave as a record. Opening the zip stays in the host — F# owns the shape of
/// the data, C# owns the I/O — which is also what makes every case below testable without
/// producing an EPUB first.
///
/// The tolerance here is deliberate. A hand-uploaded EPUB is whatever a person had on disk:
/// EPUB 2 and 3, calibre's series metadata and the EPUB 3 collection form, `dc:` prefixed or
/// not. Elements are therefore matched on local name rather than namespace, and every field
/// is optional, because a file that only knows its own title is still worth filing.
module Bindery.Core.Epub

open System
open System.Globalization
open System.Xml
open System.Xml.Linq

type EpubMetadata =
    { Title: string option
      Authors: string list
      Language: string option
      Summary: string option
      Series: string option
      SeriesIndex: float option
      Tags: string list
      Published: DateTimeOffset option
      Identifier: string option
      /// Href of the cover image, relative to the OPF document's own directory.
      CoverHref: string option }

let empty =
    { Title = None
      Authors = []
      Language = None
      Summary = None
      Series = None
      SeriesIndex = None
      Tags = []
      Published = None
      Identifier = None
      CoverHref = None }

// ---------------------------------------------------------------- helpers

/// The document element, or None for anything that is not parseable XML.
let private parseRoot (xml: string) : XElement option =
    try
        // XDocument.Parse prohibits DTD processing, which is what keeps an uploaded EPUB
        // from turning an external entity into a file-read primitive. Do not relax it.
        match XDocument.Parse(Domain.orEmpty xml).Root with
        | null -> None
        | root -> Some root
    with
    | :? XmlException -> None
    | :? ArgumentException -> None

let private elements (name: string) (root: XElement) =
    root.Descendants() |> Seq.filter (fun element -> element.Name.LocalName = name)

let private text (element: XElement) = (Domain.orEmpty element.Value).Trim()

let private attr (name: string) (element: XElement) =
    element.Attributes()
    |> Seq.tryFind (fun attribute -> attribute.Name.LocalName = name)
    |> Option.map (fun attribute -> attribute.Value.Trim())

let private nonEmpty (value: string) = if value = "" then None else Some value

let private tryFloat (value: string) =
    match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, parsed -> Some parsed
    | _ -> None

/// EPUB dates are `dc:date`, which the specification allows to be a bare year.
let private tryDate (value: string) =
    let styles = DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal

    match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, styles) with
    | true, parsed -> Some parsed
    | _ ->
        match Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) with
        | true, year when year > 0 && year < 10000 ->
            Some(DateTimeOffset(DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc)))
        | _ -> None

// ---------------------------------------------------------------- container.xml

/// The OPF document's path, read from `META-INF/container.xml`.
let rootfilePath (containerXml: string) : string option =
    parseRoot containerXml
    |> Option.bind (
        elements "rootfile"
        >> Seq.choose (attr "full-path")
        >> Seq.tryPick nonEmpty)

// ---------------------------------------------------------------- content.opf

/// Everything Bindery cares about from an OPF package document.
let parsePackage (opfXml: string) : EpubMetadata =
    match parseRoot opfXml with
    | None -> empty
    | Some root ->
        let metas = root |> elements "meta" |> Seq.toList
        let items = root |> elements "item" |> Seq.toList

        let first name =
            root |> elements name |> Seq.map text |> Seq.tryPick nonEmpty

        let metaByName name =
            metas
            |> List.tryFind (fun meta ->
                (attr "name" meta |> Option.defaultValue "").ToLowerInvariant() = name)
            |> Option.bind (attr "content")
            |> Option.bind nonEmpty

        // EPUB 3 states a series as a `belongs-to-collection` property whose position is
        // carried by a second meta refining it by id; calibre's EPUB 2 form is two plain
        // `name`/`content` metas. Both are common in the wild, so both are read.
        let collection =
            metas |> List.tryFind (fun meta -> attr "property" meta = Some "belongs-to-collection")

        let collectionIndex =
            collection
            |> Option.bind (attr "id")
            |> Option.bind (fun id ->
                metas
                |> List.tryFind (fun meta ->
                    attr "refines" meta = Some("#" + id)
                    && attr "property" meta = Some "group-position"))
            |> Option.map text
            |> Option.bind nonEmpty

        // A `creator` with an explicit authorship role wins; otherwise every creator counts,
        // because EPUB 3 puts the role in a refining meta that plenty of tools omit.
        let creators = root |> elements "creator" |> Seq.toList

        let authored =
            match creators |> List.filter (fun creator -> attr "role" creator = Some "aut") with
            | [] -> creators
            | authors -> authors

        let hrefOfItem id =
            items
            |> List.tryFind (fun item -> attr "id" item = Some id)
            |> Option.bind (attr "href")
            |> Option.bind nonEmpty

        let isImage item =
            (attr "media-type" item |> Option.defaultValue "").StartsWith("image/", StringComparison.OrdinalIgnoreCase)

        let declaredCover =
            metaByName "cover"
            |> Option.bind (fun id ->
                items
                |> List.tryFind (fun item -> attr "id" item = Some id && isImage item)
                |> Option.bind (fun _ -> hrefOfItem id))

        let propertyCover =
            items
            |> List.tryFind (fun item ->
                isImage item
                && (attr "properties" item |> Option.defaultValue "").Split(' ') |> Array.contains "cover-image")
            |> Option.bind (attr "href")
            |> Option.bind nonEmpty

        { Title = first "title"
          Authors = authored |> List.map text |> List.choose nonEmpty |> List.distinct
          Language = first "language"
          Summary = first "description" |> Option.map Domain.Summary.plain |> Option.bind nonEmpty
          Series =
            collection
            |> Option.map text
            |> Option.bind nonEmpty
            |> Option.orElseWith (fun () -> metaByName "calibre:series")
          SeriesIndex =
            collectionIndex
            |> Option.orElseWith (fun () -> metaByName "calibre:series_index")
            |> Option.bind tryFloat
          Tags = root |> elements "subject" |> Seq.map text |> Seq.choose nonEmpty |> Seq.distinct |> List.ofSeq
          Published = first "date" |> Option.bind tryDate
          Identifier = first "identifier"
          CoverHref = declaredCover |> Option.orElse propertyCover }
