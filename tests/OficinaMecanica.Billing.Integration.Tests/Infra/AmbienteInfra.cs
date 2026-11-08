using FluentAssertions;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Application;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Infrastructure;
using OficinaMecanica.Billing.Infrastructure.Data;
using OficinaMecanica.Billing.Infrastructure.Gateways;
using OficinaMecanica.Billing.Infrastructure.Messaging;
using OficinaMecanica.Billing.Integration.Tests.Support;
using OficinaMecanica.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace OficinaMecanica.Billing.Integration.Tests.Infra;

// Um Billing completo (consumers, EF no SQL Server real, outbox/inbox, RabbitMQ real) mais um "OS Service" de
// teste: um segundo bus no mesmo virtual host que manda os comandos da saga e recolhe os eventos que o Billing
// publica. Banco e virtual host são exclusivos do teste.
public sealed class AmbienteInfra : IAsyncDisposable
{
    public const string SegredoDoFake = ConfiguracaoDeTeste.SegredoDoFake;

    private readonly InfraFixture _infra;
    private IBusControl _peer = null!;
    private IHost? _hostGenerico;
    private ApiInfraFactory? _api;

    public string Banco { get; } = $"Billing_{Guid.NewGuid():N}"[..24];
    public string VirtualHost { get; } = $"v{Guid.NewGuid():N}"[..12];
    public string Conexao => _infra.ConexaoSql(Banco);
    public ColetorDeMensagens Coletor { get; } = new();
    public ComportamentoDoGateway Comportamento { get; } = new();
    public List<string> Logs { get; } = new();

    public IServiceProvider Servicos => _hostGenerico?.Services ?? _api!.Services;
    public FakePagamentoGateway Gateway => Servicos.GetRequiredService<FakePagamentoGateway>();

    private AmbienteInfra(InfraFixture infra)
    {
        _infra = infra;
    }

    public static async Task<AmbienteInfra> CriarComHostAsync(InfraFixture infra, Action<IServiceCollection>? ajustar = null)
    {
        var ambiente = new AmbienteInfra(infra);
        await ambiente.PrepararAsync();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development", DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(ambiente.Configuracao());
        builder.Services.AddLogging(l => l.SetMinimumLevel(LogLevel.Warning).AddProvider(new ProvedorDeLogsEmLista(ambiente.Logs)));
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddMensageria(builder.Configuration);
        builder.Services.AddSingleton(ambiente.Comportamento);
        builder.Services.AddScoped<IPagamentoGateway>(sp =>
            new GatewayDeTeste(sp.GetRequiredService<FakePagamentoGateway>(), ambiente.Comportamento, sp));
        ajustar?.Invoke(builder.Services);

        ambiente._hostGenerico = builder.Build();
        using (var escopo = ambiente._hostGenerico.Services.CreateScope())
            await escopo.ServiceProvider.GetRequiredService<BillingDbContext>().Database.MigrateAsync();

        await ambiente._peer.StartAsync();
        await ambiente._hostGenerico.StartAsync();
        return ambiente;
    }

    public static async Task<AmbienteInfra> CriarComApiAsync(InfraFixture infra)
    {
        var ambiente = new AmbienteInfra(infra);
        await ambiente.PrepararAsync();

        // A API migra o banco sozinha na partida (Development), exercitando a migration inicial.
        ambiente._api = new ApiInfraFactory(ambiente.Configuracao());
        await ambiente._peer.StartAsync();
        _ = ambiente._api.Server;
        return ambiente;
    }

    public HttpClient CriarClienteHttp() => _api!.CreateClient();

    private async Task PrepararAsync()
    {
        await _infra.CriarVirtualHostAsync(VirtualHost);

        _peer = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host(_infra.HostRabbit, (ushort)_infra.PortaRabbit, VirtualHost, h =>
            {
                h.Username(_infra.UsuarioRabbit);
                h.Password(_infra.SenhaRabbit);
            });
            cfg.ReceiveEndpoint("os-service-de-teste", e => Coletor.EscutarTodosOsEventosDoBilling(e));
        });
    }

    private Dictionary<string, string?> Configuracao() => new()
    {
        ["ConnectionStrings:DefaultConnection"] = Conexao,
        ["RabbitMq:Host"] = _infra.HostRabbit,
        ["RabbitMq:Port"] = _infra.PortaRabbit.ToString(),
        ["RabbitMq:VirtualHost"] = VirtualHost,
        ["RabbitMq:Username"] = _infra.UsuarioRabbit,
        ["RabbitMq:Password"] = _infra.SenhaRabbit,
        ["Mensageria:RetryIntervalosMs:0"] = "50",
        ["Mensageria:RetryIntervalosMs:1"] = "100",
        ["Mensageria:RetryIntervalosMs:2"] = "200",
        ["Mensageria:RetryIntervalosMs:3"] = "400",
        ["Mensageria:OutboxQueryDelayMs"] = "100",
        ["Pagamento:Provider"] = "Fake",
        ["Fake:WebhookSecret"] = SegredoDoFake,
        ["JwtSettings:Issuer"] = EmissorDeTokens.Emissor,
        ["JwtSettings:Audience"] = EmissorDeTokens.Audiencia,
        ["JwtSettings:JwksUri"] = "http://127.0.0.1:1/.well-known/jwks.json",
        ["Billing:ReconciliadorHabilitado"] = "false",
        // Sem cache de prontidão: estes testes derrubam e restauram as dependências e esperam ver cada mudança na hora.
        ["Health:ProntidaoCacheSegundos"] = "0",
    };

    // ---- Comandos da saga ----

    public async Task EnviarAoBillingAsync<T>(T comando, Guid messageId) where T : class
    {
        var endpoint = await _peer.GetSendEndpoint(new Uri($"queue:{Filas.Billing}"));
        await endpoint.Send(comando, contexto => contexto.MessageId = messageId);
    }

    // ---- Observacao ----

    public async Task AguardarAsync(Func<bool> condicao, string descricao, int segundos = 30)
    {
        var limite = DateTime.UtcNow.AddSeconds(segundos);
        while (!condicao() && DateTime.UtcNow < limite)
            await Task.Delay(50);

        condicao().Should().BeTrue($"{descricao} (em até {segundos}s). Logs do Billing: {string.Join(" | ", Logs.TakeLast(5))}");
    }

    public async Task AguardarAsync(Func<Task<bool>> condicao, string descricao, int segundos = 30)
    {
        var limite = DateTime.UtcNow.AddSeconds(segundos);
        var atendida = await condicao();
        while (!atendida && DateTime.UtcNow < limite)
        {
            await Task.Delay(100);
            atendida = await condicao();
        }

        atendida.Should().BeTrue($"{descricao} (em até {segundos}s). Logs do Billing: {string.Join(" | ", Logs.TakeLast(5))}");
    }

    public async Task<int> ContarMensagensNaFilaAsync(string fila)
    {
        var fabrica = new ConnectionFactory
        {
            HostName = _infra.HostRabbit,
            Port = _infra.PortaRabbit,
            VirtualHost = VirtualHost,
            UserName = _infra.UsuarioRabbit,
            Password = _infra.SenhaRabbit
        };
        await using var conexao = await fabrica.CreateConnectionAsync();
        await using var canal = await conexao.CreateChannelAsync();
        try
        {
            return (int)(await canal.QueueDeclarePassiveAsync(fila)).MessageCount;
        }
        catch (OperationInterruptedException)
        {
            return 0;
        }
    }

    public Task<int> MensagensComErroAsync() => ContarMensagensNaFilaAsync($"{Filas.Billing}_error");

    public async Task<T> ComBancoAsync<T>(Func<BillingDbContext, Task<T>> acao)
    {
        var opcoes = new DbContextOptionsBuilder<BillingDbContext>().UseSqlServer(Conexao).Options;
        await using var db = new BillingDbContext(opcoes);
        return await acao(db);
    }

    public Task ComBancoAsync(Func<BillingDbContext, Task> acao) =>
        ComBancoAsync<int>(async db =>
        {
            await acao(db);
            return 0;
        });

    public async ValueTask DisposeAsync()
    {
        try { await _peer.StopAsync(); } catch { /* o bus pode nem ter chegado a iniciar */ }
        if (_hostGenerico is not null)
        {
            try { await _hostGenerico.StopAsync(); } catch { /* idem */ }
            _hostGenerico.Dispose();
        }
        if (_api is not null)
            await _api.DisposeAsync();

        SqlConnectionLimpeza();
        await _infra.ApagarBancoAsync(Banco);
    }

    private static void SqlConnectionLimpeza() => Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();

    private sealed class ApiInfraFactory : WebApplicationFactory<Program>
    {
        private readonly Dictionary<string, string?> _configuracao;

        public ApiInfraFactory(Dictionary<string, string?> configuracao)
        {
            _configuracao = configuracao;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            foreach (var (chave, valor) in _configuracao)
                builder.UseSetting(chave, valor);
        }
    }

    private sealed class ProvedorDeLogsEmLista : ILoggerProvider
    {
        private readonly List<string> _logs;

        public ProvedorDeLogsEmLista(List<string> logs) => _logs = logs;

        public ILogger CreateLogger(string categoryName) => new Registrador(_logs, categoryName);

        public void Dispose()
        {
        }

        private sealed class Registrador : ILogger
        {
            private readonly List<string> _logs;
            private readonly string _categoria;

            public Registrador(List<string> logs, string categoria)
            {
                _logs = logs;
                _categoria = categoria;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                lock (_logs)
                    _logs.Add($"[{logLevel}] {_categoria}: {formatter(state, exception)} {exception?.GetType().Name}");
            }
        }
    }
}
