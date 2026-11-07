using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Infrastructure;
using OficinaMecanica.Billing.Infrastructure.Messaging;
using OficinaMecanica.Billing.Infrastructure.Options;
using OficinaMecanica.Billing.Integration.Tests.Support;

namespace OficinaMecanica.Billing.Integration.Tests.Composition;

public class ValidadoresDeOpcoesTests
{
    // ---- Mensageria ----

    private static ValidateOptionsResult ValidarMensageria(string ambiente, Action<OpcoesDaMensageria> ajustar)
    {
        var opcoes = new OpcoesDaMensageria();
        ajustar(opcoes);
        return new ValidadorDasOpcoesDaMensageria(ConfiguracaoDeTeste.Ambiente(ambiente)).Validate(null, opcoes);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Mensageria_AmbientesDeDesenvolvimentoETeste_DevemAceitarTransporteEmMemoriaESemOutbox(string ambiente)
    {
        var resultado = ValidarMensageria(ambiente, o =>
        {
            o.Transporte = TransporteDaMensageria.EmMemoria;
            o.Persistencia = PersistenciaDaMensageria.Nenhuma;
        });

        resultado.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    public void Mensageria_AmbientesReais_DevemRecusarTransporteEmMemoriaESemOutbox(string ambiente)
    {
        var resultado = ValidarMensageria(ambiente, o =>
        {
            o.Transporte = TransporteDaMensageria.EmMemoria;
            o.Persistencia = PersistenciaDaMensageria.Nenhuma;
        });

        resultado.Failed.Should().BeTrue();
        resultado.Failures.Should().HaveCount(2);
        resultado.Failures.Should().Contain(m => m.Contains("Mensageria:Transporte=EmMemoria só é permitido em Development ou Testing"));
        resultado.Failures.Should().Contain(m => m.Contains("Mensageria:Persistencia=Nenhuma só é permitido em Development ou Testing"));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Development")]
    public void Mensageria_RabbitMqComOutboxDoEf_DeveSerAceitoEmQualquerAmbiente(string ambiente)
    {
        ValidarMensageria(ambiente, _ => { }).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("Production", 0)]
    [InlineData("Production", 9)]
    [InlineData("Development", 0)]
    [InlineData("Development", 9)]
    [InlineData("Testing", 9)]
    public void Mensageria_TransporteSemNome_DeveSerRecusadoEmQualquerAmbiente(string ambiente, int valor)
    {
        var resultado = ValidarMensageria(ambiente, o => o.Transporte = (TransporteDaMensageria)valor);

        resultado.Failures.Should().ContainSingle()
            .Which.Should().Be($"Mensageria:Transporte tem um valor inválido ({valor}); use RabbitMq ou EmMemoria.");
    }

    [Theory]
    [InlineData("Production", 0)]
    [InlineData("Production", 9)]
    [InlineData("Development", 0)]
    [InlineData("Development", 9)]
    [InlineData("Testing", 9)]
    public void Mensageria_PersistenciaSemNome_DeveSerRecusadaEmQualquerAmbiente(string ambiente, int valor)
    {
        var resultado = ValidarMensageria(ambiente, o => o.Persistencia = (PersistenciaDaMensageria)valor);

        resultado.Failures.Should().ContainSingle()
            .Which.Should().Be($"Mensageria:Persistencia tem um valor inválido ({valor}); use EntityFramework ou Nenhuma.");
    }

    [Fact]
    public void Mensageria_SoUmDosAtalhosEmProducao_DeveRecusarSoEsse()
    {
        var resultado = ValidarMensageria("Production", o => o.Persistencia = PersistenciaDaMensageria.Nenhuma);

        resultado.Failures.Should().ContainSingle().Which.Should().Contain("Mensageria:Persistencia=Nenhuma");
    }

    [Fact]
    public void Mensageria_TemposInvalidos_DevemSerRecusados()
    {
        var resultado = ValidarMensageria("Production", o =>
        {
            o.TimeoutDeInicioSegundos = 0;
            o.OutboxQueryDelayMs = 0;
            o.RetryIntervalosMs = new[] { 500, -1 };
        });

        resultado.Failures.Should().HaveCount(3);
        resultado.Failures.Should().Contain(m => m.Contains("TimeoutDeInicioSegundos"));
        resultado.Failures.Should().Contain(m => m.Contains("OutboxQueryDelayMs"));
        resultado.Failures.Should().Contain(m => m.Contains("RetryIntervalosMs"));
    }

    private static IHost CriarHostComMensageria(string ambiente, params (string Chave, string Valor)[] valores)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = ambiente, DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(valores.ToDictionary(v => v.Chave, v => (string?)v.Valor));
        builder.Services.AddMensageria(builder.Configuration);
        return builder.Build();
    }

    [Theory]
    [InlineData("Production", "Transporte", "EmMemoria")]
    [InlineData("Production", "Persistencia", "Nenhuma")]
    [InlineData("Staging", "Transporte", "EmMemoria")]
    [InlineData("Staging", "Persistencia", "Nenhuma")]
    public async Task Partida_ForaDeDevelopmentOuTestingComAtalhoDeTeste_DeveFalharComMensagemEmPortugues(string ambiente, string chave, string valor)
    {
        using var host = CriarHostComMensageria(ambiente, ($"Mensageria:{chave}", valor));

        var act = () => host.StartAsync();

        var excecao = (await act.Should().ThrowAsync<OptionsValidationException>()).Which;
        excecao.Message.Should().Contain($"Mensageria:{chave}={valor} só é permitido em Development ou Testing");
        excecao.Message.Should().Contain($"ambiente atual: {ambiente}");
    }

    [Fact]
    public async Task Partida_EmTestingComTransporteEmMemoria_DeveSubir()
    {
        using var host = CriarHostComMensageria(
            "Testing", ("Mensageria:Transporte", "EmMemoria"), ("Mensageria:Persistencia", "Nenhuma"));

        await host.StartAsync();
        await host.StopAsync();
    }

    // ---- Billing (reconciliador) ----

    private static IHost CriarHostComReconciliador(params (string Chave, string Valor)[] valores)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development", DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(valores.ToDictionary(v => v.Chave, v => (string?)v.Valor));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddReconciliador(builder.Configuration);
        return builder.Build();
    }

    [Theory]
    [InlineData("Billing:ReconciliadorIntervaloSegundos", "Billing:ReconciliadorIntervaloSegundos deve ser maior que zero")]
    [InlineData("Billing:ReconciliadorTamanhoDoLote", "Billing:ReconciliadorTamanhoDoLote deve estar entre 1 e 1000")]
    [InlineData("Billing:ReconciliadorMaximoDeTentativasDeEstorno", "Billing:ReconciliadorMaximoDeTentativasDeEstorno deve ser maior que zero")]
    public async Task Partida_ReconciliadorHabilitadoComConfiguracaoInvalida_DeveFalharComMensagemEmPortugues(string chave, string mensagem)
    {
        using var host = CriarHostComReconciliador(("Billing:ReconciliadorHabilitado", "true"), (chave, "0"));

        var act = () => host.StartAsync();

        (await act.Should().ThrowAsync<OptionsValidationException>()).Which.Message.Should().Contain(mensagem);
    }

    [Fact]
    public async Task Partida_ReconciliadorDesligadoComConfiguracaoInvalida_DeveSubirSemReconciliar()
    {
        using var host = CriarHostComReconciliador(("Billing:ReconciliadorHabilitado", "false"), ("Billing:ReconciliadorIntervaloSegundos", "0"));

        await host.StartAsync();
        await host.StopAsync();
    }

    private static ValidateOptionsResult ValidarBilling(Action<OpcoesDoBilling> ajustar)
    {
        var opcoes = new OpcoesDoBilling();
        ajustar(opcoes);
        return new ValidadorDasOpcoesDoBilling().Validate(null, opcoes);
    }

    [Fact]
    public void Billing_Padroes_DevemSerValidos()
    {
        ValidarBilling(_ => { }).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Billing_IntervaloNaoPositivo_DeveSerRecusado(int intervalo)
    {
        var resultado = ValidarBilling(o => o.ReconciliadorIntervaloSegundos = intervalo);

        resultado.Failures.Should().ContainSingle().Which.Should().Contain("ReconciliadorIntervaloSegundos");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void Billing_LoteForaDaFaixa_DeveSerRecusado(int lote)
    {
        var resultado = ValidarBilling(o => o.ReconciliadorTamanhoDoLote = lote);

        resultado.Failures.Should().ContainSingle().Which.Should().Contain("entre 1 e 1000");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Billing_LoteNosLimites_DeveSerAceito(int lote)
    {
        ValidarBilling(o => o.ReconciliadorTamanhoDoLote = lote).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Billing_TetoDeTentativasNaoPositivo_DeveSerRecusado(int teto)
    {
        var resultado = ValidarBilling(o => o.ReconciliadorMaximoDeTentativasDeEstorno = teto);

        resultado.Failures.Should().ContainSingle().Which.Should().Contain("ReconciliadorMaximoDeTentativasDeEstorno");
    }

    [Fact]
    public void Billing_TudoInvalido_DeveListarAsTresFalhas()
    {
        var resultado = ValidarBilling(o =>
        {
            o.ReconciliadorIntervaloSegundos = 0;
            o.ReconciliadorTamanhoDoLote = 0;
            o.ReconciliadorMaximoDeTentativasDeEstorno = 0;
        });

        resultado.Failures.Should().HaveCount(3);
    }

    [Fact]
    public void Billing_ReconciliadorDesligado_NaoDeveExigirOsDemaisValores()
    {
        var resultado = ValidarBilling(o =>
        {
            o.ReconciliadorHabilitado = false;
            o.ReconciliadorIntervaloSegundos = 0;
            o.ReconciliadorTamanhoDoLote = 0;
            o.ReconciliadorMaximoDeTentativasDeEstorno = 0;
        });

        resultado.Succeeded.Should().BeTrue();
    }
}
