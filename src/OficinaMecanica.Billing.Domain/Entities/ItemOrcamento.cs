using OficinaMecanica.Billing.Domain.Common;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;

namespace OficinaMecanica.Billing.Domain.Entities;

public class ItemOrcamento : Entity
{
    public TipoItemOrcamento Tipo { get; private set; }
    public string Nome { get; private set; } = string.Empty;
    public decimal PrecoUnitario { get; private set; }
    public int Quantidade { get; private set; }
    public decimal Subtotal => PrecoUnitario * Quantidade;

    private ItemOrcamento() { } // EF Core

    public ItemOrcamento(TipoItemOrcamento tipo, string nome, decimal precoUnitario, int quantidade)
    {
        if (string.IsNullOrWhiteSpace(nome))
            throw new DomainException("O nome do item do orçamento é obrigatório.");

        if (quantidade <= 0)
            throw new DomainException("A quantidade do item do orçamento deve ser maior que zero.");

        if (quantidade > LimitesDeValor.QuantidadeMaxima)
            throw new DomainException("A quantidade do item do orçamento excede o limite permitido.");

        if (precoUnitario < 0)
            throw new DomainException("O preço unitário do item do orçamento não pode ser negativo.");

        if (precoUnitario > LimitesDeValor.PrecoUnitarioMaximo)
            throw new DomainException("O preço unitário do item do orçamento excede o limite permitido.");

        if (ValorMonetario.TemMaisDeDuasCasas(precoUnitario))
            throw new DomainException("O preço unitário do item do orçamento deve ter no máximo duas casas decimais.");

        Tipo = tipo;
        Nome = nome;
        PrecoUnitario = precoUnitario;
        Quantidade = quantidade;
    }
}
