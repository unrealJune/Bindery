using System.Globalization;
using System.Text;
using Bindery.Core;
using Bindery.Host.Configuration;
using Bindery.Host.Data;
using CirculationCore = Bindery.Core.Circulation;

namespace Bindery.Host.Circulation;

public sealed record ComposedMessage(string Subject, string Body);

/// <summary>
/// Turns a lifecycle event into words a person can act on.
/// </summary>
/// <remarks>
/// Plain text only. Every value here originated at a library, passed through a plugin, and
/// is going into an email client — none of it is ever treated as markup.
/// </remarks>
public static class CirculationMessages
{
    public static ComposedMessage Compose(
        CirculationAccountEntity account,
        CirculationItemEntity item,
        CirculationCore.Event @event,
        BinderyOptions options)
    {
        var digital = item.Medium == Protocol.Medium.Digital.Wire;
        var title = item.Title;

        var subject = @event switch
        {
            CirculationCore.Event.BecameReady => digital ? $"Ready to borrow: {title}" : $"Ready for pickup: {title}",
            CirculationCore.Event.PickupReminder => digital ? $"Borrow soon: {title}" : $"Pick up soon: {title}",
            _ when @event.IsWentInTransit => $"On its way: {title}",
            _ when @event.IsHoldExpired => $"Hold expired: {title}",
            _ when @event.IsHoldClosed => $"Hold removed: {title}",
            CirculationCore.Event.DueReminder => $"Due soon: {title}",
            _ when @event.IsBecameOverdue => $"Overdue: {title}",
            _ => title
        };

        var medium = digital ? Protocol.Medium.Digital : Protocol.Medium.Physical;
        var body = new StringBuilder();

        body.AppendLine(title);

        if (!string.IsNullOrWhiteSpace(item.Authors))
        {
            body.AppendLine(item.Authors);
        }

        body.AppendLine(digital ? "Digital — borrowed through the library's lending app" : "Physical copy");
        body.AppendLine();
        body.AppendLine(CirculationCore.describe(medium, @event));

        if (!digital && !string.IsNullOrWhiteSpace(item.PickupLocation) && @event is CirculationCore.Event.PickupReminder)
        {
            body.AppendLine($"Pickup branch: {item.PickupLocation}");
        }

        if (item.DueAt is { } due && (@event.IsBecameOverdue || @event is CirculationCore.Event.DueReminder))
        {
            body.AppendLine($"Due: {due.UtcDateTime.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)}");
        }

        body.AppendLine();
        body.Append("Library: ").Append(account.LibraryName);

        if (!string.IsNullOrWhiteSpace(account.DisplayName))
        {
            body.Append(" (").Append(account.DisplayName).Append(')');
        }

        body.AppendLine();

        if (item.Url is not null)
        {
            body.AppendLine($"Catalog record: {item.Url}");
        }

        var root = options.Notifications.Email.LinkBaseUrl ?? options.PublicBaseUrl;

        if (!string.IsNullOrWhiteSpace(root))
        {
            body.AppendLine($"All your holds: {root.TrimEnd('/')}/holds");
        }

        body.AppendLine();
        body.AppendLine("— Bindery. Change what you are told about on the Library cards page.");

        return new ComposedMessage(subject, body.ToString());
    }
}
