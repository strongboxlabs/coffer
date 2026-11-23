using Coffer.Api.Errors;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Endpoints;

/// <summary>
/// The 422 for a write the write primitive refused. The violation's code is already
/// one of <see cref="BusinessError.Codes"/> (a contract test holds the two lists
/// together), so every writer answers a refusal the same way.
/// </summary>
internal static class AnnotationProblem
{
    public static IResult Of(AnnotationViolation violation)
    {
        ArgumentNullException.ThrowIfNull(violation);
        return BusinessError.Problem(violation.Code, violation.Detail);
    }
}
