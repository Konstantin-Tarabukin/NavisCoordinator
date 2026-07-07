using Autodesk.Navisworks.Api.Plugins;
using System.Windows.Forms;

namespace NavisCoordinator
{
    [Plugin(
        "NavisCoordinator",
        "CT",
        DisplayName = "Navis Coordinator")]
    public class Plugin : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            MessageBox.Show(
                "Плагин успешно загружен!",
                "Navis Coordinator");

            return 0;
        }
    }
}