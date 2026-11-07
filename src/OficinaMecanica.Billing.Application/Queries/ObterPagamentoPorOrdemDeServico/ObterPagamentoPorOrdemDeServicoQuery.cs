using MediatR;
using OficinaMecanica.Billing.Application.DTOs;

namespace OficinaMecanica.Billing.Application.Queries.ObterPagamentoPorOrdemDeServico;

public record ObterPagamentoPorOrdemDeServicoQuery(Guid OrdemDeServicoId) : IRequest<PagamentoDto?>;
