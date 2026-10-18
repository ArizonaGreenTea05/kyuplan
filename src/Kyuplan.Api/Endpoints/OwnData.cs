using System.Security.Claims;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>
/// What is written about the members a user is themselves (comments, evaluations) is hidden from them unless they have the "own" permission
/// of that kind (<see cref="Permission.CommentsViewOwn"/>, <see cref="Permission.RatingsViewOwn"/>), and that one does not depend on the general
/// view permission: with it they read what is written about themselves, without the view permission they read nothing about anybody else,
/// and with the view permission alone they read about everybody but themselves.
/// </summary>
public static class OwnData
{
    public static int UserId(this ClaimsPrincipal principal) => int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>The permission that applies to what is written about this member: the "own" one for the user's own members, else the general one.</summary>
    public static string ReadPermission(bool isOwn, string view, string viewOwn) => isOwn ? viewOwn : view;

    /// <summary>For the members of a list: whether the user may read what is written about each, given the ids of the members they are.</summary>
    public static Func<int, bool> MayRead(ClaimsPrincipal principal, IReadOnlySet<int> own, string view, string viewOwn) =>
        memberId => principal.Has(ReadPermission(own.Contains(memberId), view, viewOwn));

    /// <summary>Refuses (403) unless the user may read what is written about this member.</summary>
    /// <exception cref="ForbiddenException"/>
    public static async Task EnsureMayReadAsync(ClaimsPrincipal principal, int memberId, string view, string viewOwn, MemberService members, string what, CancellationToken cancellationToken)
    {
        var isOwn = await members.IsUserAsync(memberId, principal.UserId(), cancellationToken);
        if (!principal.Has(ReadPermission(isOwn, view, viewOwn)))
            throw new ForbiddenException(isOwn
                ? $"You may not read {what} about yourself (that needs {viewOwn})."
                : $"You may not read {what} about other members (that needs {view}).");
    }
}
