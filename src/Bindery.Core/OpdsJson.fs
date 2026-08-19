/// OPDS 2.0 — JSON. Where the standard is going, served from the same feed model as 1.2.
///
/// The shapes differ more than they look: 2.0 splits a feed's entries into `navigation`
/// (links) and `publications` (works with their own metadata and images), where 1.2 has
/// one `entry` element that means both. That split is done here, from the link relations
/// already on the entry, so callers build a feed once.
module Bindery.Core.OpdsJson

open System
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open Bindery.Core
open Bindery.Core.Opds

let private writeString (writer: Utf8JsonWriter) (name: string) (value: string option) =
    match value with
    | Some text when text.Trim() <> "" -> writer.WriteString(name, text)
    | _ -> ()

let private writeTimestamp (writer: Utf8JsonWriter) (name: string) (value: DateTimeOffset option) =
    match value with
    | Some stamp -> writer.WriteString(name, stamp.ToUniversalTime().ToString "yyyy-MM-ddTHH:mm:ssZ")
    | None -> ()

let private writeLink (writer: Utf8JsonWriter) (link: Link) =
    writer.WriteStartObject()
    writer.WriteString("rel", link.Rel)
    writer.WriteString("href", link.Href)
    writer.WriteString("type", link.Type)
    writeString writer "title" link.Title

    match link.Length with
    | Some length -> writer.WriteNumber("length", length)
    | None -> ()

    match link.Count with
    | Some count ->
        writer.WriteStartObject "properties"
        writer.WriteNumber("numberOfItems", count)
        writer.WriteEndObject()
    | None -> ()

    writer.WriteEndObject()

let private writeLinks (writer: Utf8JsonWriter) (name: string) (links: Link list) =
    if not links.IsEmpty then
        writer.WriteStartArray name

        for link in links do
            writeLink writer link

        writer.WriteEndArray()

let private isImage (link: Link) =
    link.Rel = Rel.Image || link.Rel = Rel.Thumbnail

let private isAcquisition (link: Link) =
    link.Rel = Rel.Acquisition || link.Rel = Rel.OpenAccess

/// An entry is a publication when it offers something to download; otherwise it is
/// navigation. That is the same distinction 1.2 encodes in the link relation, read back
/// out rather than tracked separately and allowed to drift.
let internal isPublication (entry: Entry) =
    entry.Links |> List.exists isAcquisition

let private writePublication (writer: Utf8JsonWriter) (entry: Entry) =
    writer.WriteStartObject()

    writer.WriteStartObject "metadata"
    writer.WriteString("@type", "http://schema.org/Book")
    writeString writer "identifier" entry.Identifier
    writer.WriteString("title", entry.Title)
    writeString writer "description" entry.Summary
    writeString writer "language" entry.Language
    writeString writer "publisher" entry.Publisher
    writeTimestamp writer "published" entry.Published
    writeTimestamp writer "modified" (Some entry.Updated)

    if not entry.Authors.IsEmpty then
        writer.WriteStartArray "author"

        for author in entry.Authors do
            writer.WriteStartObject()
            writer.WriteString("name", author.Name)
            writer.WriteEndObject()

        writer.WriteEndArray()

    let subjects = entry.Categories |> List.filter (fun category -> category.Scheme.IsNone)

    if not subjects.IsEmpty then
        writer.WriteStartArray "subject"

        for subject in subjects do
            writer.WriteStringValue(subject.Label |> Option.defaultValue subject.Term)

        writer.WriteEndArray()

    match entry.Series with
    | Some series ->
        writer.WriteStartObject "belongsTo"
        writer.WriteStartObject "series"
        writer.WriteString("name", series.Name)

        match series.Index with
        | Some index -> writer.WriteNumber("position", index)
        | None -> ()

        writer.WriteEndObject()
        writer.WriteEndObject()
    | None -> ()

    writer.WriteEndObject() // metadata

    writeLinks writer "links" (entry.Links |> List.filter (isImage >> not))
    writeLinks writer "images" (entry.Links |> List.filter isImage)

    writer.WriteEndObject()

let private writeNavigation (writer: Utf8JsonWriter) (entry: Entry) =
    let target =
        entry.Links
        |> List.tryFind (fun link -> link.Rel = "subsection" || link.Rel = Rel.SelfRel)
        |> Option.orElse (List.tryHead entry.Links)

    match target with
    | None -> ()
    | Some link ->
        writer.WriteStartObject()
        writer.WriteString("href", link.Href)
        writer.WriteString("title", entry.Title)
        writer.WriteString("type", link.Type)
        writeString writer "rel" (Some "subsection")

        match entry.Content, link.Count with
        | None, None -> ()
        | subtitle, count ->
            writer.WriteStartObject "properties"
            writeString writer "subtitle" subtitle

            match count with
            | Some value -> writer.WriteNumber("numberOfItems", value)
            | None -> ()

            writer.WriteEndObject()

        writer.WriteEndObject()

let private options =
    // OPDS 2.0 is served as UTF-8 JSON and read by browsers and readers alike. The
    // relaxed encoder keeps non-ASCII titles legible instead of \uXXXX soup; it still
    // escapes the characters that matter for HTML embedding.
    JsonWriterOptions(Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

let render (feed: Feed) : string =
    use stream = new IO.MemoryStream()
    use writer = new Utf8JsonWriter(stream, options)

    writer.WriteStartObject()

    writer.WriteStartObject "metadata"
    writer.WriteString("@type", "http://schema.org/DataFeed")
    writer.WriteString("identifier", feed.Id)
    writer.WriteString("title", feed.Title)
    writeString writer "subtitle" feed.Subtitle
    writer.WriteString("modified", feed.Updated.ToUniversalTime().ToString "yyyy-MM-ddTHH:mm:ssZ")

    match feed.Paging with
    | Some paging ->
        writer.WriteNumber("numberOfItems", paging.TotalResults)
        writer.WriteNumber("itemsPerPage", paging.ItemsPerPage)
        writer.WriteNumber("currentPage", paging.PageIndex + 1)
    | None -> ()

    writer.WriteEndObject()

    writeLinks writer "links" feed.Links

    let publications, navigation = feed.Entries |> List.partition isPublication

    if not navigation.IsEmpty then
        writer.WriteStartArray "navigation"

        for entry in navigation do
            writeNavigation writer entry

        writer.WriteEndArray()

    if not publications.IsEmpty then
        writer.WriteStartArray "publications"

        for entry in publications do
            writePublication writer entry

        writer.WriteEndArray()

    writer.WriteEndObject()
    writer.Flush()
    Encoding.UTF8.GetString(stream.ToArray())
