using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Infrastructure;
using OficinaMecanica.Billing.Infrastructure.Data;
using OficinaMecanica.Billing.Infrastructure.Health;
using OficinaMecanica.Billing.Infrastructure.Messaging;
using OficinaMecanica.Billing.Infrastructure.Options;
using OficinaMecanica.Billing.Integration.Tests.Support;

namespace OficinaMecanica.Billing.Integration.Tests.Health;

// Sem Docker: as dependências "paradas" são endereços onde ninguém escuta (127.0.0.1:1, conexão recusada na hora).
public class ChecksDeProntidaoTests
{
    private static readonly HealthCheckContext Contexto = new()
    {
        Registration = new HealthCheckRegistration("teste", _ => null!, null, null)
    };

    private static RabbitMqHealthCheck RabbitInalcancavel() =>
        new(Microsoft.Extensions.Options.Options.Create(new OpcoesDoRabbitMq { Host = "127.0.0.1", Port = 1, Username = "svc", Password = "x" }));

    [Fact]
    public async Task RabbitMq_ComBrokerInalcancavel_DeveResponderDegradedSemExporOHost()
    {
        var resultado = await RabbitInalcancavel().CheckHealthAsync(Contexto);

        resultado.Status.Should().Be(HealthStatus.Degraded, "o outbox segura as mensagens, então o broker fora não tira o pod de rotação");
        resultado.Description.Should().Be("Não foi possível conectar ao RabbitMQ; as mensagens ficam no outbox até o broker voltar.");
        resultado.Description.Should().NotContain("127.0.0.1");
        resultado.Exception.Should().NotBeNull();
    }

    [Fact]
    public async Task RabbitMq_ComCancelamentoDoChamador_DevePropagarOCancelamento()
    {
        using var cancelamento = new CancellationTokenSource();
        await cancelamento.CancelAsync();

        var act = () => RabbitInalcancavel().CheckHealthAsync(Contexto, cancelamento.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static ServiceProvider CriarProvedor(string conexao)
    {
        var configuracao = ConfiguracaoDeTeste.Criar(
            ("ConnectionStrings:DefaultConnection", conexao),
            ("Pagamento:Provider", "Fake"),
            ("Fake:WebhookSecret", "segredo-fake"));
        var services = new ServiceCollection();
        services.AddSingleton(ConfiguracaoDeTeste.Ambiente("Development"));
        services.AddLogging();
        services.AddInfrastructure(configuracao);
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task SqlServer_ComBancoInalcancavel_DeveResponderUnhealthyDentroDoLimite()
    {
        await using var provedor = CriarProvedor("Server=tcp:127.0.0.1,1;Database=BillingDb;Encrypt=False;Connect Timeout=30;");
        await using var escopo = provedor.CreateAsyncScope();
        var check = new SqlServerHealthCheck(escopo.ServiceProvider.GetRequiredService<BillingDbContext>());

        var inicio = DateTime.UtcNow;
        var resultado = await check.CheckHealthAsync(Contexto);

        resultado.Status.Should().Be(HealthStatus.Unhealthy, "sem o banco a API não aceita escrita");
        resultado.Description.Should().Be("Não foi possível conectar ao banco de dados.");
        (DateTime.UtcNow - inicio).Should().BeLessThan(TimeSpan.FromSeconds(10), "a probe não pode esperar o retry do EF nem o timeout de 30 s da conexão");
    }

    [Fact]
    public async Task SqlServer_ComBancoDisponivel_DeveResponderHealthyComMensagemEmPortugues()
    {
        var opcoes = new DbContextOptionsBuilder<BillingDbContext>().UseInMemoryDatabase($"saude-{Guid.NewGuid():N}").Options;
        await using var db = new BillingDbContext(opcoes);

        var resultado = await new SqlServerHealthCheck(db).CheckHealthAsync(Contexto);

        resultado.Status.Should().Be(HealthStatus.Healthy);
        resultado.Description.Should().Be("Conexão com o banco de dados estabelecida.");
    }

    [Fact]
    public async Task SqlServer_ComCancelamentoDoChamador_DevePropagarOCancelamento()
    {
        await using var provedor = CriarProvedor("Server=tcp:127.0.0.1,1;Database=BillingDb;Encrypt=False;");
        await using var escopo = provedor.CreateAsyncScope();
        var check = new SqlServerHealthCheck(escopo.ServiceProvider.GetRequiredService<BillingDbContext>());
        using var cancelamento = new CancellationTokenSource();
        await cancelamento.CancelAsync();

        var act = () => check.CheckHealthAsync(Contexto, cancelamento.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- Limites de tempo e falha inesperada ----

    // Um servidor que aceita a conexão TCP (o kernel completa o handshake sem ninguém chamar Accept) e nunca fala o protocolo:
    // é o caso em que só o limite de 3 s encerra a espera, em qualquer sistema operacional (um endereço que descarta pacotes
    // dependeria da rede de quem roda o teste).
    private sealed class ServidorMudo : IDisposable
    {
        private readonly TcpListener _servidor = new(IPAddress.Loopback, 0);

        public ServidorMudo() => _servidor.Start();

        public int Porta => ((IPEndPoint)_servidor.LocalEndpoint).Port;

        public void Dispose() => _servidor.Stop();
    }

    // Mede só a avaliação do check: montar o provedor e o contexto fica fora do cronômetro, porque a janela do teste é
    // estreita (2,5 s a 3,8 s) e a montagem não faz parte do limite que a readiness enxerga.
    private static async Task<(HealthCheckResult Resultado, TimeSpan Duracao)> MedirAsync(Func<Task<HealthCheckResult>> avaliar)
    {
        var cronometro = Stopwatch.StartNew();
        var resultado = await avaliar();
        cronometro.Stop();
        return (resultado, cronometro.Elapsed);
    }

    // Acima de 2,5 s mostra que o check esperou o limite de 3 s (e não um menor) e abaixo de 3,8 s que um limite maior (4 s,
    // 4,5 s, os 30 s da conexão) estouraria, com folga para a máquina carregada.
    private static void DeveTerDesistidoNoLimiteDeTresSegundos(TimeSpan duracao, string porque) =>
        duracao.Should().BeGreaterThan(TimeSpan.FromSeconds(2.5)).And.BeLessThan(TimeSpan.FromSeconds(3.8), porque);

    [Fact]
    public async Task RabbitMq_ComBrokerQueAceitaAConexaoMasNaoResponde_DeveDesistirNoLimiteDeTresSegundos()
    {
        using var servidor = new ServidorMudo();
        var check = new RabbitMqHealthCheck(Microsoft.Extensions.Options.Options.Create(
            new OpcoesDoRabbitMq { Host = "127.0.0.1", Port = servidor.Porta, Username = "svc", Password = "x" }));

        var (resultado, duracao) = await MedirAsync(() => check.CheckHealthAsync(Contexto));

        resultado.Status.Should().Be(HealthStatus.Degraded);
        DeveTerDesistidoNoLimiteDeTresSegundos(duracao,
            "o limite é de 3 s e a readiness tem timeoutSeconds: 5; o cliente sozinho leva ~8 s para desistir de um broker mudo");
    }

    [Fact]
    public async Task SqlServer_ComBancoQueAceitaAConexaoMasNaoResponde_DeveDesistirNoLimiteDeTresSegundos()
    {
        using var servidor = new ServidorMudo();
        await using var provedor = CriarProvedor($"Server=tcp:127.0.0.1,{servidor.Porta};Database=BillingDb;Encrypt=False;Connect Timeout=30;");
        await using var escopo = provedor.CreateAsyncScope();
        var check = new SqlServerHealthCheck(escopo.ServiceProvider.GetRequiredService<BillingDbContext>());

        var (resultado, duracao) = await MedirAsync(() => check.CheckHealthAsync(Contexto));

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        DeveTerDesistidoNoLimiteDeTresSegundos(duracao,
            "o limite é de 3 s e a readiness tem timeoutSeconds: 5; o timeout da conexão é de 30 s");
    }

    [Fact]
    public async Task RabbitMq_ComBrokerCongeladoECancelamentoDoChamador_DevePropagarOCancelamentoEmVezDeVirarDegraded()
    {
        using var servidor = new ServidorMudo();
        var check = new RabbitMqHealthCheck(Microsoft.Extensions.Options.Options.Create(
            new OpcoesDoRabbitMq { Host = "127.0.0.1", Port = servidor.Porta, Username = "svc", Password = "x" }));
        using var chamador = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var act = () => check.CheckHealthAsync(Contexto, chamador.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("quem cancelou foi a probe, não o broker");
    }

    // O EF engole a falha de conexão e devolve falso; este criador joga a exceção inesperada direto, o único jeito de exercitar o catch.
    private sealed class CriadorQueFalhaAoConectar : IDatabaseCreator
    {
        public bool EnsureDeleted() => throw new NotSupportedException();
        public Task<bool> EnsureDeletedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public bool EnsureCreated() => throw new NotSupportedException();
        public Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public bool CanConnect() => throw new InvalidOperationException("falha inesperada do driver");
        public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("falha inesperada do driver");
    }

    [Fact]
    public async Task SqlServer_ComExcecaoInesperadaNoDriver_DeveResponderUnhealthyComAExcecaoEnaoDegraded()
    {
        var opcoes = new DbContextOptionsBuilder<BillingDbContext>()
            .UseInMemoryDatabase($"saude-{Guid.NewGuid():N}")
            .ReplaceService<IDatabaseCreator, CriadorQueFalhaAoConectar>()
            .Options;
        await using var db = new BillingDbContext(opcoes);

        var resultado = await new SqlServerHealthCheck(db).CheckHealthAsync(Contexto);

        resultado.Status.Should().Be(HealthStatus.Unhealthy, "sem o banco a API não aceita escrita");
        resultado.Description.Should().Be("Não foi possível conectar ao banco de dados.");
        resultado.Exception.Should().BeOfType<InvalidOperationException>();
    }

    // ---- Registros de prontidao ----

    private static ServiceProvider ProvedorDeRegistros(string transporte)
    {
        var configuracao = ConfiguracaoDeTeste.Criar(
            ("ConnectionStrings:DefaultConnection", ConfiguracaoDeTeste.ConexaoInerte),
            ("Pagamento:Provider", "Fake"),
            ("Fake:WebhookSecret", "segredo-fake"),
            ("Mensageria:Transporte", transporte),
            ("Mensageria:Persistencia", "Nenhuma"));
        var services = new ServiceCollection();
        services.AddSingleton(ConfiguracaoDeTeste.Ambiente("Development"));
        services.AddLogging();
        services.AddInfrastructure(configuracao);
        services.AddMensageria(configuracao);
        return services.BuildServiceProvider();
    }

    private static async Task<IReadOnlyDictionary<string, HealthCheckRegistration>> RegistrosAsync(string transporte)
    {
        await using var provedor = ProvedorDeRegistros(transporte);

        return provedor.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.ToDictionary(r => r.Name);
    }

    [Fact]
    public async Task Registros_BancoEBroker_DevemRodarDentroDoCacheDeProntidao_MasOBusDoMassTransitNao()
    {
        await using var provedor = ProvedorDeRegistros("RabbitMq");
        using var escopo = provedor.CreateScope();
        var registros = provedor.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.ToDictionary(r => r.Name);

        registros["sqlserver"].Factory(escopo.ServiceProvider).Should().BeOfType<HealthCheckComCache>(
            "/health/ready é anônimo e cada avaliação abre uma conexão SQL");
        registros["rabbitmq"].Factory(escopo.ServiceProvider).Should().BeOfType<HealthCheckComCache>(
            "e cada avaliação abre uma conexão AMQP");
        registros["masstransit-bus"].Factory(escopo.ServiceProvider).Should().NotBeOfType<HealthCheckComCache>(
            "o check do bus não abre conexão nova");
    }

    [Fact]
    public async Task Registros_ComRabbitMq_DevemTerBusBancoEConexaoAmqpTodosNaProntidao()
    {
        var registros = await RegistrosAsync("RabbitMq");

        registros.Keys.Should().BeEquivalentTo("masstransit-bus", "sqlserver", "rabbitmq");
        registros.Values.Should().OnlyContain(r => r.Tags.Contains("ready"), "o /health do processo não pode depender de nenhuma delas");
    }

    [Fact]
    public async Task Registros_ComTransporteEmMemoria_NaoDevemTerOCheckDoBroker()
    {
        var registros = await RegistrosAsync("EmMemoria");

        registros.Keys.Should().BeEquivalentTo("masstransit-bus", "sqlserver");
    }

    [Fact]
    public async Task Registros_BrokerForaNaoDeveTirarOPodDeRotacao_SoOBancoPodeFalharAProntidao()
    {
        var registros = await RegistrosAsync("RabbitMq");

        registros["masstransit-bus"].FailureStatus.Should().Be(HealthStatus.Degraded);
        registros["rabbitmq"].FailureStatus.Should().Be(HealthStatus.Degraded);
        registros["sqlserver"].FailureStatus.Should().Be(HealthStatus.Unhealthy);
    }
}
