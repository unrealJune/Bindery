/// Shared test data. Fixed timestamps and ids on purpose: feed output is compared against
/// checked-in reference documents, and a clock in the fixture would make that impossible.
module Bindery.Core.Tests.Fixtures

open System
open Bindery.Core.Domain
open Bindery.Core.Opds

let at (text: string) = DateTimeOffset.Parse(text, Globalization.CultureInfo.InvariantCulture)

let bookId = Guid.Parse "11111111-2222-3333-4444-555555555555"
let secondBookId = Guid.Parse "66666666-7777-8888-9999-aaaaaaaaaaaa"

let urls =
    { Root = "/opds"
      Feed = fun path -> "/opds/" + path.TrimStart '/'
      Book = fun id -> sprintf "/opds/book/%s" (id.ToString "N")
      Download = fun id ext -> sprintf "/opds/download/%s.%s" (id.ToString "N") ext
      Cover = fun id -> sprintf "/opds/cover/%s" (id.ToString "N")
      Thumbnail = fun id -> sprintf "/opds/thumb/%s" (id.ToString "N")
      Search = fun query -> sprintf "/opds/search?q=%s" (Uri.EscapeDataString query)
      OpenSearch = "/opds/opensearch.xml" }

let epub (path: string) (size: int64) =
    { Format = "epub"
      ContentType = "application/epub+zip"
      RelativePath = path
      SizeBytes = size
      Sha256 = "0000000000000000000000000000000000000000000000000000000000000000" }

let book =
    { Id = bookId
      Title = "The Hobbit, Rewritten"
      SortTitle = "Hobbit, Rewritten"
      Authors = [ { Name = "Some Author"; Sort = "Author, Some" } ]
      Summary = Some "<p>A story about a <em>journey</em> &amp; a ring.</p>"
      Language = Some "en"
      Series = Some { Name = "Middle Rewrites"; Index = Some 2.0 }
      Tags = [ "Adventure"; "Slow Burn" ]
      Published = Some(at "2025-01-02T00:00:00Z")
      Added = at "2026-08-01T09:00:00Z"
      Updated = at "2026-08-10T12:30:00Z"
      SourceUrl = Some "https://archiveofourown.org/works/12345"
      SourcePlugin = Some "fanficfare"
      SourceId = Some "ao3:12345"
      Chapters = Some 10
      HasCover = true
      Files =
        [ epub "Some Author/The Hobbit, Rewritten/The Hobbit, Rewritten - Some Author.epub" 481920L
          { Format = "txt"
            ContentType = "text/plain; charset=utf-8"
            RelativePath = "Some Author/The Hobbit, Rewritten/The Hobbit, Rewritten - Some Author.txt"
            SizeBytes = 90210L
            Sha256 = "1111111111111111111111111111111111111111111111111111111111111111" } ] }

/// Deliberately sparse: no series, no cover, no summary, one author, one file. The shape
/// that finds every place an optional field was assumed present.
let minimalBook =
    { Id = secondBookId
      Title = "Untitled Draft"
      SortTitle = "Untitled Draft"
      Authors = []
      Summary = None
      Language = None
      Series = None
      Tags = []
      Published = None
      Added = at "2026-08-02T09:00:00Z"
      Updated = at "2026-08-02T09:00:00Z"
      SourceUrl = None
      SourcePlugin = None
      SourceId = None
      Chapters = None
      HasCover = false
      Files = [ epub "Unknown/Untitled Draft/Untitled Draft - Unknown.epub" 1024L ] }

let manifestJson =
    """
{
  "protocolVersion": 1,
  "name": "fanficfare",
  "displayName": "FanFicFare",
  "version": "0.1.0",
  "homepage": "https://github.com/JimmXinu/FanFicFare",
  "priority": 100,
  "matches": ["^https?://(www\\.)?archiveofourown\\.org/works/\\d+"],
  "formats": ["EPUB", "txt"],
  "capabilities": { "probe": true, "update": true, "metadata": true, "cover": true },
  "config": [
    { "key": "ao3_username", "label": "AO3 username", "type": "string", "default": "someone" },
    { "key": "ao3_password", "label": "AO3 password", "type": "secret", "default": "leaked?" },
    { "key": "personal_ini", "label": "personal.ini", "type": "text" },
    { "key": "format", "label": "Format", "type": "select",
      "options": [ { "value": "epub", "label": "EPUB" }, { "value": "txt" } ] }
  ],
  "actions": [
    { "name": "list_urls", "label": "List stories",
      "input": [ { "key": "url", "label": "Page URL", "type": "url", "required": true } ],
      "output": { "kind": "list", "itemAction": "download" } }
  ],
  "ui": {
    "mode": "fragment",
    "nav": [
      { "label": "FanFicFare", "path": "/" },
      { "label": "Escape", "path": "/../../etc" },
      { "label": "No path" }
    ]
  },
  "somethingAddedInV2": { "ignored": true }
}
"""

let resultOkJson =
    """
{"event":"result","status":"ok",
 "artifacts":[
   {"id":"book","filename":"Story.epub","format":"EPUB","contentType":"application/epub+zip",
    "kind":"book","primary":true,"bytes":481920,"sha256":"ABC0000000000000000000000000000000000000000000000000000000000123"},
   {"id":"cover","filename":"cover.png","format":"png","kind":"cover","primary":false}],
 "metadata":{"title":"Story","authors":["Someone","Someone Else"],"series":"A Series","seriesIndex":2,
   "summary":"A summary.","language":"en","tags":["Fluff","fluff"],"sourceId":"ao3:1",
   "sourceUrl":"https://example.test/1","publisher":"AO3","chapters":10,
   "published":"2025-01-02T00:00:00Z","updated":"2026-08-01T00:00:00Z"}}
"""
