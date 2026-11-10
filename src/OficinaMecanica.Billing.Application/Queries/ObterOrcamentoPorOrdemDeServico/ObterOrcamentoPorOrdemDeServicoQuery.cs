using MediatR;
using OficinaMecanica.Billing.Application.DTOs;

namespace OficinaMecanica.Billing.Application.Queries.ObterOrcamentoPorOrdemDeServico;

public record ObterOrcamentoPorOrdemDeServicoQuery(Guid OrdemDeServicoId) : IRequest<OrcamentoDto?>;
