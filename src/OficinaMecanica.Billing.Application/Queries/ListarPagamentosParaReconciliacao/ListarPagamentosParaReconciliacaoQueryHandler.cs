using MediatR;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Application.Queries.ListarPagamentosParaReconciliacao;

public class ListarPagamentosParaReconciliacaoQueryHandler : IRequestHandler<ListarPagamentosParaReconciliacaoQuery, IReadOnlyList<Guid>>
{
    private readonly IPagamentoRepository _pagamentoRepository;

    public ListarPagamentosParaReconciliacaoQueryHandler(IPagamentoRepository pagamentoRepository)
    {
        _pagamentoRepository = pagamentoRepository;
    }

    public Task<IReadOnlyList<Guid>> Handle(ListarPagamentosParaReconciliacaoQuery request, CancellationToken cancellationToken) =>
        _pagamentoRepository.ListarIdsParaReconciliacaoAsync(request.Limite, cancellationToken);
}
