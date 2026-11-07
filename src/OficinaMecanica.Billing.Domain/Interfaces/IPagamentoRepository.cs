namespace OficinaMecanica.Billing.Domain.Interfaces;

using OficinaMecanica.Billing.Domain.Entities;

public interface IPagamentoRepository
{
    Task<Pagamento?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Pagamento?> ObterPorOrdemDeServicoIdAsync(Guid ordemDeServicoId, CancellationToken cancellationToken = default);
    Task AdicionarAsync(Pagamento pagamento, CancellationToken cancellationToken = default);
}
