namespace BeanBot.Commands;

/// <summary>
/// Abstraction implemented by BeanBot.Orchestrator.Fleet so BeanBot.Commands (CommandSurface / HttpControlServer)
/// can control and observe a 15-bot fleet without a circular project dependency.
/// </summary>
public interface IFleetControlTarget : IAsyncDisposable
{
    string CurrentLobbyCode { get; }
    object GetFleetStatus();
    IReadOnlyList<BotClient> GetBots();
    BotClient? ResolveBot(string botIdOrName);
    Task<string> StopBotOrAllAsync(string? botIdOrAll, CancellationToken ct = default);
}
