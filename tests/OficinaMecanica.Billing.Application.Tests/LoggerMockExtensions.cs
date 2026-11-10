using Microsoft.Extensions.Logging;

namespace OficinaMecanica.Billing.Application.Tests;

internal static class LoggerMockExtensions
{
    public static void VerificarLog<T>(this Mock<ILogger<T>> logger, LogLevel nivel, string trecho, Times? vezes = null) =>
        logger.Verify(
            l => l.Log(
                nivel,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((estado, _) => estado.ToString()!.Contains(trecho)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            vezes ?? Times.Once());
}
