using MediatR;

namespace OficinaMecanica.Billing.Application.Commands.EstornarPagamento;

public record EstornarPagamentoCommand(
    Guid OrdemDeServicoId,
    Guid PagamentoId,
    string Motivo
) : IRequest;
