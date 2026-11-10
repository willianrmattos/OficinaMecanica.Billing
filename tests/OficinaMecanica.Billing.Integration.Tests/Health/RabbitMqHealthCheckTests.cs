using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;
using OficinaMecanica.Billing.Infrastructure.Messaging;
using OficinaMecanica.Billing.Infrastructure.Options;
using OficinaMecanica.Billing.Integration.Tests.Support;
using RabbitMQ.Client;

namespace OficinaMecanica.Billing.Integration.Tests.Health;

// Sem Docker e sem broker: a fábrica de conexão do check é trocada para controlar QUANDO a conexão completa, o que o cliente
// real não reproduz de forma determinística. Os testes contra servidor mudo e porta fechada ficam em ChecksDeProntidaoTests.
// Toda espera que depende da tarefa pendente passa por AguardarAsync: sem o limite do check, a conexão que nunca completa
// travaria o teste em vez de reprová-lo.
public class RabbitMqHealthCheckTests
{
    private static readonly HealthCheckContext Contexto = new()
    {
        Registration = new HealthCheckRegistration("teste", _ => null!, null, null)
    };

    private static RabbitMqHealthCheck CriarCheck(Func<ConnectionFactory, CancellationToken, Task<IConnection>> conectar) =>
        new(Microsoft.Extensions.Options.Options.Create(new OpcoesDoRabbitMq { Host = "127.0.0.1", Port = 5672, Username = "svc", Password = "x" }), conectar);

    private static Mock<IConnection> ConexaoQueAvisaQuandoFechada(TaskCompletionSource fechada)
    {
        var conexao = new Mock<IConnection>();
        conexao.Setup(c => c.DisposeAsync()).Returns(() =>
        {
            fechada.TrySetResult();
            return ValueTask.CompletedTask;
        });
        return conexao;
    }

    private static TaskCompletionSource NovoSinal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task<HealthCheckResult> AvaliarAsync(RabbitMqHealthCheck check, CancellationToken cancellationToken = default) =>
        check.CheckHealthAsync(Contexto, cancellationToken)
            .AguardarAsync("o check desiste da conexão pendente no limite de 3 s, sem esperar a tarefa do cliente");

    private static Task DeveTerFechadoAsync(TaskCompletionSource fechada, string porque) => fechada.Task.AguardarAsync(porque);

    [Fact]
    public async Task ConexaoEstabelecidaDentroDoLimite_DeveResponderHealthyEFecharAConexao()
    {
        var fechada = NovoSinal();
        var conexao = ConexaoQueAvisaQuandoFechada(fechada);
        var check = CriarCheck((_, _) => Task.FromResult(conexao.Object));

        var resultado = await AvaliarAsync(check);

        resultado.Status.Should().Be(HealthStatus.Healthy);
        fechada.Task.IsCompleted.Should().BeTrue("o check abre a conexão só para provar o broker e a fecha");
        conexao.Verify(c => c.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task ConexaoQueCompletaDepoisDoLimite_DeveSerFechadaEmVezDeVazar()
    {
        var fechada = NovoSinal();
        var conexao = ConexaoQueAvisaQuandoFechada(fechada);
        var tardia = new TaskCompletionSource<IConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = CriarCheck((_, _) => tardia.Task);

        var resultado = await AvaliarAsync(check);
        resultado.Status.Should().Be(HealthStatus.Degraded, "a conexão ainda não tinha completado no limite");
        fechada.Task.IsCompleted.Should().BeFalse();

        tardia.SetResult(conexao.Object);

        await DeveTerFechadoAsync(fechada, "a conexão que chega depois do limite precisa ser fechada, senão vaza uma conexão por avaliação");
        conexao.Verify(c => c.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task TentativaTardiaQueFalha_NaoDeveAlterarOResultadoNemLancar()
    {
        var tardia = new TaskCompletionSource<IConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = CriarCheck((_, _) => tardia.Task);

        var resultado = await AvaliarAsync(check);
        tardia.SetException(new IOException("conexão recusada depois do limite"));
        await Task.Delay(100);

        resultado.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task CancelamentoDoChamadorComConexaoPendente_DevePropagarEFecharAConexaoQueCompletarDepois()
    {
        var fechada = NovoSinal();
        var conexao = ConexaoQueAvisaQuandoFechada(fechada);
        var tardia = new TaskCompletionSource<IConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = CriarCheck((_, _) => tardia.Task);
        using var chamador = new CancellationTokenSource();

        var avaliacao = AvaliarAsync(check, chamador.Token);
        await chamador.CancelAsync();

        await avaliacao.Awaiting(a => a).Should().ThrowAsync<OperationCanceledException>("quem cancelou foi a probe, não o broker");
        fechada.Task.IsCompleted.Should().BeFalse();

        tardia.SetResult(conexao.Object);

        await DeveTerFechadoAsync(fechada, "a tentativa abandonada pelo cancelamento do chamador também precisa ser fechada se ainda conectar");
        conexao.Verify(c => c.DisposeAsync(), Times.Once);
    }

    // ---- Exceção da tentativa abandonada: observada, nunca "sem tratamento" ----
    //
    // Uma Task que falha e nunca é aguardada publica TaskScheduler.UnobservedTaskException quando o coletor a finaliza. O teste
    // fabrica a tentativa tardia que falha dentro de um método separado (para nada dela ficar alcançável a partir do teste),
    // força a coleta e confere que NENHUM evento citou a exceção fabricada. O filtro é pela instância da exceção: um teste em
    // paralelo com outra exceção não interfere. É determinístico: o finalizador do TaskExceptionHolder roda no
    // WaitForPendingFinalizers e publica o evento de forma síncrona.

    private static async Task<bool> ExcecaoTardiaFicouSemObservadorAsync(Func<Exception, Task> cenario)
    {
        var excecao = new IOException("conexão recusada depois do abandono");
        var vista = 0;

        void Observador(object? remetente, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Contains(excecao)) Interlocked.Exchange(ref vista, 1);
        }

        TaskScheduler.UnobservedTaskException += Observador;
        try
        {
            await cenario(excecao).AguardarAsync("o cenário da tentativa tardia termina");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            return Volatile.Read(ref vista) == 1;
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Observador;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AbandonarPorLimiteEFalharDepoisAsync(Exception excecao)
    {
        var tardia = new TaskCompletionSource<IConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = CriarCheck((_, _) => tardia.Task);

        await AvaliarAsync(check);
        tardia.SetException(excecao);
        await Task.Delay(100);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AbandonarPorCancelamentoEFalharDepoisAsync(Exception excecao)
    {
        var tardia = new TaskCompletionSource<IConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = CriarCheck((_, _) => tardia.Task);
        using var chamador = new CancellationTokenSource();

        var avaliacao = AvaliarAsync(check, chamador.Token);
        await chamador.CancelAsync();
        try
        {
            await avaliacao;
        }
        catch (OperationCanceledException)
        {
            // Esperado: o cancelamento do chamador sobe.
        }

        tardia.SetException(excecao);
        await Task.Delay(100);
    }

    [Fact]
    public async Task TentativaTardiaQueFalhaDepoisDoLimite_DeveTerAExcecaoObservada()
    {
        var semObservador = await ExcecaoTardiaFicouSemObservadorAsync(AbandonarPorLimiteEFalharDepoisAsync);

        semObservador.Should().BeFalse("a falha da tentativa abandonada precisa ser observada, senão vira UnobservedTaskException");
    }

    [Fact]
    public async Task TentativaTardiaQueFalhaDepoisDoCancelamentoDoChamador_DeveTerAExcecaoObservada()
    {
        var semObservador = await ExcecaoTardiaFicouSemObservadorAsync(AbandonarPorCancelamentoEFalharDepoisAsync);

        semObservador.Should().BeFalse("a tentativa abandonada pelo cancelamento do chamador também precisa ter a falha observada");
    }
}
