/// OPDS 1.2 — Atom XML. The format ereaders actually implement.
module Bindery.Core.OpdsAtom

open System
open System.Text
open System.Xml
open System.Xml.Linq
open Bindery.Core.Domain
open Bindery.Core.Opds

let private atom = XNamespace.Get "http://www.w3.org/2005/Atom"
let private dcterms = XNamespace.Get "http://purl.org/dc/terms/"
let private opds = XNamespace.Get "http://opds-spec.org/2010/catalog"
let private opensearch = XNamespace.Get "http://a9.com/-/spec/opensearch/1.1/"
let private thread = XNamespace.Get "http://purl.org/syndication/thread/1.0"

/// XML 1.0 forbids most control characters outright, and no amount of escaping makes them
/// legal. Scraped summaries contain them often enough to matter, and one stray 0x0C turns
/// a whole feed into a parse error in the reader rather than a missing character.
let internal xmlSafe (value: string) =
    if isNull (box value) then
        ""
    else
        let builder = StringBuilder(value.Length)

        for ch in value do
            if XmlConvert.IsXmlChar ch then
                builder.Append ch |> ignore
            elif Char.IsWhiteSpace ch then
                builder.Append ' ' |> ignore

        builder.ToString()

let private rfc3339 (value: DateTimeOffset) =
    value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

let private text (name: XName) (value: string) = XElement(name, xmlSafe value)

let private optional (name: XName) (value: string option) =
    match value with
    | Some content when content.Trim() <> "" -> [ box (text name content) ]
    | _ -> []

let private linkElement (link: Link) =
    XElement(
        atom + "link",
        [ yield box (XAttribute(XName.Get "rel", link.Rel))
          yield box (XAttribute(XName.Get "href", xmlSafe link.Href))
          yield box (XAttribute(XName.Get "type", link.Type))
          match link.Title with
          | Some title -> yield box (XAttribute(XName.Get "title", xmlSafe title))
          | None -> ()
          match link.Length with
          | Some length -> yield box (XAttribute(XName.Get "length", length))
          | None -> ()
          match link.Count with
          | Some count -> yield box (XAttribute(thread + "count", count))
          | None -> ()
          match link.FacetGroup with
          | Some group -> yield box (XAttribute(opds + "facetGroup", xmlSafe group))
          | None -> ()
          if link.Active then
              yield box (XAttribute(opds + "activeFacet", "true")) ]
    )

let private authorElement (author: EntryAuthor) =
    XElement(
        atom + "author",
        [ yield box (text (atom + "name") author.Name)
          match author.Uri with
          | Some uri -> yield box (text (atom + "uri") uri)
          | None -> () ]
    )

let private categoryElement (category: Category) =
    XElement(
        atom + "category",
        [ yield box (XAttribute(XName.Get "term", xmlSafe category.Term))
          match category.Label with
          | Some label -> yield box (XAttribute(XName.Get "label", xmlSafe label))
          | None -> ()
          match category.Scheme with
          | Some scheme -> yield box (XAttribute(XName.Get "scheme", scheme))
          | None -> () ]
    )

let internal entryElement (entry: Entry) =
    XElement(
        atom + "entry",
        [ yield box (text (atom + "id") entry.Id)
          yield box (text (atom + "title") entry.Title)
          yield box (text (atom + "updated") (rfc3339 entry.Updated))

          match entry.Published with
          | Some published -> yield box (text (atom + "published") (rfc3339 published))
          | None -> ()

          for author in entry.Authors do
              yield box (authorElement author)

          match entry.Summary with
          | Some summary when summary.Trim() <> "" ->
              yield box (XElement(atom + "summary", XAttribute(XName.Get "type", "text"), xmlSafe summary))
          | _ -> ()

          match entry.Content with
          | Some content when content.Trim() <> "" ->
              yield box (XElement(atom + "content", XAttribute(XName.Get "type", "text"), xmlSafe content))
          | _ -> ()

          for category in entry.Categories do
              yield box (categoryElement category)

          yield! optional (dcterms + "language") entry.Language
          yield! optional (dcterms + "publisher") entry.Publisher
          yield! optional (dcterms + "identifier") entry.Identifier

          match entry.Published with
          | Some published -> yield box (text (dcterms + "issued") (published.ToString "yyyy-MM-dd"))
          | None -> ()

          // OPDS 1.2 has no series element. `dcterms:isPartOf` is the closest standard
          // thing, and clients that ignore it still get the series from the summary.
          match entry.Series with
          | Some series -> yield box (text (dcterms + "isPartOf") series.Name)
          | None -> ()

          for link in entry.Links do
              yield box (linkElement link) ]
    )

let internal feedElement (feed: Feed) =
    XElement(
        atom + "feed",
        [ yield box (XAttribute(XNamespace.Xmlns + "dcterms", dcterms.NamespaceName))
          yield box (XAttribute(XNamespace.Xmlns + "opds", opds.NamespaceName))
          yield box (XAttribute(XNamespace.Xmlns + "opensearch", opensearch.NamespaceName))
          yield box (XAttribute(XNamespace.Xmlns + "thr", thread.NamespaceName))

          yield box (text (atom + "id") feed.Id)
          yield box (text (atom + "title") feed.Title)
          yield! optional (atom + "subtitle") feed.Subtitle
          yield box (text (atom + "updated") (rfc3339 feed.Updated))

          match feed.Author with
          | Some author -> yield box (authorElement author)
          | None -> ()

          match feed.Icon with
          | Some icon -> yield box (text (atom + "icon") icon)
          | None -> ()

          match feed.Paging with
          | Some paging ->
              yield box (text (opensearch + "totalResults") (string paging.TotalResults))
              yield box (text (opensearch + "itemsPerPage") (string paging.ItemsPerPage))
              yield box (text (opensearch + "startIndex") (string paging.StartIndex))
          | None -> ()

          for link in feed.Links do
              yield box (linkElement link)

          for entry in feed.Entries do
              yield box (entryElement entry) ]
    )

let private serialize (element: XElement) =
    // Written through a byte stream rather than a StringWriter on purpose: a StringWriter
    // reports UTF-16, and the declaration would then claim an encoding the response body
    // does not use.
    let document = XDocument(XDeclaration("1.0", "utf-8", null), box element)
    use stream = new IO.MemoryStream()

    let settings =
        XmlWriterSettings(Indent = true, IndentChars = "  ", Encoding = UTF8Encoding false, OmitXmlDeclaration = false)

    use xml = XmlWriter.Create(stream, settings)
    document.Save xml
    xml.Flush()
    Encoding.UTF8.GetString(stream.ToArray())

/// Render a feed as OPDS 1.2 Atom.
let render (feed: Feed) = serialize (feedElement feed)

/// Render a single book as a standalone Atom entry document, which is what
/// `type=entry;profile=opds-catalog` links point at.
let renderEntry (entry: Entry) =
    let element = entryElement entry
    element.Add(XAttribute(XNamespace.Xmlns + "dcterms", dcterms.NamespaceName))
    element.Add(XAttribute(XNamespace.Xmlns + "opds", opds.NamespaceName))
    serialize element

/// The OpenSearch description document. Clients fetch this to learn the query template.
let renderOpenSearch (shortName: string) (description: string) (atomTemplate: string) (jsonTemplate: string) =
    let url (contentType: string) (template: string) =
        XElement(
            opensearch + "Url",
            XAttribute(XName.Get "type", contentType),
            XAttribute(XName.Get "template", xmlSafe template)
        )

    serialize (
        XElement(
            opensearch + "OpenSearchDescription",
            text (opensearch + "ShortName") shortName,
            text (opensearch + "Description") description,
            text (opensearch + "InputEncoding") "UTF-8",
            text (opensearch + "OutputEncoding") "UTF-8",
            url ContentTypes.Acquisition atomTemplate,
            url ContentTypes.Opds2 jsonTemplate
        )
    )
