using MediatR;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Application.Queries.ObterOrcamentoPorOrdemDeServico;

public class ObterOrcamentoPorOrdemDeServicoQueryHandler : IRequestHandler<ObterOrcamentoPorOrdemDeServicoQuery, OrcamentoDto?>
{
    private readonly IOrcamentoRepository _orcamentoRepository;

    public ObterOrcamentoPorOrdemDeServicoQueryHandler(IOrcamentoRepository orcamentoRepository)
    {
        _orcamentoRepository = orcamentoRepository;
    }

    public async Task<OrcamentoDto?> Handle(ObterOrcamentoPorOrdemDeServicoQuery request, CancellationToken cancellationToken)
    {
        var orcamento = await _orcamentoRepository.ObterPorOrdemDeServicoIdAsync(request.OrdemDeServicoId, cancellationToken);
        return orcamento is null ? null : OrcamentoMapper.ToDto(orcamento);
    }
}
