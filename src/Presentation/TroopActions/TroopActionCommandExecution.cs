using System;
using System.Windows.Input;

namespace APIShared.Presentation
{
    internal static class TroopActionCommandExecution
    {
        // CanExecute is external code: it may change visibility, selection or the current HUD.
        // A second authorization therefore precedes Execute, not merely the CanExecute call.
        internal static bool TryInvoke(ICommand command, object parameter, Func<bool> authorized,
            Action completed, Action<Exception> failed)
        {
            try
            {
                if (!authorized() || !command.CanExecute(parameter) || !authorized()) return false;
                command.Execute(parameter);
                completed?.Invoke();
                return true;
            }
            catch (Exception error) { failed?.Invoke(error); return false; }
        }
    }
}
