namespace OficinaMecanica.Billing.Domain.Interfaces;

using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;

public interface IOrcamentoRepository
{
    Task<Orcamento?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Orcamento?> ObterPorOrdemDeServicoIdAsync(Guid ordemDeServicoId, CancellationToken cancellationToken = default);
    Task<IEnumerable<Orcamento>> ListarAsync(int pagina, int tamanhoPagina, StatusOrcamento? filtroStatus = null, CancellationToken cancellationToken = default);
    Task<int> ContarAsync(StatusOrcamento? filtroStatus = null, CancellationToken cancellationToken = default);
    Task AdicionarAsync(Orcamento orcamento, CancellationToken cancellationToken = default);
}
