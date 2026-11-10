namespace OficinaMecanica.Billing.Application.DTOs;

public record EstornoDto(
    string Status,
    string? GatewayRefundId,
    int Tentativas,
    string? Erro
);
