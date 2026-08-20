using TantoOntManager.Domain.Sessions;
using TantoOntManager.Domain.Unlock;

namespace TantoOntManager.DeviceAdapters.Abstractions;

public interface IUnlockableDeviceAdapter : IOntDeviceAdapter
{
    Task<UnlockResult> UnlockAsync(
        AuthorizedDeviceSession session,
        UnlockOptions options,
        CancellationToken cancellationToken = default);

    Task<bool> RollbackAsync(
        AuthorizedDeviceSession session,
        string ticketId,
        CancellationToken cancellationToken = default);
}
