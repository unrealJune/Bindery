/// Deciding which plugin gets a URL.
///
/// Kept here rather than in the host because it is a pure function of manifests and a
/// string, and because getting it wrong is subtle: a bad regex from a third-party plugin
/// must not be able to hang the host or shadow a better-matching plugin.
module Bindery.Core.Routing

open System
open System.Text.RegularExpressions
open Bindery.Core.Protocol

/// Compiling attacker-influenceable regexes is the risk here. Patterns come from a
/// plugin's manifest, which is as trusted as the plugin image — but a plugin author
/// writing an accidental catastrophic-backtracking pattern is ordinary, not malicious,
/// and either way the host must not stall.
[<Literal>]
let MaxPatternLength = 512

let private matchTimeout = TimeSpan.FromMilliseconds 100.0

[<NoComparison; NoEquality>]
type CompiledPlugin =
    { Name: string
      DisplayName: string
      Priority: int
      Patterns: Regex list
      SupportsProbe: bool
      /// Patterns that were rejected, with why. Surfaced in diagnostics rather than
      /// swallowed — a plugin whose regex never compiled would otherwise just look broken.
      RejectedPatterns: (string * string) list }

type Candidate =
    { Plugin: string
      Priority: int
      /// 0.0 to 1.0. Only compared after `Priority`.
      Confidence: float
      Reason: string
      /// Whether the plugin should be asked to confirm over `POST /probe`.
      NeedsProbe: bool }

/// Turn a manifest's patterns into usable regexes, dropping the ones that cannot be
/// trusted. A rejected pattern never disables a plugin: the plugin stays, minus that rule.
let compile (manifest: Manifest) : CompiledPlugin =
    let compiled, rejected =
        manifest.Matches
        |> List.fold
            (fun (good, bad) pattern ->
                if String.IsNullOrWhiteSpace pattern then
                    good, ("", "pattern is empty") :: bad
                elif pattern.Length > MaxPatternLength then
                    good, (pattern, sprintf "pattern is longer than %d characters" MaxPatternLength) :: bad
                else
                    try
                        let regex =
                            Regex(
                                pattern,
                                RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant ||| RegexOptions.Compiled,
                                matchTimeout
                            )

                        regex :: good, bad
                    with :? ArgumentException as ex ->
                        good, (pattern, ex.Message) :: bad)
            ([], [])

    { Name = manifest.Name
      DisplayName = manifest.DisplayName
      Priority = manifest.Priority
      Patterns = List.rev compiled
      SupportsProbe = manifest.Capabilities.Probe
      RejectedPatterns = List.rev rejected }

let private isMatch (regex: Regex) (url: string) =
    try
        regex.IsMatch url
    with :? RegexMatchTimeoutException ->
        // A pattern that times out is a pattern that does not match. Never a host stall.
        false

/// Candidates for a URL, best first.
///
/// Ordering is `priority` descending, then confidence, then name for stability. A plugin
/// that declares `probe` is always offered as a candidate even when no regex matched:
/// probing exists precisely for URLs the patterns cannot express.
let candidates (plugins: CompiledPlugin list) (url: string) : Candidate list =
    if String.IsNullOrWhiteSpace url then
        []
    else
        plugins
        |> List.choose (fun plugin ->
            match plugin.Patterns |> List.tryFind (fun regex -> isMatch regex url) with
            | Some regex ->
                Some
                    { Plugin = plugin.Name
                      Priority = plugin.Priority
                      Confidence = 1.0
                      Reason = sprintf "matched %s" (regex.ToString())
                      NeedsProbe = plugin.SupportsProbe }
            | None when plugin.SupportsProbe ->
                Some
                    { Plugin = plugin.Name
                      Priority = plugin.Priority
                      // Below any pattern match, so a plugin that claims the URL outright
                      // is tried before one that merely might.
                      Confidence = 0.0
                      Reason = "no pattern matched; plugin supports probing"
                      NeedsProbe = true }
            | None -> None)
        |> List.sortWith (fun left right ->
            match compare right.Priority left.Priority with
            | 0 ->
                match compare right.Confidence left.Confidence with
                | 0 -> compare left.Plugin right.Plugin
                | other -> other
            | other -> other)

/// Fold a probe answer into a candidate. A plugin that declines is removed entirely.
let applyProbe (result: ProbeResult) (candidate: Candidate) : Candidate option =
    if not result.Supported then
        None
    else
        Some
            { candidate with
                Confidence = result.Confidence
                Reason = result.Reason |> Option.defaultValue "confirmed by probe"
                NeedsProbe = false }

/// Re-sort after probing, when confidences have changed.
let rank (candidates: Candidate list) =
    candidates
    |> List.sortWith (fun left right ->
        match compare right.Priority left.Priority with
        | 0 ->
            match compare right.Confidence left.Confidence with
            | 0 -> compare left.Plugin right.Plugin
            | other -> other
        | other -> other)
