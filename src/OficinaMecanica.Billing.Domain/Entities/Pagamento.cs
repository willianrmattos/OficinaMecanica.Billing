using OficinaMecanica.Billing.Domain.Common;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;

namespace OficinaMecanica.Billing.Domain.Entities;

public class Pagamento : AggregateRoot
{
    private const string MoedaEsperada = "BRL";

    public Guid OrcamentoId { get; private set; }
    public Guid OrdemDeServicoId { get; private set; }
    public decimal Valor { get; private set; }
    public StatusPagamento Status { get; private set; }
    public string? PreferenceId { get; private set; }
    public string? LinkPagamento { get; private set; }
    public DateTimeOffset ExpiraEm { get; private set; }
    public DateTimeOffset CriadoEm { get; private set; }
    public string? GatewayPaymentIdAprovado { get; private set; }
    public DateTimeOffset? AprovadoEm { get; private set; }

    // Aprovação tardia: o gateway aprovou depois de o pagamento já ter sido cancelado ou expirado,
    // então o cliente pagou por algo que a saga já desfez e o valor precisa voltar.
    public bool AprovacaoTardia { get; private set; }
    public string? MotivoCancelamento { get; private set; }
    public Estorno? Estorno { get; private set; }

    private readonly List<TentativaPagamento> _tentativas = new();
    public IReadOnlyCollection<TentativaPagamento> Tentativas => _tentativas.AsReadOnly();

    private Pagamento() { } // EF Core

    public Pagamento(Guid orcamentoId, Guid ordemDeServicoId, decimal valor, DateTimeOffset expiraEm, DateTimeOffset agora)
    {
        if (orcamentoId == Guid.Empty)
            throw new DomainException("O orçamento do pagamento é obrigatório.");

        if (ordemDeServicoId == Guid.Empty)
            throw new DomainException("A ordem de serviço do pagamento é obrigatória.");

        if (valor <= 0)
            throw new DomainException("O valor do pagamento deve ser maior que zero.");

        if (valor > LimitesDeValor.ValorTotalMaximo)
            throw new DomainException("O valor do pagamento excede o limite permitido.");

        if (ValorMonetario.TemMaisDeDuasCasas(valor))
            throw new DomainException("O valor do pagamento deve ter no máximo duas casas decimais.");

        if (expiraEm <= agora)
            throw new DomainException("O prazo do pagamento deve estar no futuro.");

        OrcamentoId = orcamentoId;
        OrdemDeServicoId = ordemDeServicoId;
        Valor = valor;
        ExpiraEm = expiraEm;
        CriadoEm = agora;
        Status = StatusPagamento.Pendente;
    }

    public void DefinirPreferencia(string preferenceId, string linkPagamento)
    {
        if (Status != StatusPagamento.Pendente)
            throw new DomainException($"Só é possível definir a preferência de um pagamento pendente. Status atual: '{Status}'.");

        if (string.IsNullOrWhiteSpace(preferenceId))
            throw new DomainException("O identificador da preferência de pagamento é obrigatório.");

        if (string.IsNullOrWhiteSpace(linkPagamento))
            throw new DomainException("O link de pagamento é obrigatório.");

        PreferenceId = preferenceId;
        LinkPagamento = linkPagamento;
    }

    public void RegistrarTentativa(
        string gatewayPaymentId,
        string statusGateway,
        string? statusDetalhe,
        decimal valorGateway,
        DateTimeOffset em)
    {
        if (string.IsNullOrWhiteSpace(gatewayPaymentId))
            throw new DomainException("O identificador do pagamento no gateway é obrigatório.");

        if (string.IsNullOrWhiteSpace(statusGateway))
            throw new DomainException("O status do pagamento no gateway é obrigatório.");

        var existente = _tentativas.FirstOrDefault(t => t.GatewayPaymentId == gatewayPaymentId);
        if (existente is null)
            _tentativas.Add(new TentativaPagamento(gatewayPaymentId, statusGateway, statusDetalhe, valorGateway, em));
        else
            existente.Atualizar(statusGateway, statusDetalhe, valorGateway, em);
    }

    public ResultadoAprovacao ConfirmarAprovacao(
        string gatewayPaymentId,
        decimal valorGateway,
        string moeda,
        string? externalReference,
        DateTimeOffset em)
    {
        ValidarDadosDoGateway(gatewayPaymentId, valorGateway, moeda, externalReference);

        // Um segundo pagamento aprovado no gateway para o mesmo Pagamento é cobrança em duplicidade:
        // não troco o id já registrado e devolvo um resultado próprio para o chamador sinalizar.
        if (GatewayPaymentIdAprovado is not null && GatewayPaymentIdAprovado != gatewayPaymentId)
            return ResultadoAprovacao.AprovacaoDuplicada;

        switch (Status)
        {
            case StatusPagamento.Pendente:
                Status = StatusPagamento.Aprovado;
                GatewayPaymentIdAprovado = gatewayPaymentId;
                AprovadoEm = em;
                return ResultadoAprovacao.Aprovado;

            case StatusPagamento.Cancelado:
            case StatusPagamento.Expirado:
                GatewayPaymentIdAprovado ??= gatewayPaymentId;
                AprovadoEm ??= em;
                AprovacaoTardia = true;
                return ResultadoAprovacao.AprovadoTardio;

            case StatusPagamento.Estornando:
            case StatusPagamento.EstornoFalhou:
                // Estorno de aprovação tardia ainda não concluído: devolver AprovadoTardio de novo é o que
                // permite retomar o estorno na reentrega do webhook.
                return AprovacaoTardia ? ResultadoAprovacao.AprovadoTardio : ResultadoAprovacao.JaAprovado;

            default:
                return ResultadoAprovacao.JaAprovado;
        }
    }

    public ResultadoCancelamentoPagamento Cancelar(string motivo)
    {
        switch (Status)
        {
            case StatusPagamento.Pendente:
                Status = StatusPagamento.Cancelado;
                MotivoCancelamento = motivo;
                return ResultadoCancelamentoPagamento.Cancelado;

            case StatusPagamento.Cancelado:
            case StatusPagamento.Expirado:
                return ResultadoCancelamentoPagamento.JaCancelado;

            default:
                return AprovacaoTardia
                    ? ResultadoCancelamentoPagamento.JaCancelado
                    : ResultadoCancelamentoPagamento.JaAprovado;
        }
    }

    public void Expirar(DateTimeOffset agora)
    {
        if (Status == StatusPagamento.Expirado)
            return;

        if (Status != StatusPagamento.Pendente)
            throw new DomainException($"Só é possível expirar um pagamento pendente. Status atual: '{Status}'.");

        if (agora < ExpiraEm)
            throw new DomainException("O pagamento ainda está dentro do prazo.");

        Status = StatusPagamento.Expirado;
    }

    // Devolve o id do pagamento aprovado no gateway porque é exatamente ele que o chamador precisa
    // estornar; chamar de novo em Estornando retoma um estorno interrompido sem criar outro Estorno.
    public string IniciarEstorno()
    {
        if (Status == StatusPagamento.Estornado)
            throw new DomainException("O pagamento já foi estornado.");

        var gatewayPaymentId = GatewayPaymentIdAprovado
            ?? throw new DomainException("Só é possível estornar um pagamento que foi aprovado no gateway.");

        Status = StatusPagamento.Estornando;
        if (Estorno is null)
            Estorno = new Estorno();
        else
            Estorno.Reabrir();

        return gatewayPaymentId;
    }

    public void ConfirmarEstorno(string gatewayRefundId)
    {
        if (Status == StatusPagamento.Estornado)
            return;

        if (Status != StatusPagamento.Estornando)
            throw new DomainException($"Só é possível confirmar o estorno de um pagamento em estorno. Status atual: '{Status}'.");

        if (string.IsNullOrWhiteSpace(gatewayRefundId))
            throw new DomainException("O identificador do estorno no gateway é obrigatório.");

        EstornoEmAndamento.Concluir(gatewayRefundId);
        Status = StatusPagamento.Estornado;
    }

    public void FalharEstorno(string erro)
    {
        if (Status != StatusPagamento.Estornando)
            throw new DomainException($"Só é possível registrar falha de estorno em um pagamento em estorno. Status atual: '{Status}'.");

        EstornoEmAndamento.RegistrarFalha(erro);
        Status = StatusPagamento.EstornoFalhou;
    }

    private Estorno EstornoEmAndamento =>
        Estorno ?? throw new DomainException("O estorno do pagamento não foi iniciado.");

    private void ValidarDadosDoGateway(string gatewayPaymentId, decimal valorGateway, string moeda, string? externalReference)
    {
        if (string.IsNullOrWhiteSpace(gatewayPaymentId))
            throw new DomainException("O identificador do pagamento no gateway é obrigatório.");

        if (!Guid.TryParse(externalReference, out var referencia) || referencia != Id)
            throw new DomainException("A referência externa do gateway não corresponde a este pagamento.");

        if (!string.Equals(moeda, MoedaEsperada, StringComparison.OrdinalIgnoreCase))
            throw new DomainException($"A moeda do pagamento no gateway deve ser '{MoedaEsperada}'.");

        if (valorGateway != Valor)
            throw new DomainException("O valor aprovado no gateway é diferente do valor do pagamento.");
    }
}
