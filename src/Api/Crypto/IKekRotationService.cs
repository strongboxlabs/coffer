namespace Coffer.Api.Crypto;

/// <summary>
/// The master-KEK re-wrap, as <see cref="MasterKeyRotationCoordinator"/> consumes
/// it: given an old and a new key, re-wrap every wrapped value in one transaction,
/// or verify they all open and write nothing.
/// </summary>
/// <remarks>
/// A seam, in the same spirit as <c>IWebAuthnService</c> and
/// <c>IApplicationRestarter</c>. The coordinator's job is ORDER — archive, write,
/// re-wrap, roll back — and its rollback branch is the one path that cannot be
/// reached by any real input, since <see cref="KekRotationService"/>'s transaction
/// either commits or throws on its own terms. Without this interface that branch
/// would be untestable, and it is the branch that keeps a failed rotation from
/// leaving an install holding a key that opens nothing.
/// </remarks>
public interface IKekRotationService
{
    /// <inheritdoc cref="KekRotationService.RotateAsync"/>
    Task<RotationResult> RotateAsync(
        MasterKey oldKey, MasterKey newKey, bool dryRun, CancellationToken ct = default);
}
