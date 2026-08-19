/// Thin, total accessors over System.Text.Json.
///
/// Plugin JSON is written by code Bindery does not control, in languages with looser
/// ideas about types than F#'s. Every accessor here returns an option and none of them
/// throw, so a malformed member degrades to "absent" and the caller decides whether that
/// is fatal. The protocol's rule that unknown members are ignored falls out for free.
module internal Bindery.Core.Json

open System
open System.Globalization
open System.Text.Json

let private isNull (element: JsonElement) =
    element.ValueKind = JsonValueKind.Null || element.ValueKind = JsonValueKind.Undefined

let tryProp (name: string) (element: JsonElement) : JsonElement option =
    if element.ValueKind <> JsonValueKind.Object then
        None
    else
        match element.TryGetProperty name with
        | true, value when not (isNull value) -> Some value
        | _ -> None

let tryString (name: string) (element: JsonElement) : string option =
    tryProp name element
    |> Option.bind (fun value ->
        match value.ValueKind with
        | JsonValueKind.String -> value.GetString() |> Option.ofObj
        | _ -> None)

/// A non-empty, trimmed string, or None. Blank is absent as far as we are concerned.
let tryText (name: string) (element: JsonElement) : string option =
    tryString name element
    |> Option.map (fun value -> value.Trim())
    |> Option.filter (fun value -> value <> "")

let stringOr (fallback: string) (name: string) (element: JsonElement) : string =
    tryText name element |> Option.defaultValue fallback

let tryBool (name: string) (element: JsonElement) : bool option =
    tryProp name element
    |> Option.bind (fun value ->
        match value.ValueKind with
        | JsonValueKind.True -> Some true
        | JsonValueKind.False -> Some false
        // Tolerated because config values travel as strings and plugin authors forget.
        | JsonValueKind.String ->
            match (value.GetString() |> Option.ofObj |> Option.defaultValue "").Trim().ToLowerInvariant() with
            | "true" | "1" | "yes" | "on" -> Some true
            | "false" | "0" | "no" | "off" -> Some false
            | _ -> None
        | _ -> None)

let boolOr (fallback: bool) (name: string) (element: JsonElement) : bool =
    tryBool name element |> Option.defaultValue fallback

let tryFloat (name: string) (element: JsonElement) : float option =
    tryProp name element
    |> Option.bind (fun value ->
        match value.ValueKind with
        | JsonValueKind.Number ->
            match value.TryGetDouble() with
            | true, number -> Some number
            | _ -> None
        | JsonValueKind.String ->
            match Double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, number -> Some number
            | _ -> None
        | _ -> None)

let tryInt (name: string) (element: JsonElement) : int option =
    tryProp name element
    |> Option.bind (fun value ->
        match value.ValueKind with
        | JsonValueKind.Number ->
            match value.TryGetInt32() with
            | true, number -> Some number
            | _ ->
                match value.TryGetDouble() with
                | true, number when Double.IsFinite number -> Some(int (Math.Round number))
                | _ -> None
        | JsonValueKind.String ->
            match Int32.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, number -> Some number
            | _ -> None
        | _ -> None)

let intOr (fallback: int) (name: string) (element: JsonElement) : int =
    tryInt name element |> Option.defaultValue fallback

let tryLong (name: string) (element: JsonElement) : int64 option =
    tryProp name element
    |> Option.bind (fun value ->
        match value.ValueKind with
        | JsonValueKind.Number ->
            match value.TryGetInt64() with
            | true, number -> Some number
            | _ -> None
        | JsonValueKind.String ->
            match Int64.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, number -> Some number
            | _ -> None
        | _ -> None)

/// RFC 3339 timestamps. Anything unparseable is absent rather than an error: plugins
/// forward whatever date format the site they scraped happened to use.
let tryTimestamp (name: string) (element: JsonElement) : DateTimeOffset option =
    tryText name element
    |> Option.bind (fun text ->
        match
            DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal)
        with
        | true, value -> Some value
        | _ -> None)

let items (name: string) (element: JsonElement) : JsonElement list =
    match tryProp name element with
    | Some value when value.ValueKind = JsonValueKind.Array -> value.EnumerateArray() |> List.ofSeq
    | _ -> []

let strings (name: string) (element: JsonElement) : string list =
    items name element
    |> List.choose (fun value ->
        if value.ValueKind = JsonValueKind.String then
            value.GetString() |> Option.ofObj |> Option.map (fun s -> s.Trim())
        else
            None)
    |> List.filter (fun value -> value <> "")

/// A flat string map. Values that are not strings are coerced, because the protocol says
/// config travels as strings and some plugin will inevitably send a raw bool.
let stringMap (name: string) (element: JsonElement) : Map<string, string> =
    match tryProp name element with
    | Some value when value.ValueKind = JsonValueKind.Object ->
        value.EnumerateObject()
        |> Seq.choose (fun prop ->
            match prop.Value.ValueKind with
            | JsonValueKind.String -> prop.Value.GetString() |> Option.ofObj |> Option.map (fun v -> prop.Name, v)
            | JsonValueKind.True -> Some(prop.Name, "true")
            | JsonValueKind.False -> Some(prop.Name, "false")
            | JsonValueKind.Number -> Some(prop.Name, prop.Value.GetRawText())
            | _ -> None)
        |> Map.ofSeq
    | _ -> Map.empty

let parse (text: string) : Result<JsonElement, string> =
    if String.IsNullOrWhiteSpace text then
        Error "empty JSON document"
    else
        try
            // Clone so the element outlives the document it was cut from.
            use document = JsonDocument.Parse(text, JsonDocumentOptions(AllowTrailingCommas = true))
            Ok(document.RootElement.Clone())
        with :? JsonException as ex ->
            Error(sprintf "invalid JSON: %s" ex.Message)

// ---------------------------------------------------------------- writing

let writeOptString (writer: Utf8JsonWriter) (name: string) (value: string option) =
    match value with
    | Some text -> writer.WriteString(name, text)
    | None -> ()

let writeOptInt (writer: Utf8JsonWriter) (name: string) (value: int option) =
    match value with
    | Some number -> writer.WriteNumber(name, number)
    | None -> ()

let writeOptFloat (writer: Utf8JsonWriter) (name: string) (value: float option) =
    match value with
    | Some number -> writer.WriteNumber(name, number)
    | None -> ()

let writeOptTimestamp (writer: Utf8JsonWriter) (name: string) (value: DateTimeOffset option) =
    match value with
    | Some stamp -> writer.WriteString(name, stamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))
    | None -> ()

let writeStringArray (writer: Utf8JsonWriter) (name: string) (values: string list) =
    if not values.IsEmpty then
        writer.WriteStartArray name
        for value in values do
            writer.WriteStringValue value
        writer.WriteEndArray()
