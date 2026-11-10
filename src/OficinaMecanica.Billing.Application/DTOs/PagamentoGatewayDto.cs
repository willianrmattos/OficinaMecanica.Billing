namespace OficinaMecanica.Billing.Application.DTOs;

public record PagamentoGatewayDto(
    string Id,
    string Status,
    string? StatusDetalhe,
    decimal Valor,
    string Moeda,
    string? ExternalReference
);
