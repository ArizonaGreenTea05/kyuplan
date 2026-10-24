using CommunityToolkit.Mvvm.ComponentModel;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>A subgroup of the manual group, with how many members are in it.</summary>
public sealed class ManualSubGroupRow(SubGroupDto subGroup, int memberCount) : ObservableObject
{
    public SubGroupDto SubGroup => subGroup;

    public string Id => subGroup.Id;

    public string Name => subGroup.Name;

    public string CountText => string.Format(Strings.Manual_MemberCount, memberCount);

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>A member of the manual group as the list shows it.</summary>
public sealed class ManualMemberRow(ManualMemberDto member, IReadOnlyList<SubGroupDto> subGroups) : ObservableObject
{
    public ManualMemberDto Member => member;

    public string Id => member.Id;

    public string Name => member.Name;

    public string BirthText => member.BirthDate is { } day ? Display.Date(day) : Display.None;

    /// <summary>The names of the subgroups the member is in, in the order of the group's subgroups.</summary>
    public string SubGroupsText => member.SubGroupIds.Count == 0
        ? Display.None
        : string.Join(", ", subGroups.Where(s => member.SubGroupIds.Contains(s.Id)).Select(s => s.Name));

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>An event of the manual group as the list shows it.</summary>
public sealed class ManualEventRow(ManualEventDto manualEvent, TimeZoneInfo zone) : ObservableObject
{
    public ManualEventDto Event => manualEvent;

    public string Id => manualEvent.Id;

    public string Name => manualEvent.Cancelled ? $"{manualEvent.Name} ({Strings.Manual_CancelledMark})" : manualEvent.Name;

    public string StartText
    {
        get
        {
            var start = TimeZoneInfo.ConvertTime(manualEvent.Start, zone);
            return $"{Display.EventDay(start)}, {start.ToString("t", System.Globalization.CultureInfo.CurrentCulture)}";
        }
    }

    public string InvitedText => string.Format(
        Strings.Manual_InvitedCount, manualEvent.Invitations.Count, manualEvent.Invitations.Count(i => i.Rsvp == ManualRsvp.Accepted));

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>A subgroup to tick in the member form.</summary>
public sealed partial class ManualChoiceRow(string id, string name, bool isSelected) : ObservableObject
{
    public string Id => id;

    public string Name => name;

    [ObservableProperty]
    private bool _isSelected = isSelected;
}

/// <summary>An answer to choose in the event form.</summary>
public sealed class ManualRsvpOption(string value) : ObservableObject
{
    public string Value => value;

    public string Text => value switch
    {
        ManualRsvp.Accepted => Strings.Manual_Rsvp_accepted,
        ManualRsvp.Declined => Strings.Manual_Rsvp_declined,
        ManualRsvp.Other => Strings.Manual_Rsvp_other,
        _ => Strings.Manual_Rsvp_unanswered,
    };

    public static IReadOnlyList<ManualRsvpOption> All { get; } = [.. ManualRsvp.All.Select(v => new ManualRsvpOption(v))];

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>A member of the group in the event form: invited or not, and what they answered if so, and whether they host the event.</summary>
public sealed partial class ManualInviteRow(string memberId, string name, bool isInvited, ManualRsvpOption answer, bool isTrainer = false) : ObservableObject
{
    public string MemberId => memberId;

    public string Name => name;

    /// <summary>Hosts the event: it counts for no statistic of theirs.</summary>
    [ObservableProperty]
    private bool _isTrainer = isTrainer;

    [ObservableProperty]
    private bool _isInvited = isInvited;

    [ObservableProperty]
    private ManualRsvpOption _answer = answer;
}
