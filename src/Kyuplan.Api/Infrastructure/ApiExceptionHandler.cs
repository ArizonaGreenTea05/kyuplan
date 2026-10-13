using Microsoft.AspNetCore.Diagnostics;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Spond;
using Kyuplan.Api.Statistics;

namespace Kyuplan.Api.Infrastructure;

/// <summary>Turns expected failures into proper HTTP statuses instead of a generic 500.</summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            SpondUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Spond is unavailable"),
            GroupNotFoundException => (StatusCodes.Status404NotFound, "Group not found"),
            EventNotFoundException => (StatusCodes.Status404NotFound, "Event not found"),
            ExamNotFoundException => (StatusCodes.Status404NotFound, "Exam not found"),
            SavedFilterNotFoundException => (StatusCodes.Status404NotFound, "Saved filter not found"),
            UserNotFoundException => (StatusCodes.Status404NotFound, "User not found"),
            RoleNotFoundException => (StatusCodes.Status404NotFound, "Role not found"),
            BeltNotFoundException => (StatusCodes.Status404NotFound, "Belt not found"),
            MemberNotFoundException => (StatusCodes.Status404NotFound, "Member not found"),
            CommentNotFoundException => (StatusCodes.Status404NotFound, "Comment not found"),
            TagNotFoundException => (StatusCodes.Status404NotFound, "Tag not found"),
            ForbiddenException => (StatusCodes.Status403Forbidden, "Not allowed"),
            AlreadyExistsException => (StatusCodes.Status409Conflict, "Already exists"),
            BeltInUseException => (StatusCodes.Status409Conflict, "Belt in use"),
            SetupDoneException => (StatusCodes.Status409Conflict, "Already set up"),
            InvalidInputException => (StatusCodes.Status400BadRequest, "Invalid input"),
            LastAdminException => (StatusCodes.Status409Conflict, "Last administrator"),
            SystemRoleException => (StatusCodes.Status409Conflict, "Administrator role"),
            DeleteOwnUserException => (StatusCodes.Status409Conflict, "Cannot delete yourself"),
            WrongPasswordException => (StatusCodes.Status400BadRequest, "Wrong password"),
            // A request body that is missing, is not JSON, or does not fit the contract.
            BadHttpRequestException bad => (bad.StatusCode, "Bad request"),
            _ => (0, null),
        };

        if (title is null) return false;

        if (status >= 500) logger.LogWarning(exception, "{Title}", title);

        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = { Status = status, Title = title, Detail = exception.Message },
        });
    }
}
