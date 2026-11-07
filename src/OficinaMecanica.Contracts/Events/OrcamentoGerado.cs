namespace OficinaMecanica.Contracts.Events;

public sealed record OrcamentoGerado(
    Guid MessageId,
    Guid OrdemDeServicoId,
    DateTimeOffset OcorridoEm,
    Guid OrcamentoId,
    decimal ValorTotal,
    DateTimeOffset ValidoAte);
