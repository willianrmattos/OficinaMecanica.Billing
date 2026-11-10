using FluentAssertions;
using OficinaMecanica.Billing.Application.Mensagens;

namespace OficinaMecanica.Billing.Application.Tests.Mensagens;

public class IdentificadorDeMensagemTests
{
    [Fact]
    public void Deterministico_MesmaOrigemEMesmoTipo_DeveGerarOMesmoId()
    {
        var origem = Guid.NewGuid();

        var primeiro = IdentificadorDeMensagem.Deterministico(origem, "PagamentoAprovado");
        var segundo = IdentificadorDeMensagem.Deterministico(origem, "PagamentoAprovado");

        segundo.Should().Be(primeiro);
        primeiro.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Deterministico_TiposDiferentes_DeveGerarIdsDiferentes()
    {
        var origem = Guid.NewGuid();

        var aprovado = IdentificadorDeMensagem.Deterministico(origem, "PagamentoAprovado");
        var cancelado = IdentificadorDeMensagem.Deterministico(origem, "PagamentoCancelado");

        cancelado.Should().NotBe(aprovado);
    }

    [Fact]
    public void Deterministico_OrigensDiferentes_DeveGerarIdsDiferentes()
    {
        var primeiro = IdentificadorDeMensagem.Deterministico(Guid.NewGuid(), "PagamentoAprovado");
        var segundo = IdentificadorDeMensagem.Deterministico(Guid.NewGuid(), "PagamentoAprovado");

        segundo.Should().NotBe(primeiro);
    }

    [Fact]
    public void Deterministico_DeveSerEstavelEntreVersoes_PoisOConsumidorDeduplicaPeloMessageId()
    {
        var origem = new Guid("00000000-0000-0000-0000-000000000004");

        var id = IdentificadorDeMensagem.Deterministico(origem, "PagamentoSolicitado");

        id.Should().Be(new Guid("683fe1d9-ab6e-011d-fa41-821edb9e3d53"));
    }
}
