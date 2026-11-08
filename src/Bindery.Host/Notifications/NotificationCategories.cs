namespace Bindery.Host.Notifications;

public sealed record NotificationCategory(string Key, string Group, string Label, string Description, bool DefaultOn);

/// <summary>
/// Everything Bindery may tell a person about, and whether it does so unless told otherwise.
/// </summary>
/// <remarks>
/// The vocabulary is the host's, not any plugin's: circulation categories are produced by
/// <c>Circulation.Event.Category</c> for every circulation plugin alike, and download
/// categories by the download pipeline for every downloader. A new source adds its keys here
/// and inherits preferences, the outbox, retries, and every channel.
/// </remarks>
public static class NotificationCategories
{
    public const string Test = "test";
    public const string DownloadFailed = "download-failed";
    public const string DownloadFiled = "download-filed";

    public static IReadOnlyList<NotificationCategory> All { get; } =
    [
        new("hold-ready", "Library holds", "Ready for you",
            "A hold is waiting on the shelf, or ready to borrow in the app.", true),
        new("pickup-reminder", "Library holds", "Pickup deadline approaching",
            "A ready hold has not been collected and its deadline is close.", true),
        new("hold-in-transit", "Library holds", "On its way",
            "A copy has been assigned and is travelling to your branch.", true),
        new("hold-ended", "Library holds", "Hold ended unexpectedly",
            "A hold expired on the shelf, or was cancelled by the library or on its own site.", true),
        new("due-reminder", "Library loans", "Due soon",
            "Something you have checked out is due back in a few days.", true),
        new("overdue", "Library loans", "Overdue",
            "Something you have checked out is past its due date.", true),
        new(DownloadFailed, "Downloads", "Download failed",
            "A download you started failed and will not be retried.", true),
        new(DownloadFiled, "Downloads", "Book filed",
            "A download you started finished and the book is on the shelf.", false),
    ];

    public static NotificationCategory? Find(string key) =>
        All.FirstOrDefault(category => string.Equals(category.Key, key, StringComparison.Ordinal));
}
