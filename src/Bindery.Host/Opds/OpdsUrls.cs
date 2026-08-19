using Bindery.Core;
using Microsoft.FSharp.Core;
using CoreOpds = Bindery.Core.Opds;

namespace Bindery.Host.Opds;

/// <summary>
/// Builds the <see cref="CoreOpds.UrlBuilder"/> the core needs, for whichever version of the
/// catalog is being served.
/// </summary>
/// <remarks>
/// Navigation differs between OPDS 1.2 and 2.0 — the two trees have separate roots so a
/// client stays inside the format it started in — but files do not. Downloads, covers, and
/// thumbnails are bytes either way, so both trees point at the same handlers rather than
/// duplicating them under a second prefix.
/// </remarks>
public static class OpdsUrls
{
    public const string AtomPrefix = "/opds";
    public const string JsonPrefix = "/opds/v2";

    public static CoreOpds.UrlBuilder For(string prefix) =>
        new(
            prefix,
            FuncConvert.FromFunc<string, string>(path => $"{prefix}/{path.TrimStart('/')}"),
            FuncConvert.FromFunc<Guid, string>(id => $"{prefix}/book/{id:N}"),
            FuncConvert.FromFunc<Guid, string, string>((id, format) => $"{AtomPrefix}/download/{id:N}.{format}"),
            FuncConvert.FromFunc<Guid, string>(id => $"{AtomPrefix}/cover/{id:N}"),
            FuncConvert.FromFunc<Guid, string>(id => $"{AtomPrefix}/thumb/{id:N}"),
            FuncConvert.FromFunc<string, string>(query => $"{prefix}/search?q={Uri.EscapeDataString(query)}"),
            $"{AtomPrefix}/opensearch.xml");

    public static readonly CoreOpds.UrlBuilder Atom = For(AtomPrefix);

    public static readonly CoreOpds.UrlBuilder Json = For(JsonPrefix);
}
