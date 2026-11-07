namespace OficinaMecanica.Contracts.Events;

public sealed record OrcamentoNaoGerado(
    Guid MessageId,
    Guid OrdemDeServicoId,
    DateTimeOffset OcorridoEm,
    string Motivo);
