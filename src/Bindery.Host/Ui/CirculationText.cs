using System.Globalization;
using Bindery.Core;
using Bindery.Host.Data;
using CirculationCore = Bindery.Core.Circulation;

namespace Bindery.Host.Ui;

/// <summary>The physical/digital badge, drawn once in <c>_MediumBadge.cshtml</c>.</summary>
public sealed record MediumBadgeView(bool IsDigital, string? Detail);

/// <summary>Availability as one plain sentence.</summary>
public static class AvailabilityText
{
    public static string For(CirculationCore.CatalogItem item)
    {
        var a = item.Availability;
        var copies = a.Copies.OrNullable();
        var free = a.AvailableCopies.OrNullable();
        var holds = a.Holds.OrNullable();
        var digital = item.Medium.IsDigital;

        var status = a.Status switch
        {
            _ when a.Status.IsAvailable => digital ? "Available to borrow now" : "On the shelf",
            _ when a.Status.IsOnOrder => "On order",
            _ when a.Status.IsLibraryUseOnly => "Library use only",
            _ when a.Status.IsNotHoldableStatus => "Not lendable",
            _ when a.Status.IsUnavailable => digital ? "All copies borrowed" : "All copies checked out",
            _ => "Availability unknown"
        };

        var parts = new List<string> { status };

        if (copies is { } total && free is { } available)
        {
            parts.Add($"{available} of {total} {(digital ? "licences" : "copies")} free");
        }
        else if (copies is { } only)
        {
            parts.Add(digital
                ? $"{only} {(only == 1 ? "licence" : "licences")}"
                : $"{only} {(only == 1 ? "copy" : "copies")}");
        }

        if (holds is > 0)
        {
            parts.Add($"{holds} {(holds == 1 ? "hold" : "holds")} ahead");
        }

        return string.Join(" · ", parts);
    }
}

/// <summary>How a tracked hold or loan reads on the board.</summary>
public static class CirculationStateText
{
    private static string Day(DateTimeOffset value) =>
        value.ToLocalTime().ToString("ddd d MMM", CultureInfo.InvariantCulture);

    public static bool IsDigital(CirculationItemEntity item) => item.Medium == Protocol.Medium.Digital.Wire;

    public static string Label(string state) => state switch
    {
        "pending" => "In the queue",
        "in_transit" => "On its way",
        "ready" => "Ready",
        "suspended" => "Paused",
        "checked_out" => "Checked out",
        "overdue" => "Overdue",
        "collected" => "Picked up",
        "expired" => "Expired",
        "cancelled" => "Cancelled",
        "closed" => "Removed",
        "returned" => "Returned",
        _ => state
    };

    /// <summary>The one line of detail that matters for this state.</summary>
    public static string Detail(CirculationItemEntity item)
    {
        var digital = IsDigital(item);

        return item.State switch
        {
            "pending" when item.QueuePosition is { } position && item.QueueSize is { } size =>
                $"#{position} of {size} in line",
            "pending" when item.QueuePosition is { } position => $"#{position} in line",
            "pending" => "Waiting for a copy",
            "in_transit" => item.PickupLocation is { } to ? $"Travelling to {to}" : "Travelling to your branch",
            "ready" when digital => item.ReadyUntil is { } until ? $"Borrow in the app by {Day(until)}" : "Borrow it in the app",
            "ready" => (item.PickupLocation is { } at ? $"At {at}" : "At your branch")
                       + (item.ReadyUntil is { } by ? $" — pick up by {Day(by)}" : string.Empty),
            "suspended" => item.SuspendedUntil is { } resume ? $"Paused until {Day(resume)}" : "Paused",
            "checked_out" => item.DueAt is { } due ? $"Due {Day(due)}" : "Checked out",
            "overdue" => item.DueAt is { } was ? $"Was due {Day(was)}" : "Past due",
            _ => Label(item.State) + " " + Day(item.StateChangedAt)
        };
    }

    /// <summary>Whether a deadline is close enough to draw attention to.</summary>
    public static bool IsUrgent(CirculationItemEntity item) =>
        item.State == "overdue"
        || (item.State == "ready" && item.ReadyUntil is { } until && until - DateTimeOffset.UtcNow < TimeSpan.FromDays(2))
        || (item.State == "checked_out" && item.DueAt is { } due && due - DateTimeOffset.UtcNow < TimeSpan.FromDays(3));
}
