namespace OficinaMecanica.Billing.Domain.Interfaces;

using OficinaMecanica.Billing.Domain.Entities;

public interface IPagamentoRepository
{
    Task<Pagamento?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Pagamento?> ObterPorOrdemDeServicoIdAsync(Guid ordemDeServicoId, CancellationToken cancellationToken = default);
    // Ids dos pagamentos que o reconciliador precisa revisitar: com estorno pendente (ver Pagamento.EstornoPendente)
    // ou com aprovação duplicada ainda sem estorno.
    Task<IReadOnlyList<Guid>> ListarIdsParaReconciliacaoAsync(int limite, CancellationToken cancellationToken = default);
    Task AdicionarAsync(Pagamento pagamento, CancellationToken cancellationToken = default);
}
