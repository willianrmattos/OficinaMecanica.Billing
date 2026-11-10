using MediatR;
using OficinaMecanica.Contracts.Common;

namespace OficinaMecanica.Billing.Application.Commands.GerarOrcamento;

public record GerarOrcamentoCommand(
    Guid OrdemDeServicoId,
    string Numero,
    decimal ValorTotal,
    DateTimeOffset ValidoAte,
    string? Laudo,
    IReadOnlyList<ItemDeServicoMensagem> Servicos,
    IReadOnlyList<ItemDePecaMensagem> Pecas
) : IRequest;
