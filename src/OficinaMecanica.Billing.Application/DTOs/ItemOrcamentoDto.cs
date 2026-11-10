namespace OficinaMecanica.Billing.Application.DTOs;

public record ItemOrcamentoDto(
    string Tipo,
    string Nome,
    decimal PrecoUnitario,
    int Quantidade,
    decimal Subtotal
);
