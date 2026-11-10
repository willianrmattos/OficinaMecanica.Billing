using MediatR;

namespace OficinaMecanica.Billing.Application.Commands.CancelarPagamento;

public record CancelarPagamentoCommand(
    Guid OrdemDeServicoId,
    Guid? PagamentoId,
    string Motivo
) : IRequest;
