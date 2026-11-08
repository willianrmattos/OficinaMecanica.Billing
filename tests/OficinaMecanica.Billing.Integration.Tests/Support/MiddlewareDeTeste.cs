using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.API.Middleware;

namespace OficinaMecanica.Billing.Integration.Tests.Support;

// Apoio dos testes do ExceptionHandlingMiddleware: contexto sem servidor, endpoint com template de rota, leitura do corpo e
// dos logs registrados num Mock<ILogger>.
public static class MiddlewareDeTeste
{
    public static DefaultHttpContext NovoContexto(CancellationToken requisicaoAbortada = default)
    {
        var contexto = new DefaultHttpContext { RequestAborted = requisicaoAbortada };
        contexto.Response.Body = new MemoryStream();
        return contexto;
    }

    // O roteamento do ASP.NET grava o endpoint no contexto antes de qualquer middleware; aqui faço o mesmo à mão.
    public static void DefinirRota(DefaultHttpContext contexto, string template) =>
        contexto.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask, RoutePatternFactory.Parse(template), 0, EndpointMetadataCollection.Empty, "rota de teste"));

    public static List<(LogLevel Nivel, string Mensagem, Exception? Excecao)> Registros(Mock<ILogger<ExceptionHandlingMiddleware>> logger) =>
        logger.Invocations
            .Where(i => i.Method.Name == nameof(ILogger.Log))
            .Select(i => ((LogLevel)i.Arguments[0], i.Arguments[2].ToString()!, (Exception?)i.Arguments[3]))
            .ToList();

    public static string CorpoEscrito(DefaultHttpContext contexto)
    {
        contexto.Response.Body.Position = 0;
        return new StreamReader(contexto.Response.Body).ReadToEnd();
    }

    // Reproduz o Kestrel com a resposta já enviada: mudar o status ou os cabeçalhos lança InvalidOperationException (o
    // HttpResponseFeature padrão deixaria mudar e esconderia o defeito de um middleware que mexe na resposta).
    public sealed class RespostaJaIniciada : IHttpResponseFeature
    {
        public int StatusCode
        {
            get => StatusCodes.Status200OK;
            set => throw new InvalidOperationException("StatusCode cannot be set because the response has already started.");
        }

        public string? ReasonPhrase
        {
            get => null;
            set => throw new InvalidOperationException("ReasonPhrase cannot be set because the response has already started.");
        }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary { IsReadOnly = true };

        public Stream Body { get; set; } = new MemoryStream();

        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state) =>
            throw new InvalidOperationException("OnStarting cannot be set because the response has already started.");

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }

    // Conexão de teste: Abort() cancela o RequestAborted (como o Kestrel faz) e fica registrado; o cliente que fecha por conta
    // própria cancela o mesmo token sem passar pelo Abort(). O feature padrão do DefaultHttpContext lança no Abort().
    public sealed class ConexaoQueRegistraAbort : IHttpRequestLifetimeFeature
    {
        private readonly CancellationTokenSource _fonte = new();

        public CancellationToken RequestAborted
        {
            get => _fonte.Token;
            set => throw new NotSupportedException();
        }

        public int Abortes { get; private set; }

        public void Abort()
        {
            Abortes++;
            _fonte.Cancel();
        }

        public void ClienteFechou() => _fonte.Cancel();
    }
}
