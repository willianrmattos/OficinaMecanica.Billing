namespace OficinaMecanica.Billing.Application.DTOs;

public record OrcamentoDto(
    Guid Id,
    Guid OrdemDeServicoId,
    string Numero,
    string Status,
    decimal ValorTotal,
    DateTimeOffset ValidoAte,
    DateTimeOffset CriadoEm,
    string? Laudo,
    string? MotivoCancelamento,
    IEnumerable<ItemOrcamentoDto> Itens
);
