using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>What the forms of the manual data page need from the page that owns them.</summary>
internal interface IManualPageHost
{
    bool CanEdit { get; }

    bool IsBusy { get; }

    bool HasGroup { get; }

    ManualGroupDto? SelectedGroup { get; }

    /// <summary>The members of the selected group as the page last read them.</summary>
    IReadOnlyList<ManualMemberRow> Members { get; }

    /// <summary>The time zone events are entered in (the server's).</summary>
    TimeZoneInfo Zone { get; }

    string? ErrorMessage { get; set; }

    /// <summary>A form is about to open: forgets the last error and notice and closes every form and question.</summary>
    void Prepare();

    /// <summary>A form changed in a way that can enable or disable commands and show or hide problems.</summary>
    void StateChanged();

    /// <summary>Runs a change, then lets the shell and the page read what it changed. <paramref name="notice"/> is shown when it worked.</summary>
    Task<bool> ChangeAsync(Func<CancellationToken, Task> change, Func<string> notice);

    /// <summary>Reads the members of a group again; <c>null</c> when that failed (the reason is in <see cref="ErrorMessage"/>).</summary>
    Task<IReadOnlyList<ManualMemberDto>?> ReadMembersAsync(string groupId);

    /// <summary>Makes the group with this id the selected one, once the page knows it.</summary>
    void SelectGroup(string groupId);
}
