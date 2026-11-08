using System.Reflection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using OficinaMecanica.Billing.API.Controllers;
using OficinaMecanica.Billing.API.Health;
using OficinaMecanica.Billing.API.Middleware;
using OficinaMecanica.Billing.Application;
using OficinaMecanica.Billing.Infrastructure;
using OficinaMecanica.Billing.Infrastructure.Data;
using OficinaMecanica.Billing.Infrastructure.Messaging;
using OficinaMecanica.Billing.Infrastructure.Observability;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

const string NomeDoServico = "OficinaMecanica.Billing";

var builder = WebApplication.CreateBuilder(args);

var otelEndpoint = builder.Configuration["Otel:Endpoint"];

var loggerConfig = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console();

// trace_id/span_id são populados pelo sink a partir do Activity.Current, sem enricher adicional.
if (!string.IsNullOrWhiteSpace(otelEndpoint))
{
    loggerConfig = loggerConfig.WriteTo.OpenTelemetry(options =>
    {
        options.Endpoint = otelEndpoint;
        options.Protocol = OtlpProtocol.Grpc;
        options.ResourceAttributes = new Dictionary<string, object> { ["service.name"] = NomeDoServico };
    });
}

Log.Logger = loggerConfig.CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddMensageria(builder.Configuration);

if (!string.IsNullOrWhiteSpace(otelEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(NomeDoServico))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSqlClientInstrumentation()
            // O MassTransit emite as spans de publish/consume; com isso o trace atravessa as filas.
            .AddSource("MassTransit")
            .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otelEndpoint)))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter(MetricasDoBilling.NomeDoMeter)
            .AddMeter("MassTransit")
            .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otelEndpoint)));
}

// A simulação de pagamento só existe com o gateway Fake em Development. A decisão lê o ambiente e a configuração
// finais do host (a aplicação construída), porque o MVC só descobre os controllers depois do Build.
WebApplication? aplicacaoConstruida = null;
bool SimulacaoDePagamentoHabilitada() =>
    aplicacaoConstruida is not null
    && aplicacaoConstruida.Environment.IsDevelopment()
    && string.Equals(aplicacaoConstruida.Configuration["Pagamento:Provider"], "Fake", StringComparison.OrdinalIgnoreCase);

builder.Services.AddControllers()
    .ConfigureApplicationPartManager(partes =>
        partes.FeatureProviders.Add(new ControllersDeDevFeatureProvider(SimulacaoDePagamentoHabilitada)));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "OficinaMecanica Billing API",
        Version = "v1",
        Description = "Orçamento e pagamento (Mercado Pago Checkout Pro) da oficina mecânica. " +
                      "Participa da saga orquestrada pelo OS Service por mensagens (RabbitMQ); esta API expõe a consulta, " +
                      "o webhook do Mercado Pago e a ação de staff sobre estornos."
    });

    // IDs de operação únicos por controller+action: o import do OpenAPI na APIM casa por operationId para saber
    // o que atualizar e o que apagar, e nomes genéricos ficam instáveis em reimports.
    c.CustomOperationIds(apiDesc =>
        $"{apiDesc.ActionDescriptor.RouteValues["controller"]}_{apiDesc.ActionDescriptor.RouteValues["action"]}");

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT emitido pelo OficinaMecanica.Seguranca. Informe somente o token.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });

    var arquivoXml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(arquivoXml))
        c.IncludeXmlComments(arquivoXml);
});

var app = builder.Build();
aplicacaoConstruida = app;

// X-Forwarded-Proto/-Host só são lidos se este middleware confiar neles; sem ele o Scheme/Host refletem o que o pod
// recebe (http, IP do ingress), não o que o cliente usou para chegar na APIM. KnownNetworks/KnownProxies ficam
// vazios porque o IP de quem repassa (pod do ingress-nginx) não é fixo.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
};
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

// O prefixo vem do header X-Forwarded-Prefix que a APIM injeta por policy; sem ajustar o PathBase os links
// absolutos do Swagger saem sem ele.
app.Use((context, next) =>
{
    var prefixo = context.Request.Headers["X-Forwarded-Prefix"].FirstOrDefault();
    if (!string.IsNullOrEmpty(prefixo))
        context.Request.PathBase = new PathString(prefixo);
    return next();
});

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseHttpMetrics();

// Migration automática só em Development: em produção o CI aplica as migrations (como no monolito), e uma
// réplica nunca deve alterar o esquema por conta própria.
if (app.Environment.IsDevelopment())
{
    using var escopo = app.Services.CreateScope();
    var db = escopo.ServiceProvider.GetRequiredService<BillingDbContext>();
    if (db.Database.IsRelational())
        db.Database.Migrate();
    else
        db.Database.EnsureCreated();
}

app.UseSwagger(c =>
{
    // servers[] calculado por requisição: reflete o PathBase, então o spec e o "Try it out" apontam para o prefixo
    // certo tanto direto quanto atrás da APIM.
    c.PreSerializeFilters.Add((swaggerDoc, httpReq) =>
    {
        swaggerDoc.Servers = new List<OpenApiServer>
        {
            new() { Url = $"{httpReq.Scheme}://{httpReq.Host}{httpReq.PathBase}" }
        };
    });
});
app.UseSwaggerUI(c =>
{
    // Endpoint relativo: resolve tanto direto quanto com o PathBase da APIM, sem conhecer o prefixo.
    c.SwaggerEndpoint("swagger/v1/swagger.json", "OficinaMecanica Billing API v1");
    c.RoutePrefix = string.Empty;
});

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapMetrics();
// /health só confirma que o processo está de pé (probe de liveness: uma queda de dependência não pode fazer o Kubernetes
// reiniciar o pod). /health/ready (tag "ready") cobre as dependências: o SQL Server, sem o qual a API não aceita escrita,
// devolve 503 quando cai; o RabbitMQ (conexão AMQP de verdade) devolve Degraded, que continua 200 porque o outbox segura as
// mensagens até o broker voltar. O alerta do broker fica por métrica.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = check => !check.Tags.Contains("ready") });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = ProntidaoResponseWriter.EscreverAsync
});

// Só depois de o host realmente subir: um ValidateOnStart que derruba a partida não pode ter deixado antes uma
// mensagem de sucesso no log.
app.Lifetime.ApplicationStarted.Register(() => Log.Information("OficinaMecanica.Billing API iniciada com sucesso."));

app.Run();

// Necessário para os testes de integração (WebApplicationFactory).
public partial class Program { }
