namespace OficinaMecanica.Billing.Domain.Interfaces;

using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;

public interface ICancelamentoPrevioRepository
{
    Task<CancelamentoPrevio?> ObterAsync(Guid ordemDeServicoId, TipoCancelamentoPrevio tipo, CancellationToken cancellationToken = default);
    Task AdicionarAsync(CancelamentoPrevio cancelamento, CancellationToken cancellationToken = default);
}
