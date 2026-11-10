using FluentAssertions;
using MassTransit;
using MassTransit.DependencyInjection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Domain.Interfaces;
using OficinaMecanica.Billing.Infrastructure;
using OficinaMecanica.Billing.Infrastructure.Data;
using OficinaMecanica.Billing.Infrastructure.Gateways;
using OficinaMecanica.Billing.Infrastructure.Gateways.MercadoPago;
using OficinaMecanica.Billing.Infrastructure.Messaging;
using OficinaMecanica.Billing.Infrastructure.Observability;
using OficinaMecanica.Billing.Infrastructure.Options;
using OficinaMecanica.Billing.Infrastructure.Repositories;
using OficinaMecanica.Billing.Infrastructure.Security;
using OficinaMecanica.Billing.Integration.Tests.Support;

namespace OficinaMecanica.Billing.Integration.Tests.Composition;

public class DependencyInjectionTests
{
    private static ServiceProvider CriarProvedor(IConfiguration configuracao, string ambiente = "Development", Action<IServiceCollection>? ajustar = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(ConfiguracaoDeTeste.Ambiente(ambiente));
        services.AddSingleton(configuracao);
        services.AddLogging();
        services.AddInfrastructure(configuracao);
        ajustar?.Invoke(services);
        return services.BuildServiceProvider();
    }

    // ---- Selecao do gateway ----

    [Fact]
    public void Gateway_ProviderFake_DeveResolverOGatewayEmMemoria()
    {
        using var provedor = CriarProvedor(ConfiguracaoDeTeste.ComFake());
        using var escopo = provedor.CreateScope();

        escopo.ServiceProvider.GetRequiredService<IPagamentoGateway>().Should().BeOfType<FakePagamentoGateway>();
    }

    [Fact]
    public void Gateway_ProviderFake_DeveCompartilharAMesmaInstanciaEntreEscopos()
    {
        using var provedor = CriarProvedor(ConfiguracaoDeTeste.ComFake());
        using var escopoA = provedor.CreateScope();
        using var escopoB = provedor.CreateScope();

        escopoA.ServiceProvider.GetRequiredService<IPagamentoGateway>()
            .Should().BeSameAs(escopoB.ServiceProvider.GetRequiredService<IPagamentoGateway>());
    }

    [Fact]
    public void Gateway_ProviderMercadoPago_DeveResolverOAdaptadorReal()
    {
        using var provedor = CriarProvedor(ConfiguracaoDeTeste.ComMercadoPago(), ambiente: "Production");
        using var escopo = provedor.CreateScope();

        escopo.ServiceProvider.GetRequiredService<IPagamentoGateway>().Should().BeOfType<MercadoPagoGateway>();
    }

    [Fact]
    public void Gateway_ProviderAusente_DeveAssumirMercadoPagoEExigirCredenciais()
    {
        var configuracao = ConfiguracaoDeTeste.Criar(("ConnectionStrings:DefaultConnection", ConfiguracaoDeTeste.ConexaoInerte));
        using var provedor = CriarProvedor(configuracao, ambiente: "Production");

        var act = () => provedor.GetRequiredService<IOptions<OpcoesDePagamento>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*MercadoPago:AccessToken*");
    }

    [Fact]
    public void HttpClientDoGateway_DeveUsarOTimeoutConfigurado()
    {
        var configuracao = ConfiguracaoDeTeste.Criar(
            ("ConnectionStrings:DefaultConnection", ConfiguracaoDeTeste.ConexaoInerte),
            ("Pagamento:Provider", "MercadoPago"),
            ("MercadoPago:AccessToken", "TEST-token"),
            ("MercadoPago:WebhookSecret", "segredo"),
            ("MercadoPago:NotificationBaseUrl", "https://exemplo.test"),
            ("MercadoPago:TimeoutSegundos", "3"));
        using var provedor = CriarProvedor(configuracao, ambiente: "Production");

        var cliente = provedor.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(MercadoPagoGateway));

        cliente.Timeout.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void OpcoesDoMercadoPago_Padroes_DeveTerTimeoutCurtoDe10SegundosEToleranciaDe5Minutos()
    {
        var opcoes = new OpcoesDoMercadoPago();

        opcoes.TimeoutSegundos.Should().Be(10);
        opcoes.ToleranciaDaAssinaturaSegundos.Should().Be(300);
    }

    // ---- Validacao das opcoes de pagamento ----

    [Fact]
    public void Opcoes_FakeForaDeDevelopment_DeveFalharNaPartida()
    {
        using var provedor = CriarProvedor(ConfiguracaoDeTeste.ComFake(), ambiente: "Production");

        var act = () => provedor.GetRequiredService<IOptions<OpcoesDePagamento>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*só é permitido em Development*");
    }

    [Fact]
    public void Opcoes_FakeSemSegredo_DeveFalharNaPartida()
    {
        var configuracao = ConfiguracaoDeTeste.Criar(("Pagamento:Provider", "Fake"));
        using var provedor = CriarProvedor(configuracao);

        var act = () => provedor.GetRequiredService<IOptions<OpcoesDePagamento>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*Fake:WebhookSecret*");
    }

    [Theory]
    [InlineData("MercadoPago:AccessToken")]
    [InlineData("MercadoPago:WebhookSecret")]
    [InlineData("MercadoPago:NotificationBaseUrl")]
    public void Opcoes_MercadoPagoSemUmCampoObrigatorio_DeveFalharNomeandoOCampo(string campoAusente)
    {
        var valores = new Dictionary<string, string?>
        {
            ["Pagamento:Provider"] = "MercadoPago",
            ["MercadoPago:AccessToken"] = "TEST-token",
            ["MercadoPago:WebhookSecret"] = "segredo",
            ["MercadoPago:NotificationBaseUrl"] = "https://exemplo.test",
        };
        valores.Remove(campoAusente);
        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
        using var provedor = CriarProvedor(configuracao, ambiente: "Production");

        var act = () => provedor.GetRequiredService<IOptions<OpcoesDePagamento>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage($"*{campoAusente}*");
    }

    [Theory]
    [InlineData("MercadoPago:TimeoutSegundos")]
    [InlineData("MercadoPago:ToleranciaDaAssinaturaSegundos")]
    public void Opcoes_TempoNaoPositivo_DeveFalhar(string campo)
    {
        var valores = new Dictionary<string, string?>
        {
            ["Pagamento:Provider"] = "Fake",
            ["Fake:WebhookSecret"] = "segredo",
            [campo] = "0",
        };
        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
        using var provedor = CriarProvedor(configuracao);

        var act = () => provedor.GetRequiredService<IOptions<OpcoesDePagamento>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage($"*{campo}*");
    }

    // ---- Validador de assinatura ----

    [Fact]
    public void ValidadorDeAssinatura_ProviderFake_DeveUsarOSegredoDoFake()
    {
        var relogio = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var provedor = CriarProvedor(ConfiguracaoDeTeste.ComFake(), ajustar: s => s.AddSingleton<TimeProvider>(relogio));
        var validador = provedor.GetRequiredService<IAssinaturaWebhookValidator>();

        var assinaturaDoFake = AssinaturaWebhookMercadoPago.GerarCabecalho(ConfiguracaoDeTeste.SegredoDoFake, "123", "req-1", relogio.GetUtcNow());
        var assinaturaDoMercadoPago = AssinaturaWebhookMercadoPago.GerarCabecalho(ConfiguracaoDeTeste.SegredoDoMercadoPago, "123", "req-1", relogio.GetUtcNow());

        validador.Validar(assinaturaDoFake, "req-1", "123").Should().BeTrue();
        validador.Validar(assinaturaDoMercadoPago, "req-1", "123").Should().BeFalse();
    }

    [Fact]
    public void ValidadorDeAssinatura_ProviderMercadoPago_DeveUsarOSegredoDoMercadoPago()
    {
        var relogio = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var provedor = CriarProvedor(ConfiguracaoDeTeste.ComMercadoPago(), "Production", s => s.AddSingleton<TimeProvider>(relogio));
        var validador = provedor.GetRequiredService<IAssinaturaWebhookValidator>();

        var assinatura = AssinaturaWebhookMercadoPago.GerarCabecalho(ConfiguracaoDeTeste.SegredoDoMercadoPago, "123", "req-1", relogio.GetUtcNow());

        validador.Validar(assinatura, "req-1", "123").Should().BeTrue();
    }

    [Fact]
    public void ValidadorDeAssinatura_ToleranciaConfigurada_DeveSerRespeitada()
    {
        var relogio = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var configuracao = ConfiguracaoDeTeste.Criar(
            ("Pagamento:Provider", "Fake"),
            ("Fake:WebhookSecret", ConfiguracaoDeTeste.SegredoDoFake),
            ("MercadoPago:ToleranciaDaAssinaturaSegundos", "60"));
        using var provedor = CriarProvedor(configuracao, ajustar: s => s.AddSingleton<TimeProvider>(relogio));
        var validador = provedor.GetRequiredService<IAssinaturaWebhookValidator>();
        var assinatura = AssinaturaWebhookMercadoPago.GerarCabecalho(ConfiguracaoDeTeste.SegredoDoFake, "123", "req-1", relogio.GetUtcNow());

        relogio.Advance(TimeSpan.FromSeconds(61));

        validador.Validar(assinatura, "req-1", "123").Should().BeFalse();
    }

    // ---- Registro geral ----

    [Fact]
    public void AddInfrastructure_DeveRegistrarRepositoriosUnitOfWorkMetricasEPublisher()
    {
        using var provedor = CriarProvedor(ConfiguracaoDeTeste.ComFake());
        using var escopo = provedor.CreateScope();
        var sp = escopo.ServiceProvider;

        sp.GetRequiredService<IOrcamentoRepository>().Should().BeOfType<OrcamentoRepository>();
        sp.GetRequiredService<IPagamentoRepository>().Should().BeOfType<PagamentoRepository>();
        sp.GetRequiredService<IWebhookRecebidoRepository>().Should().BeOfType<WebhookRecebidoRepository>();
        sp.GetRequiredService<ICancelamentoPrevioRepository>().Should().BeOfType<CancelamentoPrevioRepository>();
        sp.GetRequiredService<IUnitOfWork>().Should().BeSameAs(sp.GetRequiredService<BillingDbContext>());
        sp.GetRequiredService<IMetricasDoBilling>().Should().BeOfType<MetricasDoBilling>();
        provedor.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
        provedor.GetServices<IHostedService>().Select(h => h.GetType().Name)
            .Should().Contain("ReconciliadorDePagamentosBackgroundService");
    }

    // ---- EnableRetryOnFailure so fora de consumer ----

    [Fact]
    public void DbContext_ForaDeConsumer_DeveRepetirPorFalhaTransitoria()
    {
        var configuracao = ConfiguracaoDeTeste.ComFake();
        using var provedor = CriarProvedor(configuracao, ajustar: s => s.AddMensageria(MensageriaEmMemoria(configuracao)));
        using var escopo = provedor.CreateScope();

        var contexto = escopo.ServiceProvider.GetRequiredService<BillingDbContext>();

        contexto.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeTrue();
    }

    [Fact]
    public void DbContext_DentroDeUmConsumer_NaoDeveRepetirOQueEhDoRetryDoMassTransit()
    {
        var configuracao = ConfiguracaoDeTeste.ComFake();
        using var provedor = CriarProvedor(configuracao, ajustar: s => s.AddMensageria(MensageriaEmMemoria(configuracao)));
        using var escopo = provedor.CreateScope();
        using var contextoDoConsumer = escopo.ServiceProvider
            .GetRequiredService<IScopedConsumeContextProvider>()
            .PushContext(new Mock<ConsumeContext>().Object);

        var db = escopo.ServiceProvider.GetRequiredService<BillingDbContext>();

        db.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeFalse();
    }

    [Fact]
    public void DbContext_SemMassTransitRegistrado_DeveRepetirPorFalhaTransitoria()
    {
        using var provedor = CriarProvedor(ConfiguracaoDeTeste.ComFake());
        using var escopo = provedor.CreateScope();

        escopo.ServiceProvider.GetRequiredService<BillingDbContext>()
            .Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeTrue();
    }

    private static IConfiguration MensageriaEmMemoria(IConfiguration baseConfig) =>
        new ConfigurationBuilder()
            .AddConfiguration(baseConfig)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mensageria:Transporte"] = "EmMemoria",
                ["Mensageria:Persistencia"] = "Nenhuma",
            })
            .Build();

    // ---- Bloqueio por ordem de servico ----

    [Fact]
    public void Bloqueio_PelaComposicaoPadrao_DeveUsarOTempoLimiteDe20SegundosMenorQueOTimeoutDe30DoComandoSql()
    {
        var configuracao = ConfiguracaoDeTeste.ComFake();
        using var provedor = CriarProvedor(configuracao, ajustar: s => s.AddMensageria(configuracao));
        using var escopo = provedor.CreateScope();

        var bloqueio = escopo.ServiceProvider.GetRequiredService<IBloqueioPorOrdemDeServico>();

        var sql = bloqueio.Should().BeOfType<BloqueioPorOrdemDeServicoSqlServer>().Subject;
        sql.TempoLimiteEfetivoEmMilissegundos.Should().Be(20_000);
        sql.TempoLimiteEfetivoEmMilissegundos.Should().BeLessThan(30_000);
    }

    [Fact]
    public void Bloqueio_ConstrutorPublico_DeveAplicarOPadraoEOInternoOValorInformado()
    {
        using var escopo = CriarProvedor(ConfiguracaoDeTeste.ComFake()).CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<BillingDbContext>();

        new BloqueioPorOrdemDeServicoSqlServer(db).TempoLimiteEfetivoEmMilissegundos
            .Should().Be(BloqueioPorOrdemDeServicoSqlServer.TempoLimitePadraoEmMilissegundos).And.Be(20_000);
        new BloqueioPorOrdemDeServicoSqlServer(db, 300).TempoLimiteEfetivoEmMilissegundos.Should().Be(300);
    }

    [Fact]
    public void Bloqueio_SemOutboxDoEf_DeveUsarAImplementacaoSemBloqueio()
    {
        var configuracao = MensageriaEmMemoria(ConfiguracaoDeTeste.ComFake());
        using var provedor = CriarProvedor(configuracao, ajustar: s => s.AddMensageria(configuracao));
        using var escopo = provedor.CreateScope();

        escopo.ServiceProvider.GetRequiredService<IBloqueioPorOrdemDeServico>().Should().BeOfType<SemBloqueioPorOrdemDeServico>();
    }

    // ---- JWT ----

    [Fact]
    public void Jwt_SemJwksUri_DeveFalharAoMontarAsOpcoesDoBearer()
    {
        var configuracao = ConfiguracaoDeTeste.Criar(("Pagamento:Provider", "Fake"), ("Fake:WebhookSecret", "s"));
        using var provedor = CriarProvedor(configuracao);

        var act = () => provedor.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

        act.Should().Throw<InvalidOperationException>().WithMessage("*JwtSettings:JwksUri*");
    }

    [Fact]
    public void Jwt_Configurado_DeveValidarEmissorAudienciaTempoEChaveComToleranciaZero()
    {
        var configuracao = ConfiguracaoDeTeste.Criar(
            ("Pagamento:Provider", "Fake"),
            ("Fake:WebhookSecret", "s"),
            ("JwtSettings:Issuer", "Emissor.Teste"),
            ("JwtSettings:Audience", "Audiencia.Teste"),
            ("JwtSettings:JwksUri", "http://localhost:1/jwks.json"));
        using var provedor = CriarProvedor(configuracao);

        var parametros = provedor.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;

        parametros.ValidateIssuer.Should().BeTrue();
        parametros.ValidateAudience.Should().BeTrue();
        parametros.ValidateLifetime.Should().BeTrue();
        parametros.ValidateIssuerSigningKey.Should().BeTrue();
        parametros.ValidIssuer.Should().Be("Emissor.Teste");
        parametros.ValidAudience.Should().Be("Audiencia.Teste");
        parametros.ClockSkew.Should().Be(TimeSpan.Zero);
        parametros.IssuerSigningKeyResolver.Should().NotBeNull();
    }
}
