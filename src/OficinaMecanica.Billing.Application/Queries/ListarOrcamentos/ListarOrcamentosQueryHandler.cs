using MediatR;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Application.Queries.ListarOrcamentos;

public class ListarOrcamentosQueryHandler : IRequestHandler<ListarOrcamentosQuery, PaginacaoResultDto<OrcamentoDto>>
{
    private readonly IOrcamentoRepository _orcamentoRepository;

    public ListarOrcamentosQueryHandler(IOrcamentoRepository orcamentoRepository)
    {
        _orcamentoRepository = orcamentoRepository;
    }

    public async Task<PaginacaoResultDto<OrcamentoDto>> Handle(ListarOrcamentosQuery request, CancellationToken cancellationToken)
    {
        var orcamentos = await _orcamentoRepository.ListarAsync(request.Pagina, request.TamanhoPagina, request.FiltroStatus, cancellationToken);
        var total = await _orcamentoRepository.ContarAsync(request.FiltroStatus, cancellationToken);

        return new PaginacaoResultDto<OrcamentoDto>(
            orcamentos.Select(OrcamentoMapper.ToDto), total, request.Pagina, request.TamanhoPagina,
            (int)Math.Ceiling(total / (double)request.TamanhoPagina)
        );
    }
}
