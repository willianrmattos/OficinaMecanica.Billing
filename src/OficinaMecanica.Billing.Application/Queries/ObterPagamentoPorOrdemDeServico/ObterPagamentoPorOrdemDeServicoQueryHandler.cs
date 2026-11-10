using MediatR;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Application.Queries.ObterPagamentoPorOrdemDeServico;

public class ObterPagamentoPorOrdemDeServicoQueryHandler : IRequestHandler<ObterPagamentoPorOrdemDeServicoQuery, PagamentoDto?>
{
    private readonly IPagamentoRepository _pagamentoRepository;

    public ObterPagamentoPorOrdemDeServicoQueryHandler(IPagamentoRepository pagamentoRepository)
    {
        _pagamentoRepository = pagamentoRepository;
    }

    public async Task<PagamentoDto?> Handle(ObterPagamentoPorOrdemDeServicoQuery request, CancellationToken cancellationToken)
    {
        var pagamento = await _pagamentoRepository.ObterPorOrdemDeServicoIdAsync(request.OrdemDeServicoId, cancellationToken);
        return pagamento is null ? null : PagamentoMapper.ToDto(pagamento);
    }
}
