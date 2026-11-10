namespace OficinaMecanica.Contracts.Commands;

public sealed record SolicitarPagamento(
    Guid MessageId,
    Guid OrdemDeServicoId,
    DateTimeOffset OcorridoEm,
    Guid OrcamentoId,
    DateTimeOffset ExpiraEm);
