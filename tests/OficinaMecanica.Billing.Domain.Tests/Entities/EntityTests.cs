using FluentAssertions;

namespace OficinaMecanica.Billing.Domain.Tests.Entities;

public class EntityTests
{
    [Fact]
    public void Construtor_DeveGerarIdentificadoresDistintosParaCadaEntidade()
    {
        var primeiro = new OrcamentoBuilder().Gerado();
        var segundo = new OrcamentoBuilder().Gerado();

        primeiro.Id.Should().NotBe(Guid.Empty);
        primeiro.Id.Should().NotBe(segundo.Id);
    }

    [Fact]
    public void Equals_MesmaInstancia_DeveSerVerdadeiro()
    {
        var orcamento = new OrcamentoBuilder().Gerado();

        orcamento.Equals(orcamento).Should().BeTrue();
    }

    [Fact]
    public void Equals_EntidadesDiferentes_DeveSerFalso()
    {
        var primeiro = new OrcamentoBuilder().Gerado();
        var segundo = new OrcamentoBuilder().Gerado();

        primeiro.Equals(segundo).Should().BeFalse();
    }

    [Fact]
    public void Equals_TiposDiferentesComMesmoId_DeveSerFalso()
    {
        var orcamento = new OrcamentoBuilder().Gerado();
        var pagamento = new PagamentoBuilder().Pendente();

        orcamento.Equals(pagamento).Should().BeFalse();
    }

    [Fact]
    public void Equals_ObjetoQueNaoEEntidade_DeveSerFalso()
    {
        var orcamento = new OrcamentoBuilder().Gerado();

        orcamento.Equals("texto").Should().BeFalse();
        orcamento.Equals(null).Should().BeFalse();
    }

    [Fact]
    public void GetHashCode_DeveSerOHashDoId()
    {
        var orcamento = new OrcamentoBuilder().Gerado();

        orcamento.GetHashCode().Should().Be(orcamento.Id.GetHashCode());
    }

    [Fact]
    public void IncrementVersion_DeveSomarUmAVersaoDoAgregado()
    {
        var pagamento = new PagamentoBuilder().Pendente();
        var versaoInicial = pagamento.Version;

        pagamento.IncrementVersion();
        pagamento.IncrementVersion();

        pagamento.Version.Should().Be(versaoInicial + 2);
    }
}
