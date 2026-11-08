using Serilog;

namespace Demenageur.App.Services;

/// <summary>Tâches lancées sans attente : leurs erreurs sont journalisées, jamais perdues en silence.</summary>
internal static class BackgroundTasks
{
    public static async void LogFailures(this Task task, string what)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Annulation voulue (nouvelle actualisation, fermeture) : rien à signaler.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Échec en arrière-plan : {What}", what);
        }
    }
}
