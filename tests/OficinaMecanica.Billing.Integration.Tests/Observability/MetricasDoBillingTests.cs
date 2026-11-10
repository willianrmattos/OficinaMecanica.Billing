using System.Diagnostics.Metrics;
using FluentAssertions;
using OficinaMecanica.Billing.Infrastructure.Observability;

namespace OficinaMecanica.Billing.Integration.Tests.Observability;

public class MetricasDoBillingTests
{
    private static Dictionary<string, long> Coletar(Action<MetricasDoBilling> acao)
    {
        var medicoes = new Dictionary<string, long>();
        using var metricas = new MetricasDoBilling();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrumento, ouvinte) =>
            {
                if (instrumento.Meter.Name == MetricasDoBilling.NomeDoMeter)
                    ouvinte.EnableMeasurementEvents(instrumento);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrumento, valor, _, _) =>
            medicoes[instrumento.Name] = medicoes.GetValueOrDefault(instrumento.Name) + valor);
        listener.Start();

        acao(metricas);

        return medicoes;
    }

    [Fact]
    public void NomeDoMeter_DeveSerOficinaMecanicaBilling()
    {
        MetricasDoBilling.NomeDoMeter.Should().Be("OficinaMecanica.Billing");
    }

    [Theory]
    [InlineData("divergencias_de_pagamento_total")]
    [InlineData("aprovacoes_tardias_total")]
    [InlineData("aprovacoes_duplicadas_total")]
    [InlineData("orcamentos_gerados_total")]
    [InlineData("pagamentos_aprovados_total")]
    [InlineData("pagamentos_recusados_total")]
    [InlineData("pagamentos_recusados_pelo_gateway_total")]
    [InlineData("pagamentos_estornados_total")]
    [InlineData("estornos_abandonados_total")]
    public void CadaContador_DeveIncrementarSomenteOProprioInstrumento(string instrumento)
    {
        var medicoes = Coletar(m =>
        {
            switch (instrumento)
            {
                case "divergencias_de_pagamento_total": m.RegistrarDivergenciaDePagamento(); break;
                case "aprovacoes_tardias_total": m.RegistrarAprovacaoTardia(); break;
                case "aprovacoes_duplicadas_total": m.RegistrarAprovacaoDuplicada(); break;
                case "orcamentos_gerados_total": m.RegistrarOrcamentoGerado(); break;
                case "pagamentos_aprovados_total": m.RegistrarPagamentoAprovado(); break;
                case "pagamentos_recusados_total": m.RegistrarPagamentoRecusado(); break;
                case "pagamentos_recusados_pelo_gateway_total": m.RegistrarPagamentoRecusadoPeloGateway(); break;
                case "pagamentos_estornados_total": m.RegistrarPagamentoEstornado(); break;
                default: m.RegistrarEstornoAbandonado(); break;
            }
        });

        medicoes.Should().Equal(new Dictionary<string, long> { [instrumento] = 1 });
    }

    [Fact]
    public void ChamadasRepetidas_DevemSomar()
    {
        var medicoes = Coletar(m =>
        {
            m.RegistrarPagamentoAprovado();
            m.RegistrarPagamentoAprovado();
            m.RegistrarPagamentoAprovado();
        });

        medicoes["pagamentos_aprovados_total"].Should().Be(3);
    }
}
