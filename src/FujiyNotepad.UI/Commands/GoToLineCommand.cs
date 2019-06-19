using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace FujiyNotepad.UI.Commands
{
    //TODO precisa de um por comando? Só é usado para criar os atalhos e o mesmo é usado para o Find Text
    public class GoToLineCommand : ICommand
    {
        public event EventHandler<object> Executed;

        public bool CanExecute(object parameter)
        {
            return true;
        }

        public void Execute(object parameter)
        {
            Executed?.Invoke(this, parameter);
        }

        public event EventHandler CanExecuteChanged;
    }
}
