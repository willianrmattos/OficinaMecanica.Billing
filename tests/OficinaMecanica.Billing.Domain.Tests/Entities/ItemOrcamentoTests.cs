using FluentAssertions;
using OficinaMecanica.Billing.Domain.Common;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;

namespace OficinaMecanica.Billing.Domain.Tests.Entities;

public class ItemOrcamentoTests
{
    [Fact]
    public void Construtor_ComDadosValidos_DeveCriarItemComSubtotal()
    {
        var item = new ItemOrcamentoBuilder()
            .DoTipo(TipoItemOrcamento.Peca)
            .ComNome("Filtro de óleo")
            .ComPrecoUnitario(35.90m)
            .ComQuantidade(2)
            .Build();

        item.Tipo.Should().Be(TipoItemOrcamento.Peca);
        item.Nome.Should().Be("Filtro de óleo");
        item.PrecoUnitario.Should().Be(35.90m);
        item.Quantidade.Should().Be(2);
        item.Subtotal.Should().Be(71.80m);
    }

    [Fact]
    public void Construtor_ComPrecoZero_DeveCriarItemSemCusto()
    {
        var item = new ItemOrcamentoBuilder().ComPrecoUnitario(0m).ComQuantidade(3).Build();

        item.Subtotal.Should().Be(0m);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Construtor_SemNome_DeveLancarExcecao(string nome)
    {
        var act = () => new ItemOrcamentoBuilder().ComNome(nome).Build();

        act.Should().Throw<DomainException>().WithMessage("*nome do item*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Construtor_ComQuantidadeNaoPositiva_DeveLancarExcecao(int quantidade)
    {
        var act = () => new ItemOrcamentoBuilder().ComQuantidade(quantidade).Build();

        act.Should().Throw<DomainException>().WithMessage("*quantidade*maior que zero*");
    }

    [Fact]
    public void Construtor_ComPrecoNegativo_DeveLancarExcecao()
    {
        var act = () => new ItemOrcamentoBuilder().ComPrecoUnitario(-0.01m).Build();

        act.Should().Throw<DomainException>().WithMessage("*preço unitário*negativo*");
    }

    [Fact]
    public void Construtor_ComPrecoComMaisDeDuasCasasDecimais_DeveLancarExcecao()
    {
        var act = () => new ItemOrcamentoBuilder().ComPrecoUnitario(10.005m).Build();

        act.Should().Throw<DomainException>().WithMessage("*preço unitário*duas casas decimais*");
    }

    [Fact]
    public void Construtor_ComPrecoNoTetoPermitido_DeveCriarItem()
    {
        var item = new ItemOrcamentoBuilder().ComPrecoUnitario(LimitesDeValor.PrecoUnitarioMaximo).ComQuantidade(1).Build();

        item.Subtotal.Should().Be(1_000_000.00m);
    }

    [Fact]
    public void Construtor_ComPrecoUmCentavoAcimaDoTeto_DeveLancarExcecao()
    {
        var act = () => new ItemOrcamentoBuilder().ComPrecoUnitario(LimitesDeValor.PrecoUnitarioMaximo + 0.01m).Build();

        act.Should().Throw<DomainException>().WithMessage("*preço unitário*limite*");
    }

    [Fact]
    public void Construtor_ComQuantidadeNoTetoPermitido_DeveCriarItem()
    {
        var item = new ItemOrcamentoBuilder().ComPrecoUnitario(1m).ComQuantidade(LimitesDeValor.QuantidadeMaxima).Build();

        item.Subtotal.Should().Be(10_000m);
    }

    [Fact]
    public void Construtor_ComQuantidadeUmaAcimaDoTeto_DeveLancarExcecao()
    {
        var act = () => new ItemOrcamentoBuilder().ComQuantidade(LimitesDeValor.QuantidadeMaxima + 1).Build();

        act.Should().Throw<DomainException>().WithMessage("*quantidade*limite*");
    }

    [Fact]
    public void Construtor_ComValoresNosMaximosDoTipo_DeveLancarDomainExceptionEmVezDeEstourar()
    {
        var act = () => new ItemOrcamentoBuilder().ComPrecoUnitario(decimal.MaxValue).ComQuantidade(int.MaxValue).Build();

        act.Should().Throw<DomainException>();
    }
}
