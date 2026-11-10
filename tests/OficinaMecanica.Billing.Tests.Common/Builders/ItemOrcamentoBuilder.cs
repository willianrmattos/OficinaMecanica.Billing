using Bogus;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;

namespace OficinaMecanica.Billing.Tests.Common.Builders;

public class ItemOrcamentoBuilder
{
    private static readonly Faker Faker = new("pt_BR");

    private TipoItemOrcamento _tipo = TipoItemOrcamento.Servico;
    private string _nome = Faker.Commerce.ProductName();
    private decimal _precoUnitario = Math.Round(Faker.Random.Decimal(10m, 500m), 2);
    private int _quantidade = Faker.Random.Int(1, 5);

    public ItemOrcamentoBuilder DoTipo(TipoItemOrcamento tipo) { _tipo = tipo; return this; }
    public ItemOrcamentoBuilder ComNome(string nome) { _nome = nome; return this; }
    public ItemOrcamentoBuilder ComPrecoUnitario(decimal precoUnitario) { _precoUnitario = precoUnitario; return this; }
    public ItemOrcamentoBuilder ComQuantidade(int quantidade) { _quantidade = quantidade; return this; }

    public ItemOrcamento Build() => new(_tipo, _nome, _precoUnitario, _quantidade);
}
