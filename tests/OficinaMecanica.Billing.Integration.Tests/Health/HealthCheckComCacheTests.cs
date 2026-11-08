using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using OficinaMecanica.Billing.Infrastructure;
using OficinaMecanica.Billing.Infrastructure.Health;
using OficinaMecanica.Billing.Integration.Tests.Support;

namespace OficinaMecanica.Billing.Integration.Tests.Health;

// /health/ready é anônimo e passa pelo wildcard da APIM: o cache limita o custo (uma conexão SQL e uma AMQP por janela) a
// qualquer taxa de requisições. Sem Docker: o check interno é um contador e o relógio é falso.
public class HealthCheckComCacheTests
{
    private static readonly HealthCheckContext Contexto = new()
    {
        Registration = new HealthCheckRegistration("teste", _ => null!, null, null)
    };

    private sealed class CheckContador : IHealthCheck
    {
        private int _chamadas;

        public int Chamadas => Volatile.Read(ref _chamadas);

        public Func<int, Task<HealthCheckResult>> Resultado { get; set; } =
            n => Task.FromResult(HealthCheckResult.Healthy($"avaliação {n}"));

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await Resultado(Interlocked.Increment(ref _chamadas));
    }

    private static HealthCheckComCache Criar(CheckContador interno, FakeTimeProvider relogio, int cacheSegundos = 5, EstadoDoCacheDeProntidao? estado = null) =>
        new(interno, estado ?? new EstadoDoCacheDeProntidao(),
            Microsoft.Extensions.Options.Options.Create(new OpcoesDaProntidao { ProntidaoCacheSegundos = cacheSegundos }), relogio);

    [Fact]
    public void Opcoes_SemConfiguracao_DevemTerCacheDeCincoSegundos()
    {
        new OpcoesDaProntidao().ProntidaoCacheSegundos.Should().Be(5);
    }

    [Fact]
    public async Task SegundaChamadaDentroDaValidade_DeveDevolverOResultadoGuardadoSemAvaliarDeNovo()
    {
        var interno = new CheckContador();
        var check = Criar(interno, new FakeTimeProvider());

        var primeira = await check.CheckHealthAsync(Contexto);
        var segunda = await check.CheckHealthAsync(Contexto);

        interno.Chamadas.Should().Be(1);
        primeira.Description.Should().Be("avaliação 1");
        segunda.Description.Should().Be("avaliação 1");
    }

    [Fact]
    public async Task AoFimDaValidade_DeveAvaliarDeNovoEGuardarOResultadoNovo()
    {
        var interno = new CheckContador();
        var relogio = new FakeTimeProvider();
        var check = Criar(interno, relogio);
        await check.CheckHealthAsync(Contexto);

        relogio.Advance(TimeSpan.FromSeconds(4.9));
        (await check.CheckHealthAsync(Contexto)).Description.Should().Be("avaliação 1", "ainda dentro dos 5 s");

        relogio.Advance(TimeSpan.FromSeconds(0.1));
        var renovada = await check.CheckHealthAsync(Contexto);
        var seguinte = await check.CheckHealthAsync(Contexto);

        interno.Chamadas.Should().Be(2);
        renovada.Description.Should().Be("avaliação 2");
        seguinte.Description.Should().Be("avaliação 2", "a medição nova também fica guardada");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task ComCacheDesligado_DeveAvaliarEmTodaChamada(int segundos)
    {
        var interno = new CheckContador();
        var check = Criar(interno, new FakeTimeProvider(), segundos);

        await check.CheckHealthAsync(Contexto);
        var segunda = await check.CheckHealthAsync(Contexto);

        interno.Chamadas.Should().Be(2);
        segunda.Description.Should().Be("avaliação 2");
    }

    [Fact]
    public async Task ResultadoUnhealthy_TambemFicaGuardado_EALimitaACargaSobreODependenteForaDoAr()
    {
        var interno = new CheckContador { Resultado = _ => Task.FromResult(HealthCheckResult.Unhealthy("banco fora")) };
        var check = Criar(interno, new FakeTimeProvider());

        await check.CheckHealthAsync(Contexto);
        var segunda = await check.CheckHealthAsync(Contexto);

        interno.Chamadas.Should().Be(1);
        segunda.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task ChamadasSimultaneas_DevemSerRespondidasPelaMesmaAvaliacao()
    {
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interno = new CheckContador
        {
            Resultado = async n =>
            {
                await liberar.Task;
                return HealthCheckResult.Healthy($"avaliação {n}");
            }
        };
        var check = Criar(interno, new FakeTimeProvider());

        var chamadas = Enumerable.Range(0, 8).Select(_ => Task.Run(() => check.CheckHealthAsync(Contexto))).ToArray();
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (interno.Chamadas == 0 && DateTime.UtcNow < limite)
            await Task.Delay(10);
        liberar.SetResult();
        var resultados = await Task.WhenAll(chamadas).AguardarAsync("todas as chamadas simultâneas terminam quando a avaliação é liberada");

        interno.Chamadas.Should().Be(1, "as requisições simultâneas esperam a primeira avaliação em vez de abrir uma conexão cada");
        resultados.Select(r => r.Description).Should().OnlyContain(d => d == "avaliação 1");
    }

    [Fact]
    public async Task ExcecaoDoCheck_DevePropagarENaoSerGuardada()
    {
        var interno = new CheckContador
        {
            Resultado = n => n == 1
                ? throw new OperationCanceledException()
                : Task.FromResult(HealthCheckResult.Healthy($"avaliação {n}"))
        };
        var check = Criar(interno, new FakeTimeProvider());

        var act = () => check.CheckHealthAsync(Contexto);
        await act.Should().ThrowAsync<OperationCanceledException>();
        var seguinte = await check.CheckHealthAsync(Contexto);

        interno.Chamadas.Should().Be(2, "uma medição interrompida não vira resposta guardada");
        seguinte.Description.Should().Be("avaliação 2");
    }

    [Fact]
    public async Task ChamadorQueCancelaEnquantoEsperaAAvaliacaoEmAndamento_DevePropagarOCancelamento()
    {
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interno = new CheckContador
        {
            Resultado = async n =>
            {
                await liberar.Task;
                return HealthCheckResult.Healthy();
            }
        };
        var check = Criar(interno, new FakeTimeProvider());
        var emAndamento = Task.Run(() => check.CheckHealthAsync(Contexto));
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (interno.Chamadas == 0 && DateTime.UtcNow < limite)
            await Task.Delay(10);
        using var cancelamento = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try
        {
            // Sem a espera pela trava respeitar o token, esta chamada entraria na avaliação já em andamento e ficaria presa até
            // alguém liberá-la: o limite faz essa falta virar falha em vez de travar o teste.
            var act = () => check.CheckHealthAsync(Contexto, cancelamento.Token)
                .AguardarAsync("o chamador que cancela deixa de esperar a avaliação em andamento");

            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            liberar.TrySetResult();
        }

        await emAndamento.AguardarAsync("a avaliação em andamento termina quando é liberada");
        interno.Chamadas.Should().Be(1);
    }

    // ---- Registro real ----

    private static ServiceProvider ProvedorComUmCheckEmCache(CheckContador interno, FakeTimeProvider relogio)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(relogio);
        services.AddSingleton(interno);
        services.AddOptions<OpcoesDaProntidao>();
        services.AddHealthChecks().AddCheckComCache<CheckContador>("dependencia", failureStatus: null, tags: new[] { "ready" });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task AddCheckComCache_ComConfiguracaoPadrao_DeveGuardarEntreAvaliacoesDoHealthCheckService()
    {
        var interno = new CheckContador();
        var relogio = new FakeTimeProvider();
        await using var provedor = ProvedorComUmCheckEmCache(interno, relogio);
        var servico = provedor.GetRequiredService<HealthCheckService>();

        await servico.CheckHealthAsync();
        await servico.CheckHealthAsync();
        interno.Chamadas.Should().Be(1, "o estado do cache sobrevive aos escopos que o HealthCheckService cria a cada avaliação");

        relogio.Advance(TimeSpan.FromSeconds(5));
        await servico.CheckHealthAsync();
        interno.Chamadas.Should().Be(2);
    }

    [Fact]
    public async Task AddInfrastructure_DeveLerOCacheDaSecaoHealth()
    {
        async Task<int> LerAsync(params (string, string?)[] valores)
        {
            var configuracao = ConfiguracaoDeTeste.Criar(valores.Append(("ConnectionStrings:DefaultConnection", ConfiguracaoDeTeste.ConexaoInerte))
                .Append(("Pagamento:Provider", "Fake")).Append(("Fake:WebhookSecret", "segredo-fake")).ToArray());
            var services = new ServiceCollection();
            services.AddSingleton(ConfiguracaoDeTeste.Ambiente("Development"));
            services.AddLogging();
            services.AddInfrastructure(configuracao);
            await using var provedor = services.BuildServiceProvider();
            return provedor.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpcoesDaProntidao>>().Value.ProntidaoCacheSegundos;
        }

        (await LerAsync()).Should().Be(5);
        (await LerAsync(("Health:ProntidaoCacheSegundos", "0"))).Should().Be(0);
        (await LerAsync(("Health:ProntidaoCacheSegundos", "12"))).Should().Be(12);
    }
}
