using MediatR;

namespace OficinaMecanica.Billing.Application.Commands.SolicitarPagamento;

public record SolicitarPagamentoCommand(
    Guid OrdemDeServicoId,
    Guid OrcamentoId,
    DateTimeOffset ExpiraEm
) : IRequest;
