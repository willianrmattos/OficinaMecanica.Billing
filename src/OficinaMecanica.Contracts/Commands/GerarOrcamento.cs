using OficinaMecanica.Contracts.Common;

namespace OficinaMecanica.Contracts.Commands;

public sealed record GerarOrcamento(
    Guid MessageId,
    Guid OrdemDeServicoId,
    DateTimeOffset OcorridoEm,
    string Numero,
    decimal ValorTotal,
    DateTimeOffset ValidoAte,
    string? Laudo,
    IReadOnlyList<ItemDeServicoMensagem> Servicos,
    IReadOnlyList<ItemDePecaMensagem> Pecas);
