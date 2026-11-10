using MediatR;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Domain.Enums;

namespace OficinaMecanica.Billing.Application.Queries.ListarOrcamentos;

public record ListarOrcamentosQuery(
    int Pagina = 1,
    int TamanhoPagina = 10,
    StatusOrcamento? FiltroStatus = null
) : IRequest<PaginacaoResultDto<OrcamentoDto>>;
