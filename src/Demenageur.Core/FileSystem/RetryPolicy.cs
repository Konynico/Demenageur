namespace Demenageur.Core.FileSystem;

/// <summary>
/// Réessais espacés sur les erreurs passagères (antivirus ou indexeur qui ouvre brièvement un fichier).
/// </summary>
public sealed class RetryPolicy
{
    private readonly TimeSpan[] _delays;

    public RetryPolicy(params TimeSpan[] delays)
    {
        _delays = delays;
    }

    /// <summary>5 tentatives sur environ 2 secondes.</summary>
    public static RetryPolicy Default { get; } = new(
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1200));

    /// <summary>Aucun réessai (tests d'injection de pannes).</summary>
    public static RetryPolicy None { get; } = new();

    public int MaxAttempts => _delays.Length + 1;

    public T Execute<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return action();
            }
            catch (FileSystemException ex) when (ex.IsTransient && attempt < _delays.Length)
            {
                if (cancellationToken.WaitHandle.WaitOne(_delays[attempt]))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
    }

    public void Execute(Action action, CancellationToken cancellationToken = default)
        => Execute<object?>(() =>
        {
            action();
            return null;
        }, cancellationToken);
}
