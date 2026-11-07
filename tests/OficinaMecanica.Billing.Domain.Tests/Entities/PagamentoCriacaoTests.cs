using FluentAssertions;
using OficinaMecanica.Billing.Domain.Common;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;

namespace OficinaMecanica.Billing.Domain.Tests.Entities;

public class PagamentoCriacaoTests
{
    private static readonly DateTimeOffset Agora = RelogioFixo.InstanteBase;

    [Fact]
    public void Construtor_ComDadosValidos_DeveNascerPendenteSemPreferencia()
    {
        var orcamento = new OrcamentoBuilder().Aprovado();

        var pagamento = new PagamentoBuilder().DoOrcamento(orcamento).ExpiraEm(Agora.AddDays(2)).Pendente();

        pagamento.OrcamentoId.Should().Be(orcamento.Id);
        pagamento.OrdemDeServicoId.Should().Be(orcamento.OrdemDeServicoId);
        pagamento.Valor.Should().Be(orcamento.ValorTotal);
        pagamento.Status.Should().Be(StatusPagamento.Pendente);
        pagamento.ExpiraEm.Should().Be(Agora.AddDays(2));
        pagamento.CriadoEm.Should().Be(Agora);
        pagamento.PreferenceId.Should().BeNull();
        pagamento.LinkPagamento.Should().BeNull();
        pagamento.GatewayPaymentIdAprovado.Should().BeNull();
        pagamento.Tentativas.Should().BeEmpty();
        pagamento.Estorno.Should().BeNull();
        pagamento.AprovacaoTardia.Should().BeFalse();
    }

    [Fact]
    public void Construtor_SemOrcamento_DeveLancarExcecao()
    {
        var act = () => new PagamentoBuilder().ComOrcamentoId(Guid.Empty).Pendente();

        act.Should().Throw<DomainException>().WithMessage("*orçamento*obrigatório*");
    }

    [Fact]
    public void Construtor_SemOrdemDeServico_DeveLancarExcecao()
    {
        var act = () => new PagamentoBuilder().DaOrdemDeServico(Guid.Empty).Pendente();

        act.Should().Throw<DomainException>().WithMessage("*ordem de serviço*obrigatória*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Construtor_ComValorNaoPositivo_DeveLancarExcecao(int valor)
    {
        var act = () => new PagamentoBuilder().ComValor(valor).Pendente();

        act.Should().Throw<DomainException>().WithMessage("*valor*maior que zero*");
    }

    [Theory]
    [InlineData(100.001)]
    [InlineData(0.005)]
    public void Construtor_ComMaisDeDuasCasasDecimais_DeveLancarExcecao(double valor)
    {
        var act = () => new PagamentoBuilder().ComValor((decimal)valor).Pendente();

        act.Should().Throw<DomainException>().WithMessage("*valor*duas casas decimais*");
    }

    [Theory]
    [InlineData(100)]
    [InlineData(100.5)]
    [InlineData(100.55)]
    public void Construtor_ComAteDuasCasasDecimais_DeveCriarPagamento(double valor)
    {
        var pagamento = new PagamentoBuilder().ComValor((decimal)valor).Pendente();

        pagamento.Valor.Should().Be((decimal)valor);
    }

    [Fact]
    public void Construtor_ComValorNoTetoPermitido_DeveCriarPagamento()
    {
        var pagamento = new PagamentoBuilder().ComValor(LimitesDeValor.ValorTotalMaximo).Pendente();

        pagamento.Valor.Should().Be(1_000_000.00m);
    }

    [Fact]
    public void Construtor_ComValorUmCentavoAcimaDoTeto_DeveLancarExcecao()
    {
        var act = () => new PagamentoBuilder().ComValor(LimitesDeValor.ValorTotalMaximo + 0.01m).Pendente();

        act.Should().Throw<DomainException>().WithMessage("*valor do pagamento*limite*");
    }

    [Fact]
    public void Construtor_ComPrazoNoPassado_DeveLancarExcecao()
    {
        var act = () => new PagamentoBuilder().ExpiraEm(Agora.AddMinutes(-1)).Pendente();

        act.Should().Throw<DomainException>().WithMessage("*prazo*futuro*");
    }

    [Fact]
    public void Construtor_ComPrazoIgualAoAgora_DeveLancarExcecao()
    {
        var act = () => new PagamentoBuilder().ExpiraEm(Agora).Pendente();

        act.Should().Throw<DomainException>().WithMessage("*prazo*futuro*");
    }

    [Fact]
    public void DefinirPreferencia_PagamentoPendente_DeveGuardarPreferenciaELink()
    {
        var pagamento = new PagamentoBuilder().Pendente();

        pagamento.DefinirPreferencia("pref-123", "https://mp.test/checkout?pref_id=pref-123");

        pagamento.PreferenceId.Should().Be("pref-123");
        pagamento.LinkPagamento.Should().Be("https://mp.test/checkout?pref_id=pref-123");
        pagamento.Status.Should().Be(StatusPagamento.Pendente);
    }

    [Fact]
    public void DefinirPreferencia_DeNovoNoPendente_DeveSubstituirAPreferenciaAnterior()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();

        pagamento.DefinirPreferencia("pref-nova", "https://mp.test/checkout?pref_id=pref-nova");

        pagamento.PreferenceId.Should().Be("pref-nova");
    }

    [Theory]
    [InlineData("", "https://mp.test/checkout", "*preferência*obrigatório*")]
    [InlineData("  ", "https://mp.test/checkout", "*preferência*obrigatório*")]
    [InlineData("pref-123", "", "*link*obrigatório*")]
    [InlineData("pref-123", "   ", "*link*obrigatório*")]
    public void DefinirPreferencia_ComDadosVazios_DeveLancarExcecao(string preferenceId, string link, string mensagem)
    {
        var pagamento = new PagamentoBuilder().Pendente();

        var act = () => pagamento.DefinirPreferencia(preferenceId, link);

        act.Should().Throw<DomainException>().WithMessage(mensagem);
    }

    [Fact]
    public void DefinirPreferencia_PagamentoCancelado_DeveLancarExcecao()
    {
        var pagamento = new PagamentoBuilder().Cancelado();

        var act = () => pagamento.DefinirPreferencia("pref-123", "https://mp.test/checkout");

        act.Should().Throw<DomainException>().WithMessage("*pagamento pendente*Cancelado*");
    }
}
